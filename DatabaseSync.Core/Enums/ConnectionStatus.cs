namespace DatabaseSync.Core.Enums;

/// <summary>
/// Represents the outcome of a SQL Server connectivity probe.
/// </summary>
public enum ConnectionStatus
{
    Unknown = 0,
    Connected = 1,
    Failed = 2,
    TimedOut = 3,
    Cancelled = 4
}
