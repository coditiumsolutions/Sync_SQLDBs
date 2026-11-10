namespace DatabaseSync.Core.DTOs;

public sealed record TableSyncResult
{
    public required string SchemaName { get; init; }

    public required string TableName { get; init; }

    public required string TargetConnectionId { get; init; }

    public required string TargetConnectionName { get; init; }

    public bool Success { get; init; }

    /// <summary>True when the table was not synchronised (for example it has no primary key).</summary>
    public bool Skipped { get; init; }

    public bool DryRun { get; init; }

    public long RowsReadFromSource { get; init; }

    public long RowsAffectedOnTarget { get; init; }

    public long RowsInserted { get; init; }

    public long RowsUpdated { get; init; }

    public long RowsDeleted { get; init; }

    public long RowsConflicted { get; init; }

    public long RowsUnchanged { get; init; }

    /// <summary>Full rows sent for insert/update, plus delete keys. Fingerprint bytes are separate.</summary>
    public long RowsTransferred { get; init; }

    public long EstimatedBytesTransferred { get; init; }

    public string? BackupLocation { get; init; }

    public string? ChangeTimestampColumn { get; init; }

    public TimeSpan Elapsed { get; init; }

    public string? ErrorMessage { get; init; }
}
