namespace DatabaseSync.Core.Models.Management;

public sealed class AuditEventRecord
{
    public long Id { get; set; }

    public long? UserId { get; set; }

    public string? UserName { get; set; }

    public string Action { get; set; } = string.Empty;

    public string Category { get; set; } = string.Empty;

    public string? Detail { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }
}
