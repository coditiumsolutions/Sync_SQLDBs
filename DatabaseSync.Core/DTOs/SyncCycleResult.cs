using DatabaseSync.Core.Enums;

namespace DatabaseSync.Core.DTOs;

public sealed record SyncCycleResult
{
    public DateTimeOffset StartedAtUtc { get; init; }

    public DateTimeOffset FinishedAtUtc { get; init; }

    public bool Success { get; init; }

    public SyncTriggerKind Trigger { get; init; }

    public bool DryRun { get; init; }

    public string? MainConnectionName { get; init; }

    public IReadOnlyList<string> TargetsSynced { get; init; } = [];

    public IReadOnlyList<TableSyncResult> TableResults { get; init; } = [];

    public string? ErrorMessage { get; init; }

    public int SuccessfulTables => TableResults.Count(r => r.Success);

    public int FailedTables => TableResults.Count(r => !r.Success);

    public long RowsInserted => TableResults.Sum(r => r.RowsInserted);

    public long RowsUpdated => TableResults.Sum(r => r.RowsUpdated);

    public long RowsDeleted => TableResults.Sum(r => r.RowsDeleted);

    public long RowsConflicted => TableResults.Sum(r => r.RowsConflicted);

    public long EstimatedBytesTransferred => TableResults.Sum(r => r.EstimatedBytesTransferred);
}
