namespace DatabaseSync.Core.Enums;

/// <summary>
/// Identifies the logical role of a configured SQL Server database in the sync topology.
/// </summary>
public enum DatabaseRole
{
    /// <summary>Primary database hosted on the main cloud server.</summary>
    MainDatabase = 0,

    /// <summary>Remote database hosted on the second cloud server.</summary>
    CloudDatabase = 1,

    /// <summary>Database hosted on the local network server.</summary>
    LocalDatabase = 2
}
