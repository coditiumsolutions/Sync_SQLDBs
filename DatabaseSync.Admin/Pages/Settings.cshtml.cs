using System.ComponentModel.DataAnnotations;
using DatabaseSync.Admin.Services;
using DatabaseSync.Core.Interfaces;
using DatabaseSync.Core.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DatabaseSync.Admin.Pages;

public class SettingsModel : PageModel
{
    public static readonly string[] WeekDays = TriggerSettingsEditor.WeekDays;

    private readonly ISyncConfigStore _configStore;

    public SettingsModel(ISyncConfigStore configStore)
    {
        _configStore = configStore;
    }

    public bool OneWaySyncEnabled { get; private set; } = true;

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public async Task OnGetAsync()
    {
        var config = await _configStore.GetAsync();
        OneWaySyncEnabled = config.SyncSettings.SyncMainToOthers;
        Input = FromSettings(config.SyncSettings);
    }

    public async Task<IActionResult> OnPostAddSchedule()
    {
        await RememberDirectionAsync();
        NormalizeSchedules();
        Input.Schedules.Add(new ScheduleInput { Time = "02:00", Enabled = true });
        return Page();
    }

    public async Task<IActionResult> OnPostRemoveSchedule(int index)
    {
        await RememberDirectionAsync();
        NormalizeSchedules();
        if (index >= 0 && index < Input.Schedules.Count)
        {
            Input.Schedules.RemoveAt(index);
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        await RememberDirectionAsync();
        NormalizeSchedules();
        var errors = TriggerSettingsEditor.Validate(Input);
        foreach (var error in errors)
        {
            var split = error.Split('|', 2);
            ModelState.AddModelError(split[0], split.Length == 2 ? split[1] : error);
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        var config = await _configStore.GetAsync();
        TriggerSettingsEditor.Apply(config.SyncSettings, Input);
        await _configStore.SaveAsync(config);
        TempData["StatusMessage"] =
            "Triggers saved. Sync now stays on the desk. Auto and interval run only while the worker is running, and only one cycle runs at a time.";
        return RedirectToPage();
    }

    private async Task RememberDirectionAsync()
    {
        var config = await _configStore.GetAsync();
        OneWaySyncEnabled = config.SyncSettings.SyncMainToOthers;
    }

    private void NormalizeSchedules()
    {
        Input.Schedules ??= [];
        foreach (var schedule in Input.Schedules)
        {
            schedule.DaysOfWeek ??= [];
        }
    }

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
                Cron = schedule.Cron,
                UseCron = !string.IsNullOrWhiteSpace(schedule.Cron)
            }).ToList(),
            IntervalSyncEnabled = settings.IntervalSyncEnabled,
            IntervalValue = settings.IntervalValue,
            IntervalUnit = settings.IntervalUnit.ToString(),
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

    public sealed class InputModel
    {
        [Display(Name = "Auto")]
        public bool ScheduledSyncEnabled { get; set; }

        public List<ScheduleInput> Schedules { get; set; } = [];

        [Display(Name = "Interval")]
        public bool IntervalSyncEnabled { get; set; }

        [Display(Name = "Every")]
        [Range(1, 100000)]
        public int IntervalValue { get; set; } = 2;

        [Display(Name = "Unit")]
        public string IntervalUnit { get; set; } = "Minutes";

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

        /// <summary>Shown only when the stored one-way flag is off.</summary>
        public bool RestoreOneWaySync { get; set; }
    }

    public sealed class ScheduleInput
    {
        public string? Id { get; set; }

        public bool Enabled { get; set; } = true;

        public string? Time { get; set; } = "02:00";

        public List<string> DaysOfWeek { get; set; } = [];

        public string? Cron { get; set; }

        public bool UseCron { get; set; }

        public bool Remove { get; set; }
    }
}
