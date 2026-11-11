namespace DatabaseSync.Core.Models;

/// <summary>
/// Last synchronisation cycle status written by the Worker for the Admin UI.
/// </summary>
public sealed class SyncRuntimeStatus
{
    public const string FileName = "sync-status.json";

    public string State { get; set; } = "Idle"; // Idle | Running | Succeeded | Failed | Skipped

    public string? Message { get; set; }

    /// <summary>Manual, Scheduled, or Interval. Set for the cycle currently running or last finished.</summary>
    public string? Trigger { get; set; }

    public string? LastTrigger { get; set; }

    public string? MainConnectionName { get; set; }

    public IReadOnlyList<string> TargetsSynced { get; set; } = [];

    public IReadOnlyList<ServerSyncReport> Servers { get; set; } = [];

    public int SuccessfulTables { get; set; }

    public int FailedTables { get; set; }

    public long RowsInserted { get; set; }

    public long RowsUpdated { get; set; }

    public long RowsDeleted { get; set; }

    public long RowsConflicted { get; set; }

    public long EstimatedBytesTransferred { get; set; }

    public bool DryRun { get; set; }

    public DateTimeOffset? StartedAtUtc { get; set; }

    public DateTimeOffset? FinishedAtUtc { get; set; }

    public DateTimeOffset? LastRunStartedUtc { get; set; }

    public DateTimeOffset? LastRunFinishedUtc { get; set; }

    public DateTimeOffset? LastManualRunUtc { get; set; }

    public DateTimeOffset? LastScheduledRunUtc { get; set; }

    public DateTimeOffset? LastIntervalRunUtc { get; set; }

    public DateTimeOffset? NextScheduledRunUtc { get; set; }

    public DateTimeOffset? NextIntervalRunUtc { get; set; }

    public string? LastSkippedTrigger { get; set; }

    public string? LastSkipReason { get; set; }

    public DateTimeOffset? LastSkippedAtUtc { get; set; }

    /// <summary>Occurrence key → when that schedule slot fired. Pruned by the worker.</summary>
    public Dictionary<string, DateTimeOffset> FiredScheduleOccurrences { get; set; } = new();

    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
