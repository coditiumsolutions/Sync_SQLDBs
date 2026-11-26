using DatabaseSync.Core.Enums;
using DatabaseSync.Core.Models;

namespace DatabaseSync.Admin.Services;

/// <summary>Labels and icon names for the last connectivity probe on a connection.</summary>
public static class ConnectionStatusDisplay
{
    public static string Label(ConnectionStatus status) => status switch
    {
        ConnectionStatus.Connected => "Connected",
        ConnectionStatus.Failed => "Failed",
        ConnectionStatus.TimedOut => "Timed out",
        ConnectionStatus.Cancelled => "Cancelled",
        _ => "Not tested"
    };

    public static string PillClass(ConnectionStatus status) => status switch
    {
        ConnectionStatus.Connected => "mark-ok",
        ConnectionStatus.Failed => "mark-bad",
        ConnectionStatus.TimedOut => "run",
        ConnectionStatus.Cancelled => "mark-muted",
        _ => "mark-muted"
    };

    public static string Icon(ConnectionStatus status) => status switch
    {
        ConnectionStatus.Connected => "check",
        ConnectionStatus.Failed => "x",
        ConnectionStatus.TimedOut => "clock",
        ConnectionStatus.Cancelled => "cancel",
        _ => "dot"
    };

    public static string Detail(SyncConnection connection)
    {
        if (connection.LastConnectivityStatus == ConnectionStatus.Unknown ||
            connection.LastConnectivityCheckedAtUtc is null)
        {
            return "Run Test to probe this endpoint.";
        }

        var when = connection.LastConnectivityCheckedAtUtc.Value.ToLocalTime().ToString("g");
        var elapsed = connection.LastConnectivityElapsedMs is long ms ? $"{ms} ms" : "—";
        if (connection.LastConnectivityStatus == ConnectionStatus.Connected)
        {
            var version = string.IsNullOrWhiteSpace(connection.LastConnectivityServerVersion)
                ? ""
                : $" · {TrimVersion(connection.LastConnectivityServerVersion)}";
            return $"{when} · {elapsed}{version}";
        }

        var error = string.IsNullOrWhiteSpace(connection.LastConnectivityError)
            ? Label(connection.LastConnectivityStatus)
            : connection.LastConnectivityError!;
        return $"{when} · {elapsed} · {error}";
    }

    private static string TrimVersion(string version)
    {
        var trimmed = version.Trim();
        return trimmed.Length <= 48 ? trimmed : trimmed[..45] + "...";
    }
}
