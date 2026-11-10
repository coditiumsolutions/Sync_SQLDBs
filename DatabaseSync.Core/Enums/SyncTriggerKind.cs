namespace DatabaseSync.Core.Enums;

/// <summary>What asked for a synchronisation cycle.</summary>
public enum SyncTriggerKind
{
    Manual = 0,
    Scheduled = 1,
    Interval = 2
}
