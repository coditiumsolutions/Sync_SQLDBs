using DatabaseSync.Core.Enums;

namespace DatabaseSync.Core.Scheduling;

public static class IntervalSchedule
{
    public static TimeSpan ToTimeSpan(int value, SyncIntervalUnit unit)
    {
        if (value < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Interval value must be at least 1.");
        }

        var span = unit switch
        {
            SyncIntervalUnit.Minutes => TimeSpan.FromMinutes(value),
            SyncIntervalUnit.Hours => TimeSpan.FromHours(value),
            SyncIntervalUnit.Days => TimeSpan.FromDays(value),
            _ => throw new ArgumentOutOfRangeException(nameof(unit), unit, "Unknown interval unit.")
        };

        if (span < TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Interval must be at least 1 minute.");
        }

        return span;
    }

    /// <summary>The first interval slot is due immediately (next poll). Later slots are due at <paramref name="nextUtc"/>.</summary>
    public static bool IsDue(DateTimeOffset? nextUtc, DateTimeOffset nowUtc) =>
        nextUtc is null || nowUtc >= nextUtc.Value;

    /// <summary>
    /// Next slot after a run or a skipped slot. Missed ticks are not replayed:
    /// the following slot is one full interval after <paramref name="fromUtc"/>.
    /// </summary>
    public static DateTimeOffset After(DateTimeOffset fromUtc, int value, SyncIntervalUnit unit) =>
        fromUtc + ToTimeSpan(value, unit);
}
