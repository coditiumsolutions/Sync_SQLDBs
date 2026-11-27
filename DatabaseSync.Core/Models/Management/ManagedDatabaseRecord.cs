namespace DatabaseSync.Core.Models.Management;

public sealed class ManagedDatabaseRecord
{
    public long Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string DatabaseHint { get; set; } = string.Empty;

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }
}
