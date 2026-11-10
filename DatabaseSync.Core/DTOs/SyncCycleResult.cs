namespace DatabaseSync.Core.DTOs;

public sealed record SyncCycleResult
{
    public DateTimeOffset StartedAtUtc { get; init; }

    public DateTimeOffset FinishedAtUtc { get; init; }

    public bool Success { get; init; }

    public string? MainConnectionName { get; init; }

    public IReadOnlyList<string> TargetsSynced { get; init; } = [];

    public IReadOnlyList<TableSyncResult> TableResults { get; init; } = [];

    public string? ErrorMessage { get; init; }

    public int SuccessfulTables => TableResults.Count(r => r.Success);

    public int FailedTables => TableResults.Count(r => !r.Success);
}
