namespace DatabaseSync.Core.Enums;

/// <summary>Where a conflict set is stored before main's row is applied.</summary>
public enum ConflictBackupForm
{
    /// <summary>One JSON review file for the table and cycle.</summary>
    JsonReviewFile = 0,

    /// <summary>Streaming JSONL bulk export. Used when the conflict set is large and backup tables are off.</summary>
    BulkExportFile = 1,

    /// <summary>A table in the target's sync schema. Optional; off unless configured.</summary>
    BackupTable = 2
}
