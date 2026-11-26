using DatabaseSync.Core.DTOs;

namespace DatabaseSync.Core.Models;

/// <summary>Copies a connectivity probe onto the connection so the Admin UI can show the last result.</summary>
public static class ConnectivityProbe
{
    public static void Apply(SyncConnection connection, DatabaseConnectivityResult result)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(result);

        connection.LastConnectivityStatus = result.Status;
        connection.LastConnectivityCheckedAtUtc = result.CheckedAtUtc;
        connection.LastConnectivityElapsedMs = (long)Math.Round(result.Elapsed.TotalMilliseconds);
        connection.LastConnectivityServerVersion = result.ServerVersion;
        connection.LastConnectivityError = result.IsSuccessful ? null : result.ErrorMessage;
    }
}
