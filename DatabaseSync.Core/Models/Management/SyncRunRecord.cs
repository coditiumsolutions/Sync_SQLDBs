namespace DatabaseSync.Core.Models.Management;

public sealed class SyncRunRecord
{
    public long Id { get; set; }

    public DateTimeOffset StartedAtUtc { get; set; }

    public DateTimeOffset? FinishedAtUtc { get; set; }

    public string Trigger { get; set; } = string.Empty;

    public string State { get; set; } = string.Empty;

    public bool DryRun { get; set; }

    public string? MainConnectionName { get; set; }

    public string? Message { get; set; }

    public int SuccessfulTables { get; set; }

    public int FailedTables { get; set; }

    public long RowsInserted { get; set; }

    public long RowsUpdated { get; set; }

    public long RowsDeleted { get; set; }

    public long RowsConflicted { get; set; }

    public long EstimatedBytesTransferred { get; set; }

    public string? RequestedByUserName { get; set; }

    public List<SyncRunServerRecord> Servers { get; set; } = [];

    public List<SyncRunTableRecord> Tables { get; set; } = [];
}

public sealed class SyncRunServerRecord
{
    public long Id { get; set; }

    public long SyncRunId { get; set; }

    public string? ConnectionId { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Server { get; set; }

    public string? DatabaseName { get; set; }

    public bool IsMain { get; set; }

    public string Outcome { get; set; } = string.Empty;

    public string? Reason { get; set; }

    public int TablesOk { get; set; }

    public int TablesFailed { get; set; }
}

public sealed class SyncRunTableRecord
{
    public long Id { get; set; }

    public long SyncRunId { get; set; }

    public string SchemaName { get; set; } = string.Empty;

    public string TableName { get; set; } = string.Empty;

    public string? TargetConnectionId { get; set; }

    public string? TargetConnectionName { get; set; }

    public bool Success { get; set; }

    public bool Skipped { get; set; }

    public long RowsInserted { get; set; }

    public long RowsUpdated { get; set; }

    public long RowsDeleted { get; set; }

    public long RowsConflicted { get; set; }

    public long EstimatedBytesTransferred { get; set; }

    public long ElapsedMs { get; set; }

    public string? ErrorMessage { get; set; }
}

public sealed class TableSelectionSnapshot
{
    public long Id { get; set; }

    public bool SyncAllTables { get; set; }

    public string SelectedTablesJson { get; set; } = "[]";

    public string? ChangedByUserName { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }
}
