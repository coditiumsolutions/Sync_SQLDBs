namespace DatabaseSync.Core.Models.Management;

public sealed class ProjectRecord
{
    public long Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string ProjectHint { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }

    public List<ProjectDatabaseRecord> Databases { get; set; } = [];
}

public sealed class ProjectDatabaseRecord
{
    public long Id { get; set; }

    public long ProjectId { get; set; }

    public string DatabaseName { get; set; } = string.Empty;

    public string DatabaseHint { get; set; } = string.Empty;

    public string ServerIp { get; set; } = string.Empty;

    public string? UserId { get; set; }

    public string? Password { get; set; }

    public bool IsSource { get; set; }

    public bool IntegratedSecurity { get; set; }

    public bool Encrypt { get; set; } = true;

    public bool TrustServerCertificate { get; set; } = true;

    public int ConnectionTimeoutSeconds { get; set; } = 15;

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }
}
