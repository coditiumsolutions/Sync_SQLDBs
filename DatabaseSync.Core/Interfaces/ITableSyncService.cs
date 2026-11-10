using DatabaseSync.Core.DTOs;
using DatabaseSync.Core.Models;

namespace DatabaseSync.Core.Interfaces;

public interface ITableSyncService
{
    Task<TableSyncResult> SyncTableAsync(
        string sourceConnectionId,
        string targetConnectionId,
        TableSchema table,
        CancellationToken cancellationToken = default);
}
