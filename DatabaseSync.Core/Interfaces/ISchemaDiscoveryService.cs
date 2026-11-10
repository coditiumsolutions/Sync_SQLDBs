using DatabaseSync.Core.Models;

namespace DatabaseSync.Core.Interfaces;

public interface ISchemaDiscoveryService
{
    Task<IReadOnlyList<TableSchema>> GetTablesForSyncAsync(
        string sourceConnectionId,
        CancellationToken cancellationToken = default);
}
