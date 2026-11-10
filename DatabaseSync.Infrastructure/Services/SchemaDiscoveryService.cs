using Dapper;
using DatabaseSync.Core.Interfaces;
using DatabaseSync.Core.Models;
using Microsoft.Extensions.Logging;

namespace DatabaseSync.Infrastructure.Services;

public sealed class SchemaDiscoveryService : ISchemaDiscoveryService
{
    private readonly ISqlConnectionFactory _connectionFactory;
    private readonly ISyncConfigStore _configStore;
    private readonly ILogger<SchemaDiscoveryService> _logger;

    public SchemaDiscoveryService(
        ISqlConnectionFactory connectionFactory,
        ISyncConfigStore configStore,
        ILogger<SchemaDiscoveryService> logger)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _configStore = configStore ?? throw new ArgumentNullException(nameof(configStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<IReadOnlyList<TableSchema>> GetTablesForSyncAsync(
        string sourceConnectionId,
        CancellationToken cancellationToken = default)
    {
        var timeout = _configStore.Get().SyncSettings.CommandTimeoutSeconds;

        await using var connection = _connectionFactory.CreateConnection(sourceConnectionId);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        const string tableSql = """
            SELECT
                s.name AS SchemaName,
                t.name AS TableName,
                t.object_id AS ObjectId
            FROM sys.tables t
            INNER JOIN sys.schemas s ON s.schema_id = t.schema_id
            WHERE t.is_ms_shipped = 0
              AND t.name NOT LIKE '\_\_SyncStaging%' ESCAPE '\'
            ORDER BY s.name, t.name;
            """;

        var tables = (await connection.QueryAsync<(string SchemaName, string TableName, int ObjectId)>(
            new CommandDefinition(tableSql, commandTimeout: timeout, cancellationToken: cancellationToken))
            .ConfigureAwait(false)).ToList();

        const string columnSql = """
            SELECT
                c.object_id AS ObjectId,
                c.name AS Name,
                ty.name AS SqlTypeName,
                c.column_id AS Ordinal,
                CASE WHEN pk.column_id IS NULL THEN 0 ELSE 1 END AS IsPrimaryKey,
                CASE WHEN c.is_identity = 1 THEN 1 ELSE 0 END AS IsIdentity,
                CASE WHEN c.is_computed = 1 THEN 1 ELSE 0 END AS IsComputed,
                CASE WHEN ty.name IN ('timestamp', 'rowversion') THEN 1 ELSE 0 END AS IsRowVersion
            FROM sys.columns c
            INNER JOIN sys.types ty ON ty.user_type_id = c.user_type_id
            LEFT JOIN (
                SELECT ic.object_id, ic.column_id
                FROM sys.indexes i
                INNER JOIN sys.index_columns ic
                    ON ic.object_id = i.object_id AND ic.index_id = i.index_id
                WHERE i.is_primary_key = 1
            ) pk ON pk.object_id = c.object_id AND pk.column_id = c.column_id
            WHERE c.object_id IN @ObjectIds
            ORDER BY c.object_id, c.column_id;
            """;

        var objectIds = tables.Select(t => t.ObjectId).ToArray();
        var columns = (await connection.QueryAsync<ColumnRow>(
            new CommandDefinition(
                columnSql,
                new { ObjectIds = objectIds },
                commandTimeout: timeout,
                cancellationToken: cancellationToken))
            .ConfigureAwait(false)).ToList();

        const string fkSql = """
            SELECT
                fk.parent_object_id AS ChildObjectId,
                fk.referenced_object_id AS ParentObjectId
            FROM sys.foreign_keys fk
            WHERE fk.parent_object_id IN @ObjectIds
              AND fk.referenced_object_id IN @ObjectIds;
            """;

        var foreignKeys = (await connection.QueryAsync<(int ChildObjectId, int ParentObjectId)>(
            new CommandDefinition(
                fkSql,
                new { ObjectIds = objectIds },
                commandTimeout: timeout,
                cancellationToken: cancellationToken))
            .ConfigureAwait(false)).ToList();

        var columnsByObject = columns.GroupBy(c => c.ObjectId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var schemas = tables.Select(t =>
        {
            columnsByObject.TryGetValue(t.ObjectId, out var cols);
            cols ??= [];

            return new
            {
                t.ObjectId,
                Schema = new TableSchema
                {
                    SchemaName = t.SchemaName,
                    TableName = t.TableName,
                    Columns = cols.Select(c => new ColumnSchema
                    {
                        Name = c.Name,
                        SqlTypeName = c.SqlTypeName,
                        Ordinal = c.Ordinal,
                        IsPrimaryKey = c.IsPrimaryKey == 1,
                        IsIdentity = c.IsIdentity == 1,
                        IsComputed = c.IsComputed == 1,
                        IsRowVersion = c.IsRowVersion == 1
                    }).ToList()
                }
            };
        }).ToList();

        var orderedObjectIds = TopologicalSort(
            schemas.Select(s => s.ObjectId).ToList(),
            foreignKeys);

        var objectLookup = schemas.ToDictionary(s => s.ObjectId, s => s.Schema);
        var ordered = new List<TableSchema>(orderedObjectIds.Count);

        foreach (var id in orderedObjectIds)
        {
            var table = objectLookup[id];
            if (!table.HasPrimaryKey)
            {
                _logger.LogWarning("Skipping table {Table} because it has no primary key.", table.FullName);
                continue;
            }

            ordered.Add(table);
        }

        _logger.LogInformation(
            "Discovered {TableCount} syncable tables from connection {ConnectionId} (parent-first order).",
            ordered.Count,
            sourceConnectionId);

        return ordered;
    }

    private static List<int> TopologicalSort(
        IReadOnlyList<int> nodes,
        IReadOnlyList<(int ChildObjectId, int ParentObjectId)> edges)
    {
        var nodeSet = nodes.ToHashSet();
        var dependents = nodes.ToDictionary(n => n, _ => new List<int>());
        var remainingParents = nodes.ToDictionary(n => n, _ => 0);

        foreach (var (child, parent) in edges)
        {
            if (!nodeSet.Contains(child) || !nodeSet.Contains(parent) || child == parent)
            {
                continue;
            }

            dependents[parent].Add(child);
            remainingParents[child]++;
        }

        var queue = new Queue<int>(remainingParents.Where(kv => kv.Value == 0).Select(kv => kv.Key).OrderBy(x => x));
        var result = new List<int>(nodes.Count);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            result.Add(current);

            foreach (var child in dependents[current].OrderBy(x => x))
            {
                remainingParents[child]--;
                if (remainingParents[child] == 0)
                {
                    queue.Enqueue(child);
                }
            }
        }

        if (result.Count != nodes.Count)
        {
            foreach (var node in nodes.Where(n => !result.Contains(n)))
            {
                result.Add(node);
            }
        }

        return result;
    }

    private sealed class ColumnRow
    {
        public int ObjectId { get; init; }
        public string Name { get; init; } = string.Empty;
        public string SqlTypeName { get; init; } = string.Empty;
        public int Ordinal { get; init; }
        public int IsPrimaryKey { get; init; }
        public int IsIdentity { get; init; }
        public int IsComputed { get; init; }
        public int IsRowVersion { get; init; }
    }
}
