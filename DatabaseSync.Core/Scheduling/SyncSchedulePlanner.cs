using DatabaseSync.Core.Models;

namespace DatabaseSync.Core.Scheduling;

public readonly record struct ScheduleHit(string EntryId, string Description, DateTimeOffset OccurrenceUtc, string OccurrenceKey);

/// <summary>
/// Local-time schedule math. Each entry fires once per occurrence.
/// An occurrence is due when it is at or before now, inside the grace window, and has not been marked fired.
/// Occurrences older than the grace window are missed rather than replayed.
/// </summary>
public static class SyncSchedulePlanner
{
    public static IReadOnlyList<ScheduleHit> GetDue(
        IReadOnlyList<SyncScheduleEntry>? schedules,
        DateTimeOffset nowUtc,
        TimeZoneInfo timeZone,
        TimeSpan grace,
        IReadOnlyDictionary<string, DateTimeOffset>? alreadyFired)
    {
        ArgumentNullException.ThrowIfNull(timeZone);
        if (schedules is null || schedules.Count == 0 || grace < TimeSpan.Zero)
        {
            return [];
        }

        alreadyFired ??= new Dictionary<string, DateTimeOffset>();
        var hits = new List<ScheduleHit>();
        for (var index = 0; index < schedules.Count; index++)
        {
            var entry = schedules[index];
            if (!entry.Enabled)
            {
                continue;
            }

            if (TryGetDue(entry, index, nowUtc, timeZone, grace, alreadyFired, out var hit))
            {
                hits.Add(hit);
            }
        }

        return hits;
    }

    public static DateTimeOffset? GetNextRunUtc(
        IReadOnlyList<SyncScheduleEntry>? schedules,
        DateTimeOffset nowUtc,
        TimeZoneInfo timeZone,
        TimeSpan grace,
        IReadOnlyDictionary<string, DateTimeOffset>? alreadyFired)
    {
        var due = GetDue(schedules, nowUtc, timeZone, grace, alreadyFired);
        if (due.Count > 0)
        {
            return due.Min(hit => hit.OccurrenceUtc);
        }

        if (schedules is null || schedules.Count == 0)
        {
            return null;
        }

        DateTimeOffset? next = null;
        for (var index = 0; index < schedules.Count; index++)
        {
            var entry = schedules[index];
            if (!entry.Enabled)
            {
                continue;
            }

            var candidate = NextFuture(entry, nowUtc, timeZone);
            if (candidate is null)
            {
                continue;
            }

            if (next is null || candidate.Value < next.Value)
            {
                next = candidate;
            }
        }

        return next;
    }

    private static bool TryGetDue(
        SyncScheduleEntry entry,
        int index,
        DateTimeOffset nowUtc,
        TimeZoneInfo timeZone,
        TimeSpan grace,
        IReadOnlyDictionary<string, DateTimeOffset> alreadyFired,
        out ScheduleHit hit)
    {
        hit = default;
        ScheduleHit? best = null;

        if (!string.IsNullOrWhiteSpace(entry.Cron))
        {
            if (!CronExpression.TryParse(entry.Cron, out var cron, out _) || cron is null)
            {
                return false;
            }

            var localNow = TimeZoneInfo.ConvertTime(nowUtc, timeZone).DateTime;
            var minute = TruncateToMinute(localNow);
            var graceMinutes = (int)Math.Floor(grace.TotalMinutes);
            for (var age = 0; age <= graceMinutes; age++)
            {
                var candidate = minute.AddMinutes(-age);
                if (!cron.IsMatch(candidate))
                {
                    continue;
                }

                var utc = ToUtc(candidate, timeZone);
                if (utc > nowUtc || nowUtc - utc > grace)
                {
                    continue;
                }

                var possible = CreateHit(entry, index, utc, timeZone, $"cron {entry.Cron}");
                if (alreadyFired.ContainsKey(possible.OccurrenceKey))
                {
                    continue;
                }

                best = possible;
                break;
            }
        }
        else
        {
            if (!TryParseTime(entry.Time, out var time))
            {
                return false;
            }

            var localNow = TimeZoneInfo.ConvertTime(nowUtc, timeZone).DateTime;
            for (var dayOffset = 0; dayOffset >= -1; dayOffset--)
            {
                var date = localNow.Date.AddDays(dayOffset);
                if (!DayMatches(entry, date.DayOfWeek))
                {
                    continue;
                }

                var localOccurrence = date.Add(time);
                var utc = ToUtc(localOccurrence, timeZone);
                if (utc > nowUtc || nowUtc - utc > grace)
                {
                    continue;
                }

                var possible = CreateHit(entry, index, utc, timeZone, entry.Time);
                if (alreadyFired.ContainsKey(possible.OccurrenceKey))
                {
                    continue;
                }

                if (best is null || possible.OccurrenceUtc > best.Value.OccurrenceUtc)
                {
                    best = possible;
                }
            }
        }

        if (best is null)
        {
            return false;
        }

        hit = best.Value;
        return true;
    }

    private static DateTimeOffset? NextFuture(SyncScheduleEntry entry, DateTimeOffset nowUtc, TimeZoneInfo timeZone)
    {
        var localNow = TimeZoneInfo.ConvertTime(nowUtc, timeZone).DateTime;

        if (!string.IsNullOrWhiteSpace(entry.Cron))
        {
            if (!CronExpression.TryParse(entry.Cron, out var cron, out _) || cron is null)
            {
                return null;
            }

            var cursor = TruncateToMinute(localNow).AddMinutes(1);
            var limit = cursor.AddDays(366);
            while (cursor < limit)
            {
                if (cron.IsMatch(cursor))
                {
                    var utc = ToUtc(cursor, timeZone);
                    if (utc > nowUtc)
                    {
                        return utc;
                    }
                }

                cursor = cursor.AddMinutes(1);
            }

            return null;
        }

        if (!TryParseTime(entry.Time, out var time))
        {
            return null;
        }

        for (var day = 0; day <= 8; day++)
        {
            var date = localNow.Date.AddDays(day);
            if (!DayMatches(entry, date.DayOfWeek))
            {
                continue;
            }

            var utc = ToUtc(date.Add(time), timeZone);
            if (utc > nowUtc)
            {
                return utc;
            }
        }

        return null;
    }

    private static ScheduleHit CreateHit(
        SyncScheduleEntry entry,
        int index,
        DateTimeOffset occurrenceUtc,
        TimeZoneInfo timeZone,
        string description)
    {
        var id = string.IsNullOrWhiteSpace(entry.Id) ? $"idx-{index}" : entry.Id;
        var local = TimeZoneInfo.ConvertTime(occurrenceUtc, timeZone);
        var key = $"{id}|{local:yyyy-MM-ddTHH:mm}";
        return new ScheduleHit(id, description, occurrenceUtc, key);
    }

    public static bool DayMatches(SyncScheduleEntry entry, DayOfWeek day)
    {
        if (entry.DaysOfWeek is null || entry.DaysOfWeek.Count == 0)
        {
            return true;
        }

        foreach (var name in entry.DaysOfWeek)
        {
            if (Enum.TryParse<DayOfWeek>(name, ignoreCase: true, out var parsed) && parsed == day)
            {
                return true;
            }
        }

        return false;
    }

    public static bool TryParseTime(string? value, out TimeSpan time)
    {
        time = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (!TimeSpan.TryParse(value, out time))
        {
            return false;
        }

        if (time < TimeSpan.Zero || time >= TimeSpan.FromDays(1) || time.Seconds != 0 || time.Milliseconds != 0)
        {
            return false;
        }

        return true;
    }

    private static DateTime TruncateToMinute(DateTime value) =>
        new(value.Year, value.Month, value.Day, value.Hour, value.Minute, 0, value.Kind);

    public static DateTimeOffset ToUtc(DateTime localUnspecified, TimeZoneInfo timeZone)
    {
        var unspecified = DateTime.SpecifyKind(localUnspecified, DateTimeKind.Unspecified);
        var utc = TimeZoneInfo.ConvertTimeToUtc(unspecified, timeZone);
        return new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
    }
}
