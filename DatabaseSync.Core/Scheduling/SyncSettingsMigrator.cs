using DatabaseSync.Core.Enums;
using DatabaseSync.Core.Models;

namespace DatabaseSync.Core.Scheduling;

/// <summary>
/// Moves the legacy EnableSynchronization / SyncIntervalSeconds / AutoSyncEveryMinutes switches
/// onto interval sync once. After version 1 those legacy fields are mirrors and are not read.
/// </summary>
public static class SyncSettingsMigrator
{
    public const int CurrentVersion = 1;

    /// <returns>True when the file should be rewritten (version upgrade or new schedule ids).</returns>
    public static bool Apply(SyncSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Schedules ??= [];
        settings.ChangeTimestampColumnNames ??= [];
        var durable = false;

        if (settings.TriggerConfigVersion < CurrentVersion)
        {
            if (settings.EnableSynchronization)
            {
                settings.IntervalSyncEnabled = true;
                ApplyLegacyInterval(settings);
            }

            settings.TriggerConfigVersion = CurrentVersion;
            durable = true;
        }

        if (settings.IntervalValue < 1)
        {
            settings.IntervalValue = 1;
        }

        if (!Enum.IsDefined(settings.IntervalUnit))
        {
            settings.IntervalUnit = SyncIntervalUnit.Minutes;
        }

        settings.TriggerPollSeconds = Math.Clamp(settings.TriggerPollSeconds, 1, 120);
        settings.ScheduleGraceMinutes = Math.Clamp(settings.ScheduleGraceMinutes, 1, 24 * 60);
        settings.ConflictFileRowThreshold = Math.Max(1, settings.ConflictFileRowThreshold);
        settings.ConflictFileMaxBytes = Math.Max(1024, settings.ConflictFileMaxBytes);
        settings.FingerprintBatchSize = Math.Max(100, settings.FingerprintBatchSize);
        settings.DiffApplyBatchSize = Math.Max(1, settings.DiffApplyBatchSize);

        if (string.IsNullOrWhiteSpace(settings.ConflictBackupFolder))
        {
            settings.ConflictBackupFolder = "Conflicts";
        }

        foreach (var schedule in settings.Schedules)
        {
            schedule.DaysOfWeek ??= [];
            if (string.IsNullOrWhiteSpace(schedule.Id))
            {
                schedule.Id = Guid.NewGuid().ToString("N");
                durable = true;
            }
        }

        MirrorLegacyFields(settings);
        return durable;
    }

    private static void ApplyLegacyInterval(SyncSettings settings)
    {
        if (settings.AutoSyncEveryMinutes > 0)
        {
            settings.IntervalValue = settings.AutoSyncEveryMinutes;
            settings.IntervalUnit = SyncIntervalUnit.Minutes;
            return;
        }

        var seconds = Math.Max(60, settings.SyncIntervalSeconds);
        if (seconds >= 86_400 && seconds % 86_400 == 0)
        {
            settings.IntervalValue = seconds / 86_400;
            settings.IntervalUnit = SyncIntervalUnit.Days;
            return;
        }

        if (seconds >= 3_600 && seconds % 3_600 == 0)
        {
            settings.IntervalValue = seconds / 3_600;
            settings.IntervalUnit = SyncIntervalUnit.Hours;
            return;
        }

        settings.IntervalValue = Math.Max(1, (int)Math.Ceiling(seconds / 60d));
        settings.IntervalUnit = SyncIntervalUnit.Minutes;
    }

    private static void MirrorLegacyFields(SyncSettings settings)
    {
        settings.EnableSynchronization = settings.IntervalSyncEnabled || settings.ScheduledSyncEnabled;
        var seconds = (long)IntervalSchedule.ToTimeSpan(settings.IntervalValue, settings.IntervalUnit).TotalSeconds;
        settings.SyncIntervalSeconds = (int)Math.Min(int.MaxValue, Math.Max(60, seconds));
        var minutes = (int)Math.Round(IntervalSchedule.ToTimeSpan(settings.IntervalValue, settings.IntervalUnit).TotalMinutes);
        settings.AutoSyncEveryMinutes = Math.Max(1, minutes);
    }
}
