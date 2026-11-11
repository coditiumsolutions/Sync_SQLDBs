using System.Text.Json;
using DatabaseSync.Core.Enums;
using DatabaseSync.Core.Interfaces;
using DatabaseSync.Core.Models;
using DatabaseSync.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var schemaName = Environment.GetEnvironmentVariable("BENCH_SCHEMA") ?? "bench";
var builder = Host.CreateApplicationBuilder(args);
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.Services.AddDatabaseSyncInfrastructure(builder.Configuration);

using var host = builder.Build();
var tableSync = host.Services.GetRequiredService<ITableSyncService>();
var connections = host.Services.GetRequiredService<ISqlConnectionFactory>();
var config = host.Services.GetRequiredService<ISyncConfigStore>();
var main = connections.GetMainConnection()
    ?? throw new InvalidOperationException("No enabled main connection.");
var targets = connections.GetTargetConnections();
var started = DateTimeOffset.UtcNow;
var tables = await BenchCatalog.LoadAsync(connections, main.Id, schemaName);
var settings = config.Get().SyncSettings;
var backupFolder = Path.IsPathRooted(settings.ConflictBackupFolder)
    ? settings.ConflictBackupFolder
    : Path.GetFullPath(settings.ConflictBackupFolder);
Directory.CreateDirectory(backupFolder);
var cycleId = Guid.NewGuid();
var results = new List<DatabaseSync.Core.DTOs.TableSyncResult>();

foreach (var target in targets)
{
    await BenchForeignKeys.SetAsync(connections, target.Id, schemaName, enable: false);
    try
    {
        foreach (var table in tables)
        {
            results.Add(await tableSync.SyncTableAsync(
                main.Id,
                target.Id,
                table,
                new SyncTableRequest
                {
                    CycleId = cycleId,
                    Trigger = SyncTriggerKind.Manual,
                    DryRun = settings.DryRun,
                    BackupFolder = backupFolder
                }));
        }
    }
    finally
    {
        await BenchForeignKeys.SetAsync(connections, target.Id, schemaName, enable: true);
    }
}

var finished = DateTimeOffset.UtcNow;
var failed = results.Where(result => !result.Success).Select(result => $"{result.SchemaName}.{result.TableName}: {result.ErrorMessage}").ToArray();
var payload = new
{
    engine = "new",
    success = failed.Length == 0,
    engineElapsedMs = (long)(finished - started).TotalMilliseconds,
    error = failed.Length == 0 ? null : string.Join("; ", failed),
    rowsRead = results.Sum(result => result.RowsReadFromSource),
    rowsWritten = results.Sum(result => result.RowsInserted + result.RowsUpdated + result.RowsDeleted),
    rowsInserted = results.Sum(result => result.RowsInserted),
    rowsUpdated = results.Sum(result => result.RowsUpdated),
    rowsDeleted = results.Sum(result => result.RowsDeleted),
    rowsConflicted = results.Sum(result => result.RowsConflicted),
    estimatedBytes = results.Sum(result => result.EstimatedBytesTransferred),
    tableErrors = failed
};
Console.WriteLine("BENCH_JSON:" + JsonSerializer.Serialize(payload));
return failed.Length == 0 ? 0 : 1;

static class BenchCatalog
{
    public static async Task<List<TableSchema>> LoadAsync(ISqlConnectionFactory connections, string connectionId, string schema)
    {
        await using var connection = connections.CreateConnection(connectionId);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.name, ty.name, c.column_id,
                   CASE WHEN pk.column_id IS NULL THEN 0 ELSE 1 END,
                   c.is_identity, c.is_computed,
                   CASE WHEN ty.name IN ('timestamp', 'rowversion') THEN 1 ELSE 0 END,
                   t.name
            FROM sys.tables AS t
            INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id
            INNER JOIN sys.columns AS c ON c.object_id = t.object_id
            INNER JOIN sys.types AS ty ON ty.user_type_id = c.user_type_id
            LEFT JOIN (
                SELECT ic.object_id, ic.column_id
                FROM sys.indexes AS i
                INNER JOIN sys.index_columns AS ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
                WHERE i.is_primary_key = 1
            ) AS pk ON pk.object_id = c.object_id AND pk.column_id = c.column_id
            WHERE s.name = @schema
            ORDER BY t.name, c.column_id;
            """;
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@schema";
        parameter.Value = schema;
        command.Parameters.Add(parameter);
        var grouped = new Dictionary<string, List<ColumnSchema>>(StringComparer.OrdinalIgnoreCase);
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                var table = reader.GetString(7);
                if (!grouped.TryGetValue(table, out var columns))
                {
                    columns = [];
                    grouped[table] = columns;
                }

                columns.Add(new ColumnSchema
                {
                    Name = reader.GetString(0),
                    SqlTypeName = reader.GetString(1),
                    Ordinal = reader.GetInt32(2),
                    IsPrimaryKey = reader.GetInt32(3) == 1,
                    IsIdentity = reader.GetBoolean(4),
                    IsComputed = reader.GetBoolean(5),
                    IsRowVersion = reader.GetInt32(6) == 1
                });
            }
        }

        var order = new[] { "Customer", "OrderHeader", "OrderLine" };
        return grouped
            .Select(pair => new TableSchema { SchemaName = schema, TableName = pair.Key, Columns = pair.Value })
            .Where(table => table.HasPrimaryKey)
            .OrderBy(table =>
            {
                var index = Array.FindIndex(order, name => name.Equals(table.TableName, StringComparison.OrdinalIgnoreCase));
                return index < 0 ? 100 : index;
            })
            .ThenBy(table => table.TableName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}

static class BenchForeignKeys
{
    public static async Task SetAsync(ISqlConnectionFactory connections, string connectionId, string schema, bool enable)
    {
        var action = enable ? "WITH NOCHECK CHECK CONSTRAINT ALL" : "NOCHECK CONSTRAINT ALL";
        await using var connection = connections.CreateConnection(connectionId);
        await connection.OpenAsync();
        await using var list = connection.CreateCommand();
        list.CommandText = """
            SELECT QUOTENAME(s.name) + '.' + QUOTENAME(t.name)
            FROM sys.tables AS t
            INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id
            WHERE s.name = @schema;
            """;
        var parameter = list.CreateParameter();
        parameter.ParameterName = "@schema";
        parameter.Value = schema;
        list.Parameters.Add(parameter);
        var tables = new List<string>();
        await using (var reader = await list.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                tables.Add(reader.GetString(0));
            }
        }

        foreach (var table in tables)
        {
            await using var alter = connection.CreateCommand();
            alter.CommandText = $"ALTER TABLE {table} {action};";
            await alter.ExecuteNonQueryAsync();
        }
    }
}
