namespace DatabaseSync.Core.Models;

/// <summary>
/// Worker synchronization behaviour settings.
/// </summary>
public sealed class SyncSettings
{
    /// <summary>Interval between connectivity / sync cycles, in seconds (kept for compatibility).</summary>
    public int SyncIntervalSeconds { get; set; } = 120;

    /// <summary>
    /// How often automatic sync should run while the service is running, in minutes.
    /// Preferred UI field; mirrored to SyncIntervalSeconds on save.
    /// </summary>
    public int AutoSyncEveryMinutes { get; set; } = 2;

    /// <summary>
    /// Local time (HH:mm) when automatic sync may start each day while the service is running.
    /// Example: 09:00
    /// </summary>
    public string AutoSyncStartTime { get; set; } = "00:00";

    /// <summary>
    /// Local time (HH:mm) when automatic sync must stop each day while the service is running.
    /// Example: 18:00. Use 00:00-23:59 for all day.
    /// </summary>
    public string AutoSyncEndTime { get; set; } = "23:59";

    /// <summary>When false (default), the worker only performs connectivity checks / stays idle for data sync.</summary>
    public bool EnableSynchronization { get; set; } = false;

    /// <summary>Delay between connection retry attempts, in seconds.</summary>
    public int RetryIntervalSeconds { get; set; } = 15;

    /// <summary>Maximum number of consecutive connection retry attempts per cycle.</summary>
    public int MaximumRetryAttempts { get; set; } = 2;

    /// <summary>When true, additional diagnostic details are written to the log.</summary>
    public bool EnableDetailedLogging { get; set; } = true;

    /// <summary>SQL command timeout used during table synchronisation.</summary>
    public int CommandTimeoutSeconds { get; set; } = 180;

    /// <summary>One-way sync from the nominated Main connection to all other enabled connections.</summary>
    public bool SyncMainToOthers { get; set; } = true;

    public int GetEffectiveIntervalSeconds()
    {
        if (AutoSyncEveryMinutes > 0)
        {
            return Math.Max(60, AutoSyncEveryMinutes * 60);
        }

        return Math.Max(30, SyncIntervalSeconds);
    }

    public bool IsWithinAutoSyncWindow(DateTime localNow)
    {
        if (!TryParseTime(AutoSyncStartTime, out var start) ||
            !TryParseTime(AutoSyncEndTime, out var end))
        {
            return true; // invalid config → do not block sync
        }

        var current = localNow.TimeOfDay;

        // Normal window, e.g. 09:00-18:00
        if (start <= end)
        {
            return current >= start && current <= end;
        }

        // Overnight window, e.g. 22:00-06:00
        return current >= start || current <= end;
    }

    private static bool TryParseTime(string? value, out TimeSpan time)
    {
        time = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return TimeSpan.TryParse(value, out time);
    }
}
