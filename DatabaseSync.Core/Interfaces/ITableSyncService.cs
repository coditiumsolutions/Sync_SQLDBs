using DatabaseSync.Core.DTOs;
using DatabaseSync.Core.Enums;
using DatabaseSync.Core.Models;

namespace DatabaseSync.Core.Interfaces;

public sealed class SyncTableRequest
{
    public required Guid CycleId { get; init; }

    public required SyncTriggerKind Trigger { get; init; }

    public required bool DryRun { get; init; }

    public required string BackupFolder { get; init; }
}

public interface ITableSyncService
{
    Task<TableSyncResult> SyncTableAsync(
        string sourceConnectionId,
        string targetConnectionId,
        TableSchema table,
        SyncTableRequest request,
        CancellationToken cancellationToken = default);
}
