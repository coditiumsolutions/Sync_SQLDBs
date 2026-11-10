using System.Diagnostics;
using System.Text;
using Dapper;
using DatabaseSync.Core.DTOs;
using DatabaseSync.Core.Interfaces;
using DatabaseSync.Core.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace DatabaseSync.Infrastructure.Services;

public sealed class TableSyncService : ITableSyncService
{
    private readonly ISqlConnectionFactory _connectionFactory;
    private readonly ISyncConfigStore _configStore;
    private readonly ILogger<TableSyncService> _logger;

    public TableSyncService(
        ISqlConnectionFactory connectionFactory,
        ISyncConfigStore configStore,
        ILogger<TableSyncService> logger)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _configStore = configStore ?? throw new ArgumentNullException(nameof(configStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<TableSyncResult> SyncTableAsync(
        string sourceConnectionId,
        string targetConnectionId,
        TableSchema table,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var target = _connectionFactory.GetConnection(targetConnectionId);
        var settings = _configStore.Get().SyncSettings;
        var timeout = settings.CommandTimeoutSeconds;

        try
        {
            var copyable = table.CopyableColumns;
            var primaryKeys = table.PrimaryKeyColumns;

            if (copyable.Count == 0 || primaryKeys.Count == 0)
            {
                return Fail(table, target, stopwatch, "Table has no copyable columns or primary key.");
            }

            await using var sourceConnection = (SqlConnection)_connectionFactory.CreateConnection(sourceConnectionId);
            await using var targetConnection = (SqlConnection)_connectionFactory.CreateConnection(targetConnectionId);

            await sourceConnection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await targetConnection.OpenAsync(cancellationToken).ConfigureAwait(false);

            const string stagingName = "#SyncStaging";

            await targetConnection.ExecuteAsync(new CommandDefinition(
                $"""
                 IF OBJECT_ID('tempdb..{stagingName}') IS NOT NULL DROP TABLE {stagingName};
                 SELECT TOP 0 * INTO {stagingName} FROM {table.FullName};
                 """,
                commandTimeout: timeout,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

            var selectList = string.Join(", ", copyable.Select(c => Quote(c.Name)));
            var selectSql = $"SELECT {selectList} FROM {table.FullName};";

            long rowsRead;
            await using (var sourceCommand = sourceConnection.CreateCommand())
            {
                sourceCommand.CommandText = selectSql;
                sourceCommand.CommandTimeout = timeout;

                await using var reader = await sourceCommand
                    .ExecuteReaderAsync(cancellationToken)
                    .ConfigureAwait(false);

                using var bulkCopy = new SqlBulkCopy(
                    targetConnection,
                    SqlBulkCopyOptions.KeepIdentity | SqlBulkCopyOptions.CheckConstraints,
                    null)
                {
                    DestinationTableName = stagingName,
                    BulkCopyTimeout = timeout,
                    BatchSize = 5000
                };

                foreach (var column in copyable)
                {
                    bulkCopy.ColumnMappings.Add(column.Name, column.Name);
                }

                await bulkCopy.WriteToServerAsync(reader, cancellationToken).ConfigureAwait(false);
                rowsRead = bulkCopy.RowsCopied;
            }

            if (rowsRead <= 0)
            {
                rowsRead = await targetConnection.ExecuteScalarAsync<long>(new CommandDefinition(
                    $"SELECT COUNT_BIG(1) FROM {stagingName};",
                    commandTimeout: timeout,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);
            }

            var mergeSql = BuildMergeSql(table, stagingName, copyable, primaryKeys);

            if (table.HasIdentity)
            {
                await targetConnection.ExecuteAsync(new CommandDefinition(
                    $"SET IDENTITY_INSERT {table.FullName} ON;",
                    commandTimeout: timeout,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);
            }

            long affected;
            try
            {
                affected = await targetConnection.ExecuteAsync(new CommandDefinition(
                    mergeSql,
                    commandTimeout: timeout,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);
            }
            finally
            {
                if (table.HasIdentity)
                {
                    await targetConnection.ExecuteAsync(new CommandDefinition(
                        $"SET IDENTITY_INSERT {table.FullName} OFF;",
                        commandTimeout: timeout,
                        cancellationToken: cancellationToken)).ConfigureAwait(false);
                }
            }

            await targetConnection.ExecuteAsync(new CommandDefinition(
                $"DROP TABLE {stagingName};",
                commandTimeout: timeout,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

            stopwatch.Stop();

            if (settings.EnableDetailedLogging)
            {
                _logger.LogInformation(
                    "Synced {Table} => {TargetName}. SourceRows={RowsRead}, MergeAffected={Affected}, ElapsedMs={ElapsedMs}",
                    table.FullName,
                    target.Name,
                    rowsRead,
                    affected,
                    stopwatch.ElapsedMilliseconds);
            }

            return new TableSyncResult
            {
                SchemaName = table.SchemaName,
                TableName = table.TableName,
                TargetConnectionId = target.Id,
                TargetConnectionName = target.Name,
                Success = true,
                RowsReadFromSource = rowsRead,
                RowsAffectedOnTarget = affected,
                Elapsed = stopwatch.Elapsed
            };
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogError(ex, "Failed syncing {Schema}.{Table} to {TargetName}.", table.SchemaName, table.TableName, target.Name);
            return Fail(table, target, stopwatch, ex.Message);
        }
    }

    private static string BuildMergeSql(
        TableSchema table,
        string stagingName,
        IReadOnlyList<ColumnSchema> copyable,
        IReadOnlyList<ColumnSchema> primaryKeys)
    {
        var onClause = string.Join(" AND ", primaryKeys.Select(pk => $"target.{Quote(pk.Name)} = source.{Quote(pk.Name)}"));
        var updateColumns = copyable.Where(c => !c.IsPrimaryKey).ToList();

        var sb = new StringBuilder();
        sb.AppendLine($"MERGE {table.FullName} AS target");
        sb.AppendLine($"USING {stagingName} AS source");
        sb.AppendLine($"ON ({onClause})");

        if (updateColumns.Count > 0)
        {
            var setClause = string.Join(", ", updateColumns.Select(c => $"target.{Quote(c.Name)} = source.{Quote(c.Name)}"));
            sb.AppendLine("WHEN MATCHED THEN");
            sb.AppendLine($"  UPDATE SET {setClause}");
        }

        var insertCols = string.Join(", ", copyable.Select(c => Quote(c.Name)));
        var insertVals = string.Join(", ", copyable.Select(c => $"source.{Quote(c.Name)}"));
        sb.AppendLine("WHEN NOT MATCHED BY TARGET THEN");
        sb.AppendLine($"  INSERT ({insertCols}) VALUES ({insertVals})");
        sb.AppendLine("WHEN NOT MATCHED BY SOURCE THEN");
        sb.AppendLine("  DELETE");
        sb.AppendLine(";");

        return sb.ToString();
    }

    private static TableSyncResult Fail(
        TableSchema table,
        SyncConnection target,
        Stopwatch stopwatch,
        string error) =>
        new()
        {
            SchemaName = table.SchemaName,
            TableName = table.TableName,
            TargetConnectionId = target.Id,
            TargetConnectionName = target.Name,
            Success = false,
            Elapsed = stopwatch.Elapsed,
            ErrorMessage = error
        };

    private static string Quote(string identifier) => $"[{identifier.Replace("]", "]]", StringComparison.Ordinal)}]";
}
