using DatabaseSync.Core.Enums;
using DatabaseSync.Core.Models;
using DatabaseSync.Core.Scheduling;

namespace DatabaseSync.Core.Tests;

public class CronExpressionTests
{
    [Fact]
    public void Parses_lists_ranges_steps_and_names()
    {
        Assert.True(CronExpression.TryParse("0 9 * * 1-5", out var weekdays, out _));
        Assert.True(weekdays!.IsMatch(new DateTime(2026, 11, 10, 9, 0, 0)));
        Assert.False(weekdays.IsMatch(new DateTime(2026, 11, 10, 9, 15, 0)));
        Assert.False(weekdays.IsMatch(new DateTime(2026, 11, 15, 9, 0, 0)));

        Assert.True(CronExpression.TryParse("*/15 8-17 * * MON-FRI", out var office, out _));
        Assert.True(office!.IsMatch(new DateTime(2026, 11, 10, 8, 15, 0)));
        Assert.False(office.IsMatch(new DateTime(2026, 11, 10, 8, 10, 0)));
        Assert.False(office.IsMatch(new DateTime(2026, 11, 15, 8, 15, 0)));

        Assert.True(CronExpression.TryParse("0 0 1 1 *", out var newYear, out _));
        Assert.True(newYear!.IsMatch(new DateTime(2026, 1, 1, 0, 0, 0)));
        Assert.False(newYear.IsMatch(new DateTime(2026, 1, 2, 0, 0, 0)));
    }

    [Fact]
    public void Restricted_day_of_month_or_day_of_week_matches_either()
    {
        Assert.True(CronExpression.TryParse("0 9 1 * FRI", out var cron, out _));
        Assert.True(cron!.IsMatch(new DateTime(2026, 11, 1, 9, 0, 0)));
        Assert.True(cron.IsMatch(new DateTime(2026, 11, 13, 9, 0, 0)));
        Assert.False(cron.IsMatch(new DateTime(2026, 11, 10, 9, 0, 0)));
    }

    [Fact]
    public void Friday_through_Sunday_includes_Sunday()
    {
        Assert.True(CronExpression.TryParse("0 1 * * FRI-SUN", out var cron, out _));
        Assert.True(cron!.IsMatch(new DateTime(2026, 11, 13, 1, 0, 0)));
        Assert.True(cron.IsMatch(new DateTime(2026, 11, 15, 1, 0, 0)));
        Assert.False(cron.IsMatch(new DateTime(2026, 11, 10, 1, 0, 0)));
    }

    [Fact]
    public void Rejects_malformed_expressions()
    {
        Assert.False(CronExpression.TryParse("0 9 * *", out _, out var error));
        Assert.Contains("5 fields", error, StringComparison.Ordinal);
    }
}

public class SyncSchedulePlannerTests
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.Utc;

    [Fact]
    public void Next_daily_time_is_later_today_or_tomorrow()
    {
        var schedules = new List<SyncScheduleEntry> { Entry("09:00") };
        var morning = Utc(2026, 11, 10, 8, 0);
        var next = SyncSchedulePlanner.GetNextRunUtc(schedules, morning, Zone, TimeSpan.FromMinutes(15), null);
        Assert.Equal(Utc(2026, 11, 10, 9, 0), next);

        var afternoon = Utc(2026, 11, 10, 9, 30);
        next = SyncSchedulePlanner.GetNextRunUtc(schedules, afternoon, Zone, TimeSpan.FromMinutes(15), null);
        Assert.Equal(Utc(2026, 11, 11, 9, 0), next);
    }

    [Fact]
    public void Due_inside_grace_fires_once()
    {
        var entry = Entry("09:00");
        var now = Utc(2026, 11, 10, 9, 5);
        var due = SyncSchedulePlanner.GetDue([entry], now, Zone, TimeSpan.FromMinutes(15), null);
        Assert.Single(due);
        Assert.Equal(Utc(2026, 11, 10, 9, 0), due[0].OccurrenceUtc);

        var fired = new Dictionary<string, DateTimeOffset> { [due[0].OccurrenceKey] = due[0].OccurrenceUtc };
        Assert.Empty(SyncSchedulePlanner.GetDue([entry], now, Zone, TimeSpan.FromMinutes(15), fired));
    }

    [Fact]
    public void Occurrence_outside_grace_is_missed()
    {
        var now = Utc(2026, 11, 10, 9, 20);
        var due = SyncSchedulePlanner.GetDue([Entry("09:00")], now, Zone, TimeSpan.FromMinutes(15), null);
        Assert.Empty(due);
    }

    [Fact]
    public void Day_filter_skips_to_the_next_matching_day()
    {
        var entry = Entry("09:00", "Monday");
        var tuesday = Utc(2026, 11, 10, 8, 0);
        var next = SyncSchedulePlanner.GetNextRunUtc([entry], tuesday, Zone, TimeSpan.FromMinutes(15), null);
        Assert.Equal(Utc(2026, 11, 16, 9, 0), next);
        Assert.Empty(SyncSchedulePlanner.GetDue([entry], Utc(2026, 11, 10, 9, 5), Zone, TimeSpan.FromMinutes(15), null));
    }

    [Fact]
    public void Disabled_schedules_and_a_disabled_mode_do_not_produce_a_next_run()
    {
        var disabled = Entry("09:00");
        disabled.Enabled = false;
        Assert.Null(SyncSchedulePlanner.GetNextRunUtc([disabled], Utc(2026, 11, 10, 8, 0), Zone, TimeSpan.FromMinutes(15), null));
        Assert.Null(SyncSchedulePlanner.GetNextRunUtc([], Utc(2026, 11, 10, 8, 0), Zone, TimeSpan.FromMinutes(15), null));
    }

    [Fact]
    public void Cron_schedule_reports_the_next_minute_match()
    {
        var entry = new SyncScheduleEntry { Id = "cron", Cron = "0 9 * * 1-5", Enabled = true };
        var next = SyncSchedulePlanner.GetNextRunUtc([entry], Utc(2026, 11, 10, 10, 0), Zone, TimeSpan.FromMinutes(15), null);
        Assert.Equal(Utc(2026, 11, 11, 9, 0), next);
    }

    [Fact]
    public void The_earliest_of_several_schedules_wins()
    {
        var schedules = new List<SyncScheduleEntry> { Entry("18:00", id: "evening"), Entry("09:30", id: "morning") };
        var next = SyncSchedulePlanner.GetNextRunUtc(schedules, Utc(2026, 11, 10, 8, 0), Zone, TimeSpan.FromMinutes(15), null);
        Assert.Equal(Utc(2026, 11, 10, 9, 30), next);
    }

    private static SyncScheduleEntry Entry(string time, string? day = null, string id = "daily") => new()
    {
        Id = id,
        Enabled = true,
        Time = time,
        DaysOfWeek = day is null ? [] : [day]
    };

    private static DateTimeOffset Utc(int year, int month, int day, int hour, int minute) =>
        new(year, month, day, hour, minute, 0, TimeSpan.Zero);
}

public class IntervalScheduleTests
{
    [Fact]
    public void First_slot_is_due_and_the_next_slot_waits_a_full_interval()
    {
        var now = new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
        Assert.True(IntervalSchedule.IsDue(null, now));
        Assert.False(IntervalSchedule.IsDue(now.AddMinutes(1), now));
        Assert.Equal(now.AddHours(2), IntervalSchedule.After(now, 2, SyncIntervalUnit.Hours));
        Assert.Equal(now.AddDays(1), IntervalSchedule.After(now, 1, SyncIntervalUnit.Days));
    }

    [Fact]
    public void Rejects_a_zero_interval()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => IntervalSchedule.ToTimeSpan(0, SyncIntervalUnit.Minutes));
    }
}

public class SyncTriggerSelectorTests
{
    [Fact]
    public void Manual_beats_schedule_which_beats_interval()
    {
        Assert.Equal(SyncTriggerKind.Manual, SyncTriggerSelector.Select(true, true, true, true, true)!.Value.Kind);
        Assert.Equal(SyncTriggerKind.Scheduled, SyncTriggerSelector.Select(false, true, true, true, true)!.Value.Kind);
        Assert.Equal(SyncTriggerKind.Interval, SyncTriggerSelector.Select(false, true, false, true, true)!.Value.Kind);
        Assert.Null(SyncTriggerSelector.Select(false, false, true, false, true));
        Assert.Null(SyncTriggerSelector.Select(false, true, false, true, false));
    }
}

public class SyncOverlapGuardTests
{
    [Fact]
    public void A_second_trigger_is_rejected_until_the_first_exits()
    {
        var guard = new SyncOverlapGuard();
        Assert.True(guard.TryEnter(SyncTriggerKind.Scheduled, out var active));
        Assert.Null(active);

        Assert.False(guard.TryEnter(SyncTriggerKind.Manual, out active));
        Assert.Equal(SyncTriggerKind.Scheduled, active);
        Assert.Equal(SyncTriggerKind.Scheduled, guard.Active);

        guard.Exit();
        Assert.True(guard.TryEnter(SyncTriggerKind.Interval, out _));
        guard.Exit();
        guard.Exit();
        Assert.Null(guard.Active);
    }
}

public class SyncSettingsMigratorTests
{
    [Fact]
    public void Legacy_enabled_sync_becomes_interval_sync_once()
    {
        var settings = new SyncSettings
        {
            TriggerConfigVersion = 0,
            EnableSynchronization = true,
            AutoSyncEveryMinutes = 5,
            SyncIntervalSeconds = 30
        };

        Assert.True(SyncSettingsMigrator.Apply(settings));
        Assert.True(settings.IntervalSyncEnabled);
        Assert.Equal(5, settings.IntervalValue);
        Assert.Equal(SyncIntervalUnit.Minutes, settings.IntervalUnit);
        Assert.Equal(1, settings.TriggerConfigVersion);
        Assert.True(settings.EnableSynchronization);
        Assert.Equal(300, settings.SyncIntervalSeconds);

        settings.IntervalSyncEnabled = false;
        settings.EnableSynchronization = true;
        Assert.False(SyncSettingsMigrator.Apply(settings));
        Assert.False(settings.IntervalSyncEnabled);
        Assert.False(settings.EnableSynchronization);
    }

    [Fact]
    public void Legacy_disabled_sync_stays_off()
    {
        var settings = new SyncSettings
        {
            TriggerConfigVersion = 0,
            EnableSynchronization = false,
            AutoSyncEveryMinutes = 2
        };

        Assert.True(SyncSettingsMigrator.Apply(settings));
        Assert.False(settings.IntervalSyncEnabled);
        Assert.False(settings.ScheduledSyncEnabled);
        Assert.False(settings.EnableSynchronization);
    }

    [Fact]
    public void Legacy_hour_interval_is_preserved_when_minute_mirror_is_absent()
    {
        var settings = new SyncSettings
        {
            TriggerConfigVersion = 0,
            EnableSynchronization = true,
            AutoSyncEveryMinutes = 0,
            SyncIntervalSeconds = 7200
        };

        SyncSettingsMigrator.Apply(settings);
        Assert.Equal(SyncIntervalUnit.Hours, settings.IntervalUnit);
        Assert.Equal(2, settings.IntervalValue);
    }
}
