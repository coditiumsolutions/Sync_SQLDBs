namespace DatabaseSync.Core.Models;

/// <summary>
/// Per-server outcome for the latest synchronisation cycle.
/// </summary>
public sealed class ServerSyncReport
{
    public string ConnectionId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Server { get; set; } = string.Empty;

    public string Database { get; set; } = string.Empty;

    public bool IsMain { get; set; }

    /// <summary>Synced | Failed | Skipped | Source</summary>
    public string Outcome { get; set; } = "Skipped";

    public string? Reason { get; set; }

    public int TablesOk { get; set; }

    public int TablesFailed { get; set; }
}
