namespace DatabaseSync.Infrastructure.Management;

public sealed class SynchronizationDbOptions
{
    public const string SectionName = "SynchronizationDb";

    /// <summary>
    /// Shared SQLite file for Synchronization projects across all product instances.
    /// Absolute path recommended (set by Start-SyncInstance.ps1).
    /// </summary>
    public string FilePath { get; set; } = "synchronization.db";
}
