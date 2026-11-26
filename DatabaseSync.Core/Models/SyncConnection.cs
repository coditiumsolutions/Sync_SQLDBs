using System.Text.Json.Serialization;
using DatabaseSync.Core.Enums;

namespace DatabaseSync.Core.Models;

/// <summary>
/// A configurable SQL Server endpoint that can be nominated as Main (source) or used as a sync target.
/// </summary>
public sealed class SyncConnection
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Friendly display name shown in the Admin UI.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>When true, this connection is the synchronisation source.</summary>
    public bool IsMain { get; set; }

    /// <summary>When false, connectivity checks and sync skip this connection.</summary>
    public bool IsEnabled { get; set; } = true;

    public string Server { get; set; } = string.Empty;

    public string Database { get; set; } = string.Empty;

    public bool IntegratedSecurity { get; set; }

    public string? UserId { get; set; }

    public string? Password { get; set; }

    public int ConnectionTimeoutSeconds { get; set; } = 15;

    public bool Encrypt { get; set; } = true;

    public bool TrustServerCertificate { get; set; } = true;

    public string ApplicationName { get; set; } = "Coditium.DatabaseSync";

    /// <summary>Last Admin UI connectivity probe. Unknown until a test has run.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ConnectionStatus LastConnectivityStatus { get; set; } = ConnectionStatus.Unknown;

    public DateTimeOffset? LastConnectivityCheckedAtUtc { get; set; }

    public long? LastConnectivityElapsedMs { get; set; }

    public string? LastConnectivityServerVersion { get; set; }

    public string? LastConnectivityError { get; set; }

    public void ClearLastConnectivity()
    {
        LastConnectivityStatus = ConnectionStatus.Unknown;
        LastConnectivityCheckedAtUtc = null;
        LastConnectivityElapsedMs = null;
        LastConnectivityServerVersion = null;
        LastConnectivityError = null;
    }
}
