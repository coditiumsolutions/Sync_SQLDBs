namespace DatabaseSync.Infrastructure.Management;

public sealed class ManagementDbOptions
{
    public const string SectionName = "ManagementDb";

    /// <summary>
    /// Absolute or content-root-relative path to the SQLite file.
    /// Default resolves next to sync-config.json as management.db.
    /// </summary>
    public string FilePath { get; set; } = "management.db";
}
