using DatabaseSync.Core.DTOs;
using DatabaseSync.Core.Models;

namespace DatabaseSync.Core.Interfaces;

public interface ISchemaDiscoveryService
{
    /// <summary>Lists user tables on the connection for Admin selection UI.</summary>
    Task<IReadOnlyList<TableCatalogEntry>> ListTablesAsync(
        string connectionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Tables that will be synchronised (primary key required; respects SelectedTables).
    /// </summary>
    Task<IReadOnlyList<TableSchema>> GetTablesForSyncAsync(
        string sourceConnectionId,
        CancellationToken cancellationToken = default);
}
