namespace DatabaseSync.Core.Models;

/// <summary>
/// One scheduled sync occurrence. Interpreted in the host's local time zone.
/// A cron expression, when set, replaces <see cref="Time"/> and <see cref="DaysOfWeek"/>.
/// </summary>
public sealed class SyncScheduleEntry
{
    /// <summary>Stable id so occurrence tracking survives reordering. Assigned on save when empty.</summary>
    public string Id { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    /// <summary>Local time HH:mm. Used when <see cref="Cron"/> is empty.</summary>
    public string Time { get; set; } = "02:00";

    /// <summary>
    /// Day names (Sunday … Saturday). Empty means every day.
    /// Ignored when <see cref="Cron"/> is set.
    /// </summary>
    public List<string> DaysOfWeek { get; set; } = [];

    /// <summary>
    /// Optional 5-field cron in local time: minute hour day-of-month month day-of-week.
    /// Example: <c>0 9 * * 1-5</c> is 09:00 on weekdays.
    /// When both day-of-month and day-of-week are restricted, either match fires (standard cron).
    /// </summary>
    public string? Cron { get; set; }
}
