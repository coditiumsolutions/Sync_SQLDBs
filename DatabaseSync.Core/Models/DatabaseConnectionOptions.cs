namespace DatabaseSync.Core.Models;

/// <summary>
/// Strongly typed connection settings for a single SQL Server database.
/// Passwords should be supplied via User Secrets, environment variables, or a secure store — never hardcoded.
/// </summary>
public sealed class DatabaseConnectionOptions
{
    /// <summary>SQL Server host name or private IP address (optionally with instance name).</summary>
    public string Server { get; set; } = string.Empty;

    /// <summary>Target database name.</summary>
    public string Database { get; set; } = string.Empty;

    /// <summary>
    /// When true, Integrated Security (Windows authentication) is used and UserId/Password are ignored.
    /// </summary>
    public bool IntegratedSecurity { get; set; }

    /// <summary>SQL authentication user name. Leave empty when using Windows authentication.</summary>
    public string? UserId { get; set; }

    /// <summary>SQL authentication password. Prefer environment variables / user secrets in production.</summary>
    public string? Password { get; set; }

    /// <summary>Connection timeout in seconds.</summary>
    public int ConnectionTimeoutSeconds { get; set; } = 30;

    /// <summary>Whether TLS encryption is required for the connection.</summary>
    public bool Encrypt { get; set; } = true;

    /// <summary>
    /// When true, the SQL Server certificate is trusted without chain validation.
    /// Useful for SQL Server Express with self-signed certificates on a private VPN.
    /// </summary>
    public bool TrustServerCertificate { get; set; } = true;

    /// <summary>Optional application name reported to SQL Server for diagnostics.</summary>
    public string ApplicationName { get; set; } = "Coditium.DatabaseSync";
}
