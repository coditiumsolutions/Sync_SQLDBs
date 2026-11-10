using System.Data.Common;
using DatabaseSync.Core.Models;

namespace DatabaseSync.Core.Interfaces;

/// <summary>
/// Builds ADO.NET connections for configured databases without exposing credentials in logs.
/// </summary>
public interface ISqlConnectionFactory
{
    DbConnection CreateConnection(string connectionId);

    SyncConnection GetConnection(string connectionId);

    IReadOnlyList<SyncConnection> GetEnabledConnections();

    SyncConnection? GetMainConnection();

    IReadOnlyList<SyncConnection> GetTargetConnections();

    string BuildSafeConnectionSummary(string connectionId);
}
