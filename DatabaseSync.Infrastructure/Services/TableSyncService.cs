using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Dapper;
using DatabaseSync.Core.Diff;
using DatabaseSync.Core.DTOs;
using DatabaseSync.Core.Enums;
using DatabaseSync.Core.Interfaces;
using DatabaseSync.Core.Models;
using DatabaseSync.Core.Sql;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace DatabaseSync.Infrastructure.Services;

/// <summary>
/// Differential Main → target sync.
/// Fingerprints (primary key + SHA2_256 of the other copyable columns) are compared on the target,
/// and only inserts, updates, and deletes cross the network as full rows. Main always wins.
/// A target row that is newer is backed up before it is overwritten or deleted.
/// </summary>
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
        SyncTableRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(request);

        var stopwatch = Stopwatch.StartNew();
        var target = _connectionFactory.GetConnection(targetConnectionId);
        var settings = _configStore.Get().SyncSettings;
        var timeout = settings.CommandTimeoutSeconds;
        var dryRun = request.DryRun || settings.DryRun;

        if (!table.HasPrimaryKey)
        {
            _logger.LogWarning(
                "Skipping {Table}: differential sync requires a primary key, so the table was not copied.",
                table.FullName);
            return Result(table, target, stopwatch, dryRun, success: true, skipped: true,
                error: "Skipped: table has no primary key.");
        }

        var copyable = table.CopyableColumns;
        if (copyable.Count == 0)
        {
            return Result(table, target, stopwatch, dryRun, success: false, skipped: false,
                error: "Table has no copyable columns.");
        }

        var changeColumn = ChangeTimestampDetector.Detect(table.Columns, settings.ChangeTimestampColumnNames);
        if (changeColumn is null)
        {
            _logger.LogInformation(
                "Table {Table} has no comparable change-timestamp column. Conflict rule: main is applied and the target row is not backed up unless AssumeDifferingRowsConflict is true (currently {Assume}). rowversion is not used because it is not comparable across servers.",
                table.FullName,
                settings.AssumeDifferingRowsConflict);
        }

        try
        {
            await using var sourceConnection = (SqlConnection)_connectionFactory.CreateConnection(sourceConnectionId);
            await using var targetConnection = (SqlConnection)_connectionFactory.CreateConnection(targetConnectionId);
            await sourceConnection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await targetConnection.OpenAsync(cancellationToken).ConfigureAwait(false);

            var targetRows = await ScalarLongAsync(targetConnection, $"SELECT COUNT_BIG(1) FROM {table.FullName};", timeout, cancellationToken)
                .ConfigureAwait(false);

            if (targetRows == 0)
            {
                return await CopyAllAsInsertsAsync(
                    sourceConnection, targetConnection, table, target, copyable, settings, request, dryRun, changeColumn, stopwatch, timeout, cancellationToken)
                    .ConfigureAwait(false);
            }

            var conflictsPossible = changeColumn is not null || settings.AssumeDifferingRowsConflict;
            var sourceRows = await ScalarLongAsync(sourceConnection, $"SELECT COUNT_BIG(1) FROM {table.FullName};", timeout, cancellationToken)
                .ConfigureAwait(false);

            if (sourceRows == 0 && !conflictsPossible)
            {
                if (!dryRun)
                {
                    await ExecuteAsync(targetConnection, $"DELETE FROM {table.FullName};", timeout, cancellationToken)
                        .ConfigureAwait(false);
                }

                stopwatch.Stop();
                LogSummary(table, target, request, dryRun, changeColumn, sourceRows, inserted: 0, updated: 0, deleted: targetRows,
                    conflicted: 0, unchanged: 0, transferred: 0, bytes: targetRows * ColumnSizeEstimator.EstimateRowBytes(table.PrimaryKeyColumns),
                    backup: null, stopwatch);
                return Result(table, target, stopwatch, dryRun, true, false, sourceRows, targetRows, 0, 0, targetRows, 0, 0, 0,
                    targetRows * ColumnSizeEstimator.EstimateRowBytes(table.PrimaryKeyColumns), null, changeColumn?.Name);
            }

            var hashSql = RowHashSqlBuilder.BuildRowHashExpression(table);
            var keyList = string.Join(", ", table.PrimaryKeyColumns.Select(column => Q(column.Name)));

            await ExecuteAsync(targetConnection, $"""
                IF OBJECT_ID('tempdb..#SourceFp') IS NOT NULL DROP TABLE #SourceFp;
                SELECT TOP (0) {keyList}, CONVERT(varbinary(32), 0x) AS RowHash
                INTO #SourceFp
                FROM {table.FullName};
                """, timeout, cancellationToken).ConfigureAwait(false);

            var fingerprintRows = await BulkCopyFingerprintsAsync(
                sourceConnection, targetConnection, table, $"CONVERT(varbinary(32), {hashSql})", timeout, settings.FingerprintBatchSize, cancellationToken)
                .ConfigureAwait(false);

            await ExecuteAsync(targetConnection, $"CREATE CLUSTERED INDEX IX_SourceFp ON #SourceFp ({keyList});", timeout, cancellationToken)
                .ConfigureAwait(false);

            await ExecuteAsync(targetConnection, $"""
                IF OBJECT_ID('tempdb..#TargetFp') IS NOT NULL DROP TABLE #TargetFp;
                SELECT {keyList}, CONVERT(varbinary(32), {hashSql}) AS RowHash
                INTO #TargetFp
                FROM {table.FullName};
                CREATE CLUSTERED INDEX IX_TargetFp ON #TargetFp ({keyList});
                """, timeout, cancellationToken).ConfigureAwait(false);

            var diffs = await ReadDiffsAsync(targetConnection, table, timeout, cancellationToken).ConfigureAwait(false);
            var inserted = diffs.Count(row => row.Kind == DiffKind.Insert);
            var updated = diffs.Count(row => row.Kind == DiffKind.Update);
            var deleted = diffs.Count(row => row.Kind == DiffKind.Delete);
            var unchanged = Math.Max(0, fingerprintRows - inserted - updated);
            var pkBytes = ColumnSizeEstimator.EstimateRowBytes(table.PrimaryKeyColumns);
            var rowBytes = ColumnSizeEstimator.EstimateRowBytes(copyable);
            long bytes = fingerprintRows * (32L + pkBytes);

            if (diffs.Count == 0)
            {
                stopwatch.Stop();
                LogSummary(table, target, request, dryRun, changeColumn, fingerprintRows, 0, 0, 0, 0, unchanged, 0, bytes, null, stopwatch);
                return Result(table, target, stopwatch, dryRun, true, false, fingerprintRows, 0, 0, 0, 0, 0, unchanged, 0, bytes, null, changeColumn?.Name);
            }

            DateTimeOffset? mainMax = null;
            if (changeColumn is not null)
            {
                var rawMax = await sourceConnection.ExecuteScalarAsync<object?>(new CommandDefinition(
                    $"SELECT MAX({Q(changeColumn.Name)}) FROM {table.FullName};",
                    commandTimeout: timeout,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);
                mainMax = TimestampValue.Read(rawMax);
            }

            var classification = new ConflictClassificationContext
            {
                HasChangeTimestampColumn = changeColumn is not null,
                AssumeDifferingRowsConflict = settings.AssumeDifferingRowsConflict,
                MainMaxChangeTimestamp = mainMax
            };

            await ExecuteAsync(targetConnection, $"""
                IF OBJECT_ID('tempdb..#SyncStaging') IS NOT NULL DROP TABLE #SyncStaging;
                SELECT TOP (0) * INTO #SyncStaging FROM {table.FullName};
                """, timeout, cancellationToken).ConfigureAwait(false);

            var backupSettings = new ConflictBackupSettings
            {
                FileRowThreshold = settings.ConflictFileRowThreshold,
                FileMaxBytes = settings.ConflictFileMaxBytes,
                UseBackupTables = settings.UseConflictBackupTables && !dryRun
            };
            IConflictTableWriter? tableWriter = backupSettings.UseBackupTables
                ? new BackupTableWriter(targetConnection, table, copyable, timeout, request.CycleId)
                : null;
            IConflictLogTable? logTable = settings.WriteConflictLogTable && !dryRun
                ? new ConflictLogTableWriter(targetConnection, table, target.Name, request.CycleId, timeout)
                : null;

            await using var backups = ConflictBackupSession.Create(
                backupSettings,
                _logger,
                request.BackupFolder,
                $"{table.SchemaName}.{table.TableName}",
                target.Name,
                request.CycleId,
                request.Trigger.ToString(),
                dryRun,
                tableWriter,
                logTable);

            var batchSize = Math.Max(1, settings.DiffApplyBatchSize);
            try
            {
                for (var offset = 0; offset < diffs.Count; offset += batchSize)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var batch = diffs.Skip(offset).Take(batchSize).ToList();
                    await ApplyBatchAsync(
                        sourceConnection,
                        targetConnection,
                        table,
                        copyable,
                        changeColumn,
                        batch,
                        classification,
                        backups,
                        dryRun,
                        timeout,
                        cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                await backups.CompleteAsync(cancellationToken).ConfigureAwait(false);
            }

            bytes += (inserted + updated) * rowBytes;
            bytes += deleted * pkBytes;
            stopwatch.Stop();
            LogSummary(table, target, request, dryRun, changeColumn, fingerprintRows, inserted, updated, deleted,
                backups.Count, unchanged, inserted + updated, bytes, backups.Location, stopwatch);

            return Result(table, target, stopwatch, dryRun, true, false, fingerprintRows, inserted + updated + deleted,
                inserted, updated, deleted, backups.Count, unchanged, inserted + updated, bytes, backups.Location, changeColumn?.Name);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            stopwatch.Stop();
            _logger.LogError(ex, "Failed differential sync of {Table} to {Target}.", table.FullName, target.Name);
            return Result(table, target, stopwatch, dryRun, false, false, error: ex.Message, changeColumn: changeColumn?.Name);
        }
    }

    private async Task<TableSyncResult> CopyAllAsInsertsAsync(
        SqlConnection sourceConnection,
        SqlConnection targetConnection,
        TableSchema table,
        SyncConnection target,
        IReadOnlyList<ColumnSchema> copyable,
        SyncSettings settings,
        SyncTableRequest request,
        bool dryRun,
        ColumnSchema? changeColumn,
        Stopwatch stopwatch,
        int timeout,
        CancellationToken cancellationToken)
    {
        var selectSql = $"SELECT {ColumnList(copyable)} FROM {table.FullName};";
        long rows;
        long bytes;
        if (dryRun)
        {
            rows = await ScalarLongAsync(sourceConnection, $"SELECT COUNT_BIG(1) FROM {table.FullName};", timeout, cancellationToken)
                .ConfigureAwait(false);
            bytes = rows * ColumnSizeEstimator.EstimateRowBytes(copyable);
        }
        else
        {
            await ExecuteAsync(targetConnection, $"""
                IF OBJECT_ID('tempdb..#SyncStaging') IS NOT NULL DROP TABLE #SyncStaging;
                SELECT TOP (0) * INTO #SyncStaging FROM {table.FullName};
                """, timeout, cancellationToken).ConfigureAwait(false);

            if (table.HasIdentity)
            {
                await ExecuteAsync(targetConnection, $"SET IDENTITY_INSERT {table.FullName} ON;", timeout, cancellationToken)
                    .ConfigureAwait(false);
            }

            try
            {
                rows = await BulkCopyQueryAsync(
                    sourceConnection, targetConnection, selectSql, "#SyncStaging", copyable, timeout, settings.FingerprintBatchSize, transaction: null, cancellationToken)
                    .ConfigureAwait(false);
                await ExecuteAsync(targetConnection, BuildMergeSql(table, copyable), timeout, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (table.HasIdentity)
                {
                    await ExecuteAsync(targetConnection, $"SET IDENTITY_INSERT {table.FullName} OFF;", timeout, cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            bytes = rows * ColumnSizeEstimator.EstimateRowBytes(copyable);
        }

        stopwatch.Stop();
        _logger.LogInformation(
            "Differential sync {Table} => {Target}. Target was empty; all source rows are inserts. Trigger={Trigger} DryRun={DryRun} Inserted={Inserted} EstimatedBytes={EstimatedBytes} ElapsedMs={ElapsedMs}",
            table.FullName, target.Name, request.Trigger, dryRun, rows, bytes, stopwatch.ElapsedMilliseconds);

        return Result(table, target, stopwatch, dryRun, true, false, rows, rows, rows, 0, 0, 0, 0, rows, bytes, null, changeColumn?.Name);
    }

    private async Task ApplyBatchAsync(
        SqlConnection sourceConnection,
        SqlConnection targetConnection,
        TableSchema table,
        IReadOnlyList<ColumnSchema> copyable,
        ColumnSchema? changeColumn,
        IReadOnlyList<DiffKey> batch,
        ConflictClassificationContext classification,
        ConflictBackupSession backups,
        bool dryRun,
        int timeout,
        CancellationToken cancellationToken)
    {
        await CreateBatchKeysAsync(sourceConnection, table, timeout, cancellationToken).ConfigureAwait(false);
        await CreateBatchKeysAsync(targetConnection, table, timeout, cancellationToken).ConfigureAwait(false);
        await InsertBatchKeysAsync(sourceConnection, table, batch, timeout, cancellationToken).ConfigureAwait(false);
        await InsertBatchKeysAsync(targetConnection, table, batch, timeout, cancellationToken).ConfigureAwait(false);

        var keyNames = table.PrimaryKeyColumns.Select(column => column.Name).ToArray();
        var mainStamps = changeColumn is null
            ? new Dictionary<RowKey, DateTimeOffset?>()
            : await ReadStampsAsync(sourceConnection, table, changeColumn, "k.Op = 'U'", timeout, cancellationToken).ConfigureAwait(false);
        var targetStamps = changeColumn is null
            ? new Dictionary<RowKey, DateTimeOffset?>()
            : await ReadStampsAsync(targetConnection, table, changeColumn, "k.Op IN ('U', 'D')", timeout, cancellationToken).ConfigureAwait(false);

        var conflicts = new List<DiffKey>();
        foreach (var row in batch)
        {
            if (row.Kind == DiffKind.Insert)
            {
                continue;
            }

            mainStamps.TryGetValue(row.ToKey(), out var mainStamp);
            targetStamps.TryGetValue(row.ToKey(), out var targetStamp);
            if (ConflictClassifier.IsConflict(row.Kind, mainStamp, targetStamp, classification))
            {
                conflicts.Add(row);
            }
        }

        if (conflicts.Count > 0)
        {
            await CreateBatchKeysAsync(sourceConnection, table, timeout, cancellationToken, "#ConflictKeys").ConfigureAwait(false);
            await CreateBatchKeysAsync(targetConnection, table, timeout, cancellationToken, "#ConflictKeys").ConfigureAwait(false);
            await InsertBatchKeysAsync(sourceConnection, table, conflicts, timeout, cancellationToken, "#ConflictKeys").ConfigureAwait(false);
            await InsertBatchKeysAsync(targetConnection, table, conflicts, timeout, cancellationToken, "#ConflictKeys").ConfigureAwait(false);

            var targetValues = await ReadRowsAsync(targetConnection, table, copyable, "#ConflictKeys", "k.Op IN ('U', 'D')", timeout, cancellationToken)
                .ConfigureAwait(false);
            var mainValues = await ReadRowsAsync(sourceConnection, table, copyable, "#ConflictKeys", "k.Op = 'U'", timeout, cancellationToken)
                .ConfigureAwait(false);

            foreach (var conflict in conflicts)
            {
                var key = conflict.ToKey();
                targetValues.TryGetValue(key, out var targetRow);
                if (targetRow is null)
                {
                    continue;
                }

                mainValues.TryGetValue(key, out var mainRow);
                mainStamps.TryGetValue(key, out var mainStamp);
                targetStamps.TryGetValue(key, out var targetStamp);
                await backups.AddAsync(new ConflictRow
                {
                    Operation = conflict.Kind == DiffKind.Delete ? "Delete" : "Update",
                    KeyNames = keyNames,
                    KeyValues = conflict.Values,
                    TargetValues = targetRow,
                    MainValues = conflict.Kind == DiffKind.Update ? mainRow : null,
                    MainChangeTimestamp = mainStamp,
                    TargetChangeTimestamp = targetStamp
                }, cancellationToken).ConfigureAwait(false);
            }

            await backups.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        if (dryRun)
        {
            return;
        }

        var hasUpsert = batch.Any(row => row.Kind is DiffKind.Insert or DiffKind.Update);
        var hasDelete = batch.Any(row => row.Kind == DiffKind.Delete);
        var identityOn = false;
        try
        {
            if (hasUpsert && table.HasIdentity)
            {
                await ExecuteAsync(targetConnection, $"SET IDENTITY_INSERT {table.FullName} ON;", timeout, cancellationToken)
                    .ConfigureAwait(false);
                identityOn = true;
            }

            await using var transaction = (SqlTransaction)await targetConnection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            try
            {
            if (hasUpsert)
            {
                await ExecuteAsync(targetConnection, "TRUNCATE TABLE #SyncStaging;", timeout, cancellationToken, transaction)
                    .ConfigureAwait(false);
                var selectSql = $"""
                    SELECT {ColumnList(copyable)}
                    FROM {table.FullName} AS src
                    INNER JOIN #BatchKeys AS k ON {Join("src", "k", table)}
                    WHERE k.Op IN ('I', 'U');
                    """;
                await BulkCopyQueryAsync(
                    sourceConnection, targetConnection, selectSql, "#SyncStaging", copyable, timeout, batch.Count, transaction, cancellationToken)
                    .ConfigureAwait(false);
                await ExecuteAsync(targetConnection, BuildMergeSql(table, copyable), timeout, cancellationToken, transaction)
                    .ConfigureAwait(false);
            }

            if (hasDelete)
            {
                await ExecuteAsync(targetConnection, $"""
                    DELETE tgt
                    FROM {table.FullName} AS tgt
                    INNER JOIN #BatchKeys AS k ON {Join("tgt", "k", table)}
                    WHERE k.Op = 'D';
                    """, timeout, cancellationToken, transaction).ConfigureAwait(false);
            }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            if (identityOn)
            {
                await ExecuteAsync(targetConnection, $"SET IDENTITY_INSERT {table.FullName} OFF;", timeout, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private static async Task<long> BulkCopyFingerprintsAsync(
        SqlConnection sourceConnection,
        SqlConnection targetConnection,
        TableSchema table,
        string hashSql,
        int timeout,
        int batchSize,
        CancellationToken cancellationToken)
    {
        var keyList = string.Join(", ", table.PrimaryKeyColumns.Select(column => Q(column.Name)));
        await using var command = sourceConnection.CreateCommand();
        command.CommandText = $"SELECT {keyList}, {hashSql} AS RowHash FROM {table.FullName};";
        command.CommandTimeout = timeout;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        using var bulk = new SqlBulkCopy(targetConnection, SqlBulkCopyOptions.TableLock, null)
        {
            DestinationTableName = "#SourceFp",
            BulkCopyTimeout = timeout,
            BatchSize = batchSize,
            EnableStreaming = true
        };
        foreach (var column in table.PrimaryKeyColumns)
        {
            bulk.ColumnMappings.Add(column.Name, column.Name);
        }

        bulk.ColumnMappings.Add("RowHash", "RowHash");
        await bulk.WriteToServerAsync(reader, cancellationToken).ConfigureAwait(false);
        return bulk.RowsCopied;
    }

    private static async Task<long> BulkCopyQueryAsync(
        SqlConnection sourceConnection,
        SqlConnection targetConnection,
        string sql,
        string destination,
        IReadOnlyList<ColumnSchema> columns,
        int timeout,
        int batchSize,
        SqlTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = sourceConnection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = timeout;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        using var bulk = new SqlBulkCopy(targetConnection, SqlBulkCopyOptions.KeepIdentity, transaction)
        {
            DestinationTableName = destination,
            BulkCopyTimeout = timeout,
            BatchSize = Math.Max(1, batchSize),
            EnableStreaming = true
        };
        foreach (var column in columns)
        {
            bulk.ColumnMappings.Add(column.Name, column.Name);
        }

        await bulk.WriteToServerAsync(reader, cancellationToken).ConfigureAwait(false);
        return bulk.RowsCopied;
    }

    private static async Task<List<DiffKey>> ReadDiffsAsync(
        SqlConnection targetConnection,
        TableSchema table,
        int timeout,
        CancellationToken cancellationToken)
    {
        var rows = new List<DiffKey>();
        await using var command = targetConnection.CreateCommand();
        command.CommandText = BuildDiffSql(table);
        command.CommandTimeout = timeout;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var width = table.PrimaryKeyColumns.Count;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var values = new object?[width];
            for (var i = 0; i < width; i++)
            {
                values[i] = reader.IsDBNull(i + 1) ? null : reader.GetValue(i + 1);
            }

            rows.Add(new DiffKey(ParseOp(reader.GetValue(0)), values));
        }

        return rows;
    }

    private static async Task<Dictionary<RowKey, DateTimeOffset?>> ReadStampsAsync(
        SqlConnection connection,
        TableSchema table,
        ColumnSchema changeColumn,
        string opFilter,
        int timeout,
        CancellationToken cancellationToken)
    {
        var keys = table.PrimaryKeyColumns;
        var projection = string.Join(", ", keys.Select(column => $"k.{Q(column.Name)}"));
        var sql = $"""
            SELECT {projection}, src.{Q(changeColumn.Name)} AS __ChangeStamp
            FROM {table.FullName} AS src
            INNER JOIN #BatchKeys AS k ON {Join("src", "k", table)}
            WHERE {opFilter};
            """;

        var map = new Dictionary<RowKey, DateTimeOffset?>();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = timeout;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var values = new object?[keys.Count];
            for (var i = 0; i < keys.Count; i++)
            {
                values[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }

            var stamp = reader.IsDBNull(keys.Count) ? null : TimestampValue.Read(reader.GetValue(keys.Count));
            map[new RowKey(values)] = stamp;
        }

        return map;
    }

    private static async Task<Dictionary<RowKey, Dictionary<string, object?>>> ReadRowsAsync(
        SqlConnection connection,
        TableSchema table,
        IReadOnlyList<ColumnSchema> columns,
        string keyTable,
        string opFilter,
        int timeout,
        CancellationToken cancellationToken)
    {
        var projection = string.Join(", ", columns.Select(column =>
            $"{(column.IsPrimaryKey ? "k" : "src")}.{Q(column.Name)}"));
        var sql = $"""
            SELECT {projection}
            FROM {table.FullName} AS src
            INNER JOIN {keyTable} AS k ON {Join("src", "k", table)}
            WHERE {opFilter};
            """;

        var map = new Dictionary<RowKey, Dictionary<string, object?>>();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = timeout;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var keyOrdinals = table.PrimaryKeyColumns.Select(column => reader.GetOrdinal(column.Name)).ToArray();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var values = new object?[keyOrdinals.Length];
            for (var i = 0; i < keyOrdinals.Length; i++)
            {
                values[i] = reader.IsDBNull(keyOrdinals[i]) ? null : reader.GetValue(keyOrdinals[i]);
            }

            var row = new Dictionary<string, object?>(reader.FieldCount, StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++)
            {
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }

            map[new RowKey(values)] = row;
        }

        return map;
    }

    private static async Task CreateBatchKeysAsync(
        SqlConnection connection,
        TableSchema table,
        int timeout,
        CancellationToken cancellationToken,
        string tableName = "#BatchKeys")
    {
        var keyList = string.Join(", ", table.PrimaryKeyColumns.Select(column => Q(column.Name)));
        await ExecuteAsync(connection, $"""
            IF OBJECT_ID('tempdb..{tableName}') IS NOT NULL DROP TABLE {tableName};
            SELECT TOP (0) CAST(N'I' AS char(1)) AS Op, {keyList}
            INTO {tableName}
            FROM {table.FullName};
            """, timeout, cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertBatchKeysAsync(
        SqlConnection connection,
        TableSchema table,
        IReadOnlyList<DiffKey> rows,
        int timeout,
        CancellationToken cancellationToken,
        string tableName = "#BatchKeys")
    {
        var keys = table.PrimaryKeyColumns;
        const int groupSize = 40;
        for (var offset = 0; offset < rows.Count; offset += groupSize)
        {
            var slice = rows.Skip(offset).Take(groupSize).ToList();
            var sql = new StringBuilder();
            sql.Append("INSERT INTO ").Append(tableName).Append(" (Op");
            foreach (var key in keys)
            {
                sql.Append(", ").Append(Q(key.Name));
            }

            sql.Append(") VALUES ");
            var parameters = new DynamicParameters();
            for (var i = 0; i < slice.Count; i++)
            {
                if (i > 0)
                {
                    sql.Append(", ");
                }

                sql.Append("(@op").Append(i);
                parameters.Add($"op{i}", slice[i].Op);
                for (var column = 0; column < keys.Count; column++)
                {
                    sql.Append(", @k").Append(i).Append('_').Append(column);
                    parameters.Add($"k{i}_{column}", slice[i].Values[column] ?? DBNull.Value);
                }

                sql.Append(')');
            }

            await connection.ExecuteAsync(new CommandDefinition(
                sql.ToString(),
                parameters,
                commandTimeout: timeout,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
    }

    private static string BuildDiffSql(TableSchema table)
    {
        var keys = table.PrimaryKeyColumns;
        var join = Join("s", "t", table);
        var sourceKeys = string.Join(", ", keys.Select(key => $"s.{Q(key.Name)}"));
        var targetKeys = string.Join(", ", keys.Select(key => $"t.{Q(key.Name)}"));
        var first = Q(keys[0].Name);
        return $"""
            SELECT CAST('I' AS char(1)) AS Op, {sourceKeys}
            FROM #SourceFp AS s
            LEFT JOIN #TargetFp AS t ON {join}
            WHERE t.{first} IS NULL
            UNION ALL
            SELECT CAST('U' AS char(1)), {sourceKeys}
            FROM #SourceFp AS s
            INNER JOIN #TargetFp AS t ON {join}
            WHERE s.RowHash <> t.RowHash
            UNION ALL
            SELECT CAST('D' AS char(1)), {targetKeys}
            FROM #TargetFp AS t
            LEFT JOIN #SourceFp AS s ON {join}
            WHERE s.{first} IS NULL
            """;
    }

    private static string BuildMergeSql(TableSchema table, IReadOnlyList<ColumnSchema> copyable)
    {
        var onClause = Join("target", "source", table);
        var updateColumns = copyable.Where(column => !column.IsPrimaryKey).ToList();
        var sql = new StringBuilder();
        sql.Append("MERGE ").Append(table.FullName).AppendLine(" AS target");
        sql.AppendLine("USING #SyncStaging AS source");
        sql.Append("ON ").AppendLine(onClause);
        if (updateColumns.Count > 0)
        {
            var setClause = string.Join(", ", updateColumns.Select(column => $"target.{Q(column.Name)} = source.{Q(column.Name)}"));
            sql.AppendLine("WHEN MATCHED THEN");
            sql.Append("  UPDATE SET ").AppendLine(setClause);
        }

        var insertCols = string.Join(", ", copyable.Select(column => Q(column.Name)));
        var insertVals = string.Join(", ", copyable.Select(column => $"source.{Q(column.Name)}"));
        sql.AppendLine("WHEN NOT MATCHED BY TARGET THEN");
        sql.Append("  INSERT (").Append(insertCols).Append(") VALUES (").Append(insertVals).AppendLine(");");
        return sql.ToString();
    }

    private static string Join(string left, string right, TableSchema table) =>
        string.Join(" AND ", table.PrimaryKeyColumns.Select(column => $"{left}.{Q(column.Name)} = {right}.{Q(column.Name)}"));

    private static string ColumnList(IReadOnlyList<ColumnSchema> columns, string? alias = null)
    {
        var prefix = string.IsNullOrEmpty(alias) ? string.Empty : alias + ".";
        return string.Join(", ", columns.Select(column => prefix + Q(column.Name)));
    }

    private static string Q(string identifier) => SqlQuoting.Quote(identifier);

    private static char ParseOp(object value)
    {
        var text = Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim();
        return text switch
        {
            "I" or "U" or "D" => text[0],
            _ => throw new InvalidOperationException($"Unexpected differential operation '{text}'.")
        };
    }

    private static async Task<long> ScalarLongAsync(
        SqlConnection connection,
        string sql,
        int timeout,
        CancellationToken cancellationToken) =>
        await connection.ExecuteScalarAsync<long>(new CommandDefinition(sql, commandTimeout: timeout, cancellationToken: cancellationToken))
            .ConfigureAwait(false);

    private static async Task ExecuteAsync(
        SqlConnection connection,
        string sql,
        int timeout,
        CancellationToken cancellationToken,
        SqlTransaction? transaction = null) =>
        await connection.ExecuteAsync(new CommandDefinition(
            sql,
            transaction: transaction,
            commandTimeout: timeout,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

    private void LogSummary(
        TableSchema table,
        SyncConnection target,
        SyncTableRequest request,
        bool dryRun,
        ColumnSchema? changeColumn,
        long sourceRows,
        long inserted,
        long updated,
        long deleted,
        long conflicted,
        long unchanged,
        long transferred,
        long bytes,
        string? backup,
        Stopwatch stopwatch)
    {
        _logger.LogInformation(
            "Differential sync {Table} => {Target}. Trigger={Trigger} DryRun={DryRun} Inserted={Inserted} Updated={Updated} Deleted={Deleted} Conflicted={Conflicted} Unchanged={Unchanged} RowsTransferred={RowsTransferred} EstimatedBytes={EstimatedBytes} SourceFingerprints={SourceFingerprints} ChangeColumn={ChangeColumn} Backup={Backup} ElapsedMs={ElapsedMs}",
            table.FullName,
            target.Name,
            request.Trigger,
            dryRun,
            inserted,
            updated,
            deleted,
            conflicted,
            unchanged,
            transferred,
            bytes,
            sourceRows,
            changeColumn?.Name ?? "(none)",
            backup ?? "(none)",
            stopwatch.ElapsedMilliseconds);
    }

    private static TableSyncResult Result(
        TableSchema table,
        SyncConnection target,
        Stopwatch stopwatch,
        bool dryRun,
        bool success,
        bool skipped,
        long rowsRead = 0,
        long rowsAffected = 0,
        long inserted = 0,
        long updated = 0,
        long deleted = 0,
        long conflicted = 0,
        long unchanged = 0,
        long transferred = 0,
        long bytes = 0,
        string? backup = null,
        string? changeColumn = null,
        string? error = null) =>
        new()
        {
            SchemaName = table.SchemaName,
            TableName = table.TableName,
            TargetConnectionId = target.Id,
            TargetConnectionName = target.Name,
            Success = success,
            Skipped = skipped,
            DryRun = dryRun,
            RowsReadFromSource = rowsRead,
            RowsAffectedOnTarget = rowsAffected,
            RowsInserted = inserted,
            RowsUpdated = updated,
            RowsDeleted = deleted,
            RowsConflicted = conflicted,
            RowsUnchanged = unchanged,
            RowsTransferred = transferred,
            EstimatedBytesTransferred = bytes,
            BackupLocation = backup,
            ChangeTimestampColumn = changeColumn,
            Elapsed = stopwatch.Elapsed,
            ErrorMessage = error
        };

    private readonly record struct DiffKey(char Op, object?[] Values)
    {
        public DiffKind Kind => Op switch
        {
            'I' => DiffKind.Insert,
            'U' => DiffKind.Update,
            'D' => DiffKind.Delete,
            _ => throw new InvalidOperationException($"Unexpected differential operation '{Op}'.")
        };

        public RowKey ToKey() => new(Values);
    }

    private sealed class ConflictLogTableWriter : IConflictLogTable
    {
        private readonly SqlConnection _connection;
        private readonly TableSchema _table;
        private readonly string _targetName;
        private readonly Guid _cycleId;
        private readonly int _timeout;
        private bool _ensured;

        public ConflictLogTableWriter(SqlConnection connection, TableSchema table, string targetName, Guid cycleId, int timeout)
        {
            _connection = connection;
            _table = table;
            _targetName = targetName;
            _cycleId = cycleId;
            _timeout = timeout;
        }

        public async Task InsertAsync(IReadOnlyList<ConflictRow> rows, string backupLocation, CancellationToken cancellationToken)
        {
            if (!_ensured)
            {
                await ExecuteAsync(_connection, """
                    IF SCHEMA_ID(N'sync') IS NULL EXEC(N'CREATE SCHEMA sync');
                    IF OBJECT_ID(N'sync.ConflictLog', N'U') IS NULL
                    CREATE TABLE sync.ConflictLog
                    (
                        ConflictId bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_sync_ConflictLog PRIMARY KEY,
                        CycleId uniqueidentifier NOT NULL,
                        LoggedAtUtc datetime2 NOT NULL,
                        SchemaName sysname NOT NULL,
                        TableName sysname NOT NULL,
                        TargetName nvarchar(256) NOT NULL,
                        Operation varchar(16) NOT NULL,
                        KeyJson nvarchar(max) NOT NULL,
                        TargetValuesJson nvarchar(max) NULL,
                        MainValuesJson nvarchar(max) NULL,
                        MainChangeTimestamp datetimeoffset NULL,
                        TargetChangeTimestamp datetimeoffset NULL,
                        BackupLocation nvarchar(1024) NULL,
                        ReviewStatus varchar(20) NOT NULL CONSTRAINT DF_sync_ConflictLog_Review DEFAULT ('Pending'),
                        DryRun bit NOT NULL CONSTRAINT DF_sync_ConflictLog_DryRun DEFAULT (0)
                    );
                    """, _timeout, cancellationToken).ConfigureAwait(false);
                _ensured = true;
            }

            foreach (var row in rows)
            {
                var key = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < row.KeyNames.Length; i++)
                {
                    key[row.KeyNames[i]] = ConflictBackupSession.Normalize(row.KeyValues[i]);
                }

                await _connection.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT INTO sync.ConflictLog
                    (
                        CycleId, LoggedAtUtc, SchemaName, TableName, TargetName, Operation,
                        KeyJson, TargetValuesJson, MainValuesJson, MainChangeTimestamp, TargetChangeTimestamp,
                        BackupLocation, ReviewStatus, DryRun
                    )
                    VALUES
                    (
                        @CycleId, SYSUTCDATETIME(), @SchemaName, @TableName, @TargetName, @Operation,
                        @KeyJson, @TargetValuesJson, @MainValuesJson, @MainChangeTimestamp, @TargetChangeTimestamp,
                        @BackupLocation, N'Pending', 0
                    );
                    """,
                    new
                    {
                        CycleId = _cycleId,
                        SchemaName = _table.SchemaName,
                        TableName = _table.TableName,
                        TargetName = _targetName,
                        row.Operation,
                        KeyJson = Json(key),
                        TargetValuesJson = Json(row.TargetValues),
                        MainValuesJson = Json(row.MainValues),
                        row.MainChangeTimestamp,
                        row.TargetChangeTimestamp,
                        BackupLocation = backupLocation
                    },
                    commandTimeout: _timeout,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);
            }
        }

        private static string? Json(object? value) =>
            value is null ? null : System.Text.Json.JsonSerializer.Serialize(value);
    }

    private sealed class BackupTableWriter : IConflictTableWriter
    {
        private readonly SqlConnection _connection;
        private readonly TableSchema _table;
        private readonly IReadOnlyList<ColumnSchema> _columns;
        private readonly int _timeout;
        private readonly Guid _cycleId;
        private string? _qualified;

        public BackupTableWriter(
            SqlConnection connection,
            TableSchema table,
            IReadOnlyList<ColumnSchema> columns,
            int timeout,
            Guid cycleId)
        {
            _connection = connection;
            _table = table;
            _columns = columns;
            _timeout = timeout;
            _cycleId = cycleId;
        }

        public async Task<string> CreateAsync(CancellationToken cancellationToken)
        {
            await ExecuteAsync(_connection, "IF SCHEMA_ID(N'sync') IS NULL EXEC(N'CREATE SCHEMA sync');", _timeout, cancellationToken)
                .ConfigureAwait(false);

            var leaf = $"ConflictBackup_{_table.SchemaName}_{_table.TableName}_{DateTime.UtcNow:yyyyMMddHHmmss}_{Guid.NewGuid():N}";
            var safe = new string(leaf.Select(ch => char.IsLetterOrDigit(ch) || ch == '_' ? ch : '_').ToArray());
            if (safe.Length > 110)
            {
                safe = safe[..110];
            }

            _qualified = $"sync.{Q(safe)}";
            var columnList = string.Join(", ", _columns.Select(column => Q(column.Name)));
            await ExecuteAsync(_connection, $"""
                SELECT TOP (0) {columnList}
                INTO {_qualified}
                FROM {_table.FullName};
                ALTER TABLE {_qualified} ADD
                    BackupOperation varchar(16) NULL,
                    BackupCycleId uniqueidentifier NULL,
                    BackedUpAtUtc datetime2 NULL;
                """, _timeout, cancellationToken).ConfigureAwait(false);
            return _qualified;
        }

        public async Task WriteAsync(IReadOnlyList<ConflictRow> rows, CancellationToken cancellationToken)
        {
            if (_qualified is null)
            {
                throw new InvalidOperationException("Conflict backup table has not been created.");
            }

            var data = new DataTable();
            foreach (var column in _columns)
            {
                data.Columns.Add(column.Name, Nullable.GetUnderlyingType(ClrType(column.SqlTypeName)) ?? ClrType(column.SqlTypeName));
            }

            data.Columns.Add("BackupOperation", typeof(string));
            data.Columns.Add("BackupCycleId", typeof(Guid));
            data.Columns.Add("BackedUpAtUtc", typeof(DateTime));

            foreach (var row in rows)
            {
                var values = new object[data.Columns.Count];
                for (var i = 0; i < _columns.Count; i++)
                {
                object? raw = null;
                row.TargetValues?.TryGetValue(_columns[i].Name, out raw);
                    values[i] = Cell(raw, data.Columns[i].DataType);
                }

                values[^3] = row.Operation;
                values[^2] = _cycleId;
                values[^1] = DateTime.UtcNow;
                data.Rows.Add(values);
            }

            using var bulk = new SqlBulkCopy(_connection, SqlBulkCopyOptions.KeepIdentity, null)
            {
                DestinationTableName = _qualified,
                BulkCopyTimeout = _timeout
            };
            foreach (DataColumn column in data.Columns)
            {
                bulk.ColumnMappings.Add(column.ColumnName, column.ColumnName);
            }

            await bulk.WriteToServerAsync(data, cancellationToken).ConfigureAwait(false);
        }

        private static Type ClrType(string sqlType) => sqlType.ToLowerInvariant() switch
        {
            "bit" => typeof(bool),
            "tinyint" => typeof(byte),
            "smallint" => typeof(short),
            "int" => typeof(int),
            "bigint" => typeof(long),
            "real" => typeof(float),
            "float" => typeof(double),
            "decimal" or "numeric" or "money" or "smallmoney" => typeof(decimal),
            "uniqueidentifier" => typeof(Guid),
            "datetimeoffset" => typeof(DateTimeOffset),
            "time" => typeof(TimeSpan),
            "binary" or "varbinary" or "image" or "rowversion" or "timestamp" => typeof(byte[]),
            "date" or "datetime" or "datetime2" or "smalldatetime" => typeof(DateTime),
            _ => typeof(string)
        };

        private static object Cell(object? value, Type clrType)
        {
            if (value is null or DBNull)
            {
                return DBNull.Value;
            }

            if (clrType == typeof(string))
            {
                return value as string ?? Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
            }

            if (value.GetType() == clrType)
            {
                return value;
            }

            if (clrType == typeof(DateTime) && value is DateTimeOffset offset)
            {
                return offset.DateTime;
            }

            if (clrType == typeof(byte[]) && value is byte[] bytes)
            {
                return bytes;
            }

            return value;
        }
    }
}
