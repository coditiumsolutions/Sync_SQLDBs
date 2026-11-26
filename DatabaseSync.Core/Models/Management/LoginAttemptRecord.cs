namespace DatabaseSync.Core.Models.Management;

public sealed class LoginAttemptRecord
{
    public long Id { get; set; }

    public string UserName { get; set; } = string.Empty;

    public bool Succeeded { get; set; }

    public string? IpAddress { get; set; }

    public string? UserAgent { get; set; }

    public string? Detail { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }
}
