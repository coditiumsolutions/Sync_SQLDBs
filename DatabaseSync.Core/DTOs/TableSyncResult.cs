namespace DatabaseSync.Core.DTOs;

public sealed record TableSyncResult
{
    public required string SchemaName { get; init; }

    public required string TableName { get; init; }

    public required string TargetConnectionId { get; init; }

    public required string TargetConnectionName { get; init; }

    public bool Success { get; init; }

    public long RowsReadFromSource { get; init; }

    public long RowsAffectedOnTarget { get; init; }

    public TimeSpan Elapsed { get; init; }

    public string? ErrorMessage { get; init; }
}
