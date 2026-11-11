using System.ComponentModel.DataAnnotations;
using DatabaseSync.Core.Enums;
using DatabaseSync.Core.Interfaces;
using DatabaseSync.Core.Models;
using DatabaseSync.Core.Scheduling;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DatabaseSync.Admin.Pages;

public class SettingsModel : PageModel
{
    public static readonly string[] WeekDays =
    [
        "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday"
    ];

    private readonly ISyncConfigStore _configStore;

    public SettingsModel(ISyncConfigStore configStore)
    {
        _configStore = configStore;
    }

    public string ConfigPath { get; private set; } = string.Empty;

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public async Task OnGetAsync()
    {
        var config = await _configStore.GetAsync();
        ConfigPath = _configStore.ConfigFilePath;
        Input = FromSettings(config.SyncSettings);
    }

    public IActionResult OnPostAddSchedule()
    {
        ConfigPath = _configStore.ConfigFilePath;
        NormalizeSchedules();
        Input.Schedules.Add(new ScheduleInput { Time = "02:00", Enabled = true });
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        ConfigPath = _configStore.ConfigFilePath;
        NormalizeSchedules();
        Validate();
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var kept = Input.Schedules.Where(schedule => !schedule.Remove && !IsBlank(schedule)).ToList();

        var config = await _configStore.GetAsync();
        var settings = config.SyncSettings;
        settings.ScheduledSyncEnabled = Input.ScheduledSyncEnabled;
        settings.Schedules = kept.Select(ToEntry).ToList();
        settings.IntervalSyncEnabled = Input.IntervalSyncEnabled;
        settings.IntervalValue = Input.IntervalValue;
        settings.IntervalUnit = Enum.Parse<SyncIntervalUnit>(Input.IntervalUnit, ignoreCase: true);
        settings.SyncMainToOthers = Input.SyncMainToOthers;
        settings.RetryIntervalSeconds = Input.RetryIntervalSeconds;
        settings.MaximumRetryAttempts = Input.MaximumRetryAttempts;
        settings.CommandTimeoutSeconds = Input.CommandTimeoutSeconds;
        settings.EnableDetailedLogging = Input.EnableDetailedLogging;
        settings.DryRun = Input.DryRun;
        settings.ConflictBackupFolder = Input.ConflictBackupFolder.Trim();
        settings.ConflictFileRowThreshold = Input.ConflictFileRowThreshold;
        settings.ConflictFileMaxBytes = Input.ConflictFileMaxMegabytes * 1_000_000L;
        settings.UseConflictBackupTables = Input.UseConflictBackupTables;
        settings.WriteConflictLogTable = Input.WriteConflictLogTable;
        settings.AssumeDifferingRowsConflict = Input.AssumeDifferingRowsConflict;
        settings.ChangeTimestampColumnNames = ParseNames(Input.ChangeTimestampColumnNames);

        await _configStore.SaveAsync(config);
        TempData["StatusMessage"] =
            "Settings saved. Manual sync is always available. Scheduled and interval sync run only while the worker service is running, and only one sync runs at a time.";
        return RedirectToPage();
    }

    private void NormalizeSchedules()
    {
        Input.Schedules ??= [];
        foreach (var schedule in Input.Schedules)
        {
            schedule.DaysOfWeek ??= [];
        }
    }

    private void Validate()
    {
        if (!Enum.TryParse<SyncIntervalUnit>(Input.IntervalUnit, ignoreCase: true, out _))
        {
            ModelState.AddModelError("Input.IntervalUnit", "Interval unit must be Minutes, Hours, or Days.");
        }

        if (Input.IntervalValue < 1)
        {
            ModelState.AddModelError("Input.IntervalValue", "Interval value must be at least 1.");
        }

        for (var i = 0; i < Input.Schedules.Count; i++)
        {
            var schedule = Input.Schedules[i];
            if (schedule.Remove || IsBlank(schedule))
            {
                continue;
            }

            var prefix = $"Input.Schedules[{i}]";
            if (!string.IsNullOrWhiteSpace(schedule.Cron))
            {
                if (!CronExpression.TryParse(schedule.Cron, out _, out var error))
                {
                    ModelState.AddModelError($"{prefix}.Cron", error ?? "Invalid cron expression.");
                }
            }
            else if (!SyncSchedulePlanner.TryParseTime(schedule.Time, out _))
            {
                ModelState.AddModelError($"{prefix}.Time", "Use HH:mm, for example 09:00.");
            }

            foreach (var day in schedule.DaysOfWeek)
            {
                if (!WeekDays.Contains(day, StringComparer.OrdinalIgnoreCase))
                {
                    ModelState.AddModelError($"{prefix}.DaysOfWeek", $"Unknown day '{day}'.");
                }
            }
        }
    }

    private static bool IsBlank(ScheduleInput schedule) =>
        string.IsNullOrWhiteSpace(schedule.Time) && string.IsNullOrWhiteSpace(schedule.Cron);

    private static SyncScheduleEntry ToEntry(ScheduleInput schedule) => new()
    {
        Id = string.IsNullOrWhiteSpace(schedule.Id) ? Guid.NewGuid().ToString("N") : schedule.Id.Trim(),
        Enabled = schedule.Enabled,
        Time = string.IsNullOrWhiteSpace(schedule.Time) ? "00:00" : schedule.Time.Trim(),
        DaysOfWeek = schedule.DaysOfWeek
            .Select(day => WeekDays.First(name => name.Equals(day, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList(),
        Cron = string.IsNullOrWhiteSpace(schedule.Cron) ? null : schedule.Cron.Trim()
    };

    private static InputModel FromSettings(SyncSettings settings)
    {
        var megabytes = Math.Max(1, (int)Math.Round(settings.ConflictFileMaxBytes / 1_000_000d, MidpointRounding.AwayFromZero));
        return new InputModel
        {
            ScheduledSyncEnabled = settings.ScheduledSyncEnabled,
            Schedules = settings.Schedules.Select(schedule => new ScheduleInput
            {
                Id = schedule.Id,
                Enabled = schedule.Enabled,
                Time = schedule.Time,
                DaysOfWeek = schedule.DaysOfWeek ?? [],
                Cron = schedule.Cron
            }).ToList(),
            IntervalSyncEnabled = settings.IntervalSyncEnabled,
            IntervalValue = settings.IntervalValue,
            IntervalUnit = settings.IntervalUnit.ToString(),
            SyncMainToOthers = settings.SyncMainToOthers,
            RetryIntervalSeconds = settings.RetryIntervalSeconds,
            MaximumRetryAttempts = settings.MaximumRetryAttempts,
            CommandTimeoutSeconds = settings.CommandTimeoutSeconds,
            EnableDetailedLogging = settings.EnableDetailedLogging,
            DryRun = settings.DryRun,
            ConflictBackupFolder = settings.ConflictBackupFolder,
            ConflictFileRowThreshold = settings.ConflictFileRowThreshold,
            ConflictFileMaxMegabytes = megabytes,
            UseConflictBackupTables = settings.UseConflictBackupTables,
            WriteConflictLogTable = settings.WriteConflictLogTable,
            AssumeDifferingRowsConflict = settings.AssumeDifferingRowsConflict,
            ChangeTimestampColumnNames = string.Join(", ", settings.ChangeTimestampColumnNames ?? [])
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

    public sealed class InputModel
    {
        [Display(Name = "Scheduled sync")]
        public bool ScheduledSyncEnabled { get; set; }

        public List<ScheduleInput> Schedules { get; set; } = [];

        [Display(Name = "Interval sync")]
        public bool IntervalSyncEnabled { get; set; }

        [Display(Name = "Every")]
        [Range(1, 100000)]
        public int IntervalValue { get; set; } = 2;

        [Display(Name = "Unit")]
        public string IntervalUnit { get; set; } = "Minutes";

        [Display(Name = "Sync Main → others")]
        public bool SyncMainToOthers { get; set; } = true;

        [Display(Name = "Retry interval (seconds)")]
        [Range(1, 600)]
        public int RetryIntervalSeconds { get; set; } = 15;

        [Display(Name = "Maximum retry attempts")]
        [Range(1, 10)]
        public int MaximumRetryAttempts { get; set; } = 2;

        [Display(Name = "SQL command timeout (seconds)")]
        [Range(30, 3600)]
        public int CommandTimeoutSeconds { get; set; } = 180;

        [Display(Name = "Detailed logging")]
        public bool EnableDetailedLogging { get; set; } = true;

        [Display(Name = "Dry run")]
        public bool DryRun { get; set; }

        [Display(Name = "Conflict backup folder")]
        [Required]
        public string ConflictBackupFolder { get; set; } = "Conflicts";

        [Display(Name = "Small conflict row threshold")]
        [Range(1, 1_000_000)]
        public int ConflictFileRowThreshold { get; set; } = 1000;

        [Display(Name = "Small conflict size limit (MB)")]
        [Range(1, 2048)]
        public int ConflictFileMaxMegabytes { get; set; } = 5;

        [Display(Name = "Large conflicts go to a backup table")]
        public bool UseConflictBackupTables { get; set; }

        [Display(Name = "Also write sync.ConflictLog on the target")]
        public bool WriteConflictLogTable { get; set; }

        [Display(Name = "Back up every differing target row")]
        public bool AssumeDifferingRowsConflict { get; set; }

        [Display(Name = "Change-timestamp column names")]
        public string? ChangeTimestampColumnNames { get; set; }
    }

    public sealed class ScheduleInput
    {
        public string? Id { get; set; }

        public bool Enabled { get; set; } = true;

        public string? Time { get; set; } = "02:00";

        public List<string> DaysOfWeek { get; set; } = [];

        public string? Cron { get; set; }

        public bool Remove { get; set; }
    }
}
