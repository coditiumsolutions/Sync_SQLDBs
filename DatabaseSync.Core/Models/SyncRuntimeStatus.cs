namespace DatabaseSync.Core.Models;

/// <summary>
/// Last synchronisation cycle status written by the Worker for the Admin UI.
/// </summary>
public sealed class SyncRuntimeStatus
{
    public const string FileName = "sync-status.json";

    public string State { get; set; } = "Idle"; // Idle | Running | Succeeded | Failed | Skipped

    public string? Message { get; set; }

    public string? MainConnectionName { get; set; }

    public IReadOnlyList<string> TargetsSynced { get; set; } = [];

    public IReadOnlyList<ServerSyncReport> Servers { get; set; } = [];

    public int SuccessfulTables { get; set; }

    public int FailedTables { get; set; }

    public DateTimeOffset? StartedAtUtc { get; set; }

    public DateTimeOffset? FinishedAtUtc { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
