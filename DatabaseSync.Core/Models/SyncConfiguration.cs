namespace DatabaseSync.Core.Models;

/// <summary>
/// Root shared configuration used by the Worker and the Admin Web UI.
/// Persisted as sync-config.json next to the Worker executable.
/// </summary>
public sealed class SyncConfiguration
{
    public const string FileName = "sync-config.json";

    public SyncSettings SyncSettings { get; set; } = new();

    public List<SyncConnection> Connections { get; set; } = [];
}
