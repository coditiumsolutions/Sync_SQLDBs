using DatabaseSync.Core.Enums;
using DatabaseSync.Core.Models;
using DatabaseSync.Core.Scheduling;

namespace DatabaseSync.Admin.Services;

/// <summary>
/// Validates and copies the Triggers form onto <see cref="SyncSettings"/>.
/// Manual sync is not a setting. One-way sync stays as stored unless the operator restores it.
/// </summary>
public static class TriggerSettingsEditor
{
    public static readonly string[] WeekDays =
    [
        "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday"
    ];

    public static IReadOnlyList<string> Validate(DatabaseSync.Admin.Pages.SettingsModel.InputModel input)
    {
        var errors = new List<string>();
        if (!Enum.TryParse<SyncIntervalUnit>(input.IntervalUnit, ignoreCase: true, out _))
        {
            errors.Add("Input.IntervalUnit|Interval unit must be Minutes, Hours, or Days.");
        }

        if (input.IntervalValue < 1)
        {
            errors.Add("Input.IntervalValue|Interval value must be at least 1.");
        }

        var schedules = input.Schedules ?? [];
        for (var i = 0; i < schedules.Count; i++)
        {
            var schedule = schedules[i];
            schedule.DaysOfWeek ??= [];
            if (schedule.Remove || IsBlank(schedule))
            {
                continue;
            }

            var prefix = $"Input.Schedules[{i}]";
            if (schedule.UseCron)
            {
                string? error = null;
                if (string.IsNullOrWhiteSpace(schedule.Cron) ||
                    !CronExpression.TryParse(schedule.Cron, out _, out error))
                {
                    errors.Add($"{prefix}.Cron|{error ?? "Enter a 5-field cron expression."}");
                }
            }
            else if (!SyncSchedulePlanner.TryParseTime(schedule.Time, out _))
            {
                errors.Add($"{prefix}.Time|Use HH:mm, for example 09:00.");
            }

            if (!schedule.UseCron)
            {
                foreach (var day in schedule.DaysOfWeek)
                {
                    if (!WeekDays.Contains(day, StringComparer.OrdinalIgnoreCase))
                    {
                        errors.Add($"{prefix}.DaysOfWeek|Unknown day '{day}'.");
                    }
                }
            }
        }

        return errors;
    }

    public static void Apply(SyncSettings settings, DatabaseSync.Admin.Pages.SettingsModel.InputModel input)
    {
        var kept = (input.Schedules ?? [])
            .Where(schedule => !schedule.Remove && !IsBlank(schedule))
            .ToList();

        settings.ScheduledSyncEnabled = input.ScheduledSyncEnabled;
        settings.Schedules = kept.Select(ToEntry).ToList();
        settings.IntervalSyncEnabled = input.IntervalSyncEnabled;
        settings.IntervalValue = input.IntervalValue;
        settings.IntervalUnit = Enum.Parse<SyncIntervalUnit>(input.IntervalUnit, ignoreCase: true);
        settings.RetryIntervalSeconds = input.RetryIntervalSeconds;
        settings.MaximumRetryAttempts = input.MaximumRetryAttempts;
        settings.CommandTimeoutSeconds = input.CommandTimeoutSeconds;
        settings.EnableDetailedLogging = input.EnableDetailedLogging;
        settings.DryRun = input.DryRun;
        settings.ConflictBackupFolder = input.ConflictBackupFolder.Trim();
        settings.ConflictFileRowThreshold = input.ConflictFileRowThreshold;
        settings.ConflictFileMaxBytes = input.ConflictFileMaxMegabytes * 1_000_000L;
        settings.UseConflictBackupTables = input.UseConflictBackupTables;
        settings.WriteConflictLogTable = input.WriteConflictLogTable;
        settings.AssumeDifferingRowsConflict = input.AssumeDifferingRowsConflict;
        settings.ChangeTimestampColumnNames = ParseNames(input.ChangeTimestampColumnNames);
        if (input.RestoreOneWaySync)
        {
            settings.SyncMainToOthers = true;
        }
    }

    private static bool IsBlank(DatabaseSync.Admin.Pages.SettingsModel.ScheduleInput schedule) =>
        !schedule.UseCron &&
        string.IsNullOrWhiteSpace(schedule.Time) &&
        string.IsNullOrWhiteSpace(schedule.Cron);

    private static SyncScheduleEntry ToEntry(DatabaseSync.Admin.Pages.SettingsModel.ScheduleInput schedule)
    {
        if (schedule.UseCron)
        {
            return new SyncScheduleEntry
            {
                Id = string.IsNullOrWhiteSpace(schedule.Id) ? Guid.NewGuid().ToString("N") : schedule.Id.Trim(),
                Enabled = schedule.Enabled,
                Time = "00:00",
                DaysOfWeek = [],
                Cron = schedule.Cron!.Trim()
            };
        }

        return new SyncScheduleEntry
        {
            Id = string.IsNullOrWhiteSpace(schedule.Id) ? Guid.NewGuid().ToString("N") : schedule.Id.Trim(),
            Enabled = schedule.Enabled,
            Time = schedule.Time!.Trim(),
            DaysOfWeek = schedule.DaysOfWeek
                .Select(day => WeekDays.First(name => name.Equals(day, StringComparison.OrdinalIgnoreCase)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            Cron = null
        };
    }

    private static List<string> ParseNames(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        return text.Split([',', '\n', '\r', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
