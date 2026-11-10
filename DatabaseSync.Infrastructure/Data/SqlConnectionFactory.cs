using System.Data.Common;
using DatabaseSync.Core.Interfaces;
using DatabaseSync.Core.Models;
using Microsoft.Data.SqlClient;

namespace DatabaseSync.Infrastructure.Data;

public sealed class SqlConnectionFactory : ISqlConnectionFactory
{
    private readonly ISyncConfigStore _configStore;

    public SqlConnectionFactory(ISyncConfigStore configStore)
    {
        _configStore = configStore ?? throw new ArgumentNullException(nameof(configStore));
    }

    public DbConnection CreateConnection(string connectionId)
    {
        var connection = GetConnection(connectionId);
        return new SqlConnection(BuildConnectionString(connection));
    }

    public SyncConnection GetConnection(string connectionId)
    {
        var connection = _configStore.Get().Connections
            .FirstOrDefault(c => string.Equals(c.Id, connectionId, StringComparison.OrdinalIgnoreCase));

        if (connection is null)
        {
            throw new InvalidOperationException($"Connection '{connectionId}' was not found in sync-config.json.");
        }

        return connection;
    }

    public IReadOnlyList<SyncConnection> GetEnabledConnections() =>
        _configStore.Get().Connections
            .Where(c => c.IsEnabled)
            .OrderByDescending(c => c.IsMain)
            .ThenBy(c => c.Name)
            .ToList();

    public SyncConnection? GetMainConnection() =>
        _configStore.Get().Connections
            .FirstOrDefault(c => c.IsEnabled && c.IsMain);

    public IReadOnlyList<SyncConnection> GetTargetConnections() =>
        _configStore.Get().Connections
            .Where(c => c.IsEnabled && !c.IsMain)
            .OrderBy(c => c.Name)
            .ToList();

    public string BuildSafeConnectionSummary(string connectionId)
    {
        var options = GetConnection(connectionId);
        var authMode = options.IntegratedSecurity
            ? "Integrated Security"
            : $"SQL Auth (UserId={options.UserId ?? "(not set)"})";

        return $"Name={options.Name}; Server={options.Server}; Database={options.Database}; Auth={authMode}; " +
               $"Encrypt={options.Encrypt}; TrustServerCertificate={options.TrustServerCertificate}; " +
               $"Timeout={options.ConnectionTimeoutSeconds}s; IsMain={options.IsMain}";
    }

    private static string BuildConnectionString(SyncConnection options)
    {
        if (string.IsNullOrWhiteSpace(options.Server))
        {
            throw new InvalidOperationException($"Database Server is not configured for '{options.Name}'.");
        }

        if (string.IsNullOrWhiteSpace(options.Database))
        {
            throw new InvalidOperationException($"Database name is not configured for '{options.Name}'.");
        }

        var builder = new SqlConnectionStringBuilder
        {
            DataSource = options.Server,
            InitialCatalog = options.Database,
            ConnectTimeout = options.ConnectionTimeoutSeconds,
            Encrypt = options.Encrypt,
            TrustServerCertificate = options.TrustServerCertificate,
            ApplicationName = options.ApplicationName,
            PersistSecurityInfo = false
        };

        if (options.IntegratedSecurity)
        {
            builder.IntegratedSecurity = true;
        }
        else
        {
            builder.IntegratedSecurity = false;
            builder.UserID = options.UserId ?? string.Empty;
            builder.Password = options.Password ?? string.Empty;
        }

        return builder.ConnectionString;
    }
}
