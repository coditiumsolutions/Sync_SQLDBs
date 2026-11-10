using System.ComponentModel.DataAnnotations;
using DatabaseSync.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DatabaseSync.Admin.Pages;

public class SettingsModel : PageModel
{
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

        var minutes = config.SyncSettings.AutoSyncEveryMinutes > 0
            ? config.SyncSettings.AutoSyncEveryMinutes
            : Math.Max(1, config.SyncSettings.SyncIntervalSeconds / 60);

        Input = new InputModel
        {
            EnableSynchronization = config.SyncSettings.EnableSynchronization,
            SyncMainToOthers = config.SyncSettings.SyncMainToOthers,
            AutoSyncEveryMinutes = minutes,
            AutoSyncStartTime = string.IsNullOrWhiteSpace(config.SyncSettings.AutoSyncStartTime)
                ? "00:00"
                : config.SyncSettings.AutoSyncStartTime,
            AutoSyncEndTime = string.IsNullOrWhiteSpace(config.SyncSettings.AutoSyncEndTime)
                ? "23:59"
                : config.SyncSettings.AutoSyncEndTime,
            RetryIntervalSeconds = config.SyncSettings.RetryIntervalSeconds,
            MaximumRetryAttempts = config.SyncSettings.MaximumRetryAttempts,
            CommandTimeoutSeconds = config.SyncSettings.CommandTimeoutSeconds,
            EnableDetailedLogging = config.SyncSettings.EnableDetailedLogging
        };
    }

    public async Task<IActionResult> OnPostAsync()
    {
        ConfigPath = _configStore.ConfigFilePath;
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var config = await _configStore.GetAsync();
        config.SyncSettings.EnableSynchronization = Input.EnableSynchronization;
        config.SyncSettings.SyncMainToOthers = Input.SyncMainToOthers;
        config.SyncSettings.AutoSyncEveryMinutes = Input.AutoSyncEveryMinutes;
        config.SyncSettings.SyncIntervalSeconds = Math.Max(60, Input.AutoSyncEveryMinutes * 60);
        config.SyncSettings.AutoSyncStartTime = Input.AutoSyncStartTime.Trim();
        config.SyncSettings.AutoSyncEndTime = Input.AutoSyncEndTime.Trim();
        config.SyncSettings.RetryIntervalSeconds = Input.RetryIntervalSeconds;
        config.SyncSettings.MaximumRetryAttempts = Input.MaximumRetryAttempts;
        config.SyncSettings.CommandTimeoutSeconds = Input.CommandTimeoutSeconds;
        config.SyncSettings.EnableDetailedLogging = Input.EnableDetailedLogging;

        await _configStore.SaveAsync(config);
        TempData["StatusMessage"] = "Settings saved. Automatic sync timing applies while the service is running.";
        return RedirectToPage();
    }

    public sealed class InputModel
    {
        [Display(Name = "Auto sync")]
        public bool EnableSynchronization { get; set; }

        [Display(Name = "Sync Main → others")]
        public bool SyncMainToOthers { get; set; } = true;

        [Display(Name = "Auto sync every (minutes)")]
        [Range(1, 1440)]
        public int AutoSyncEveryMinutes { get; set; } = 2;

        [Display(Name = "Auto sync start time (HH:mm)")]
        [RegularExpression(@"^\d{2}:\d{2}$", ErrorMessage = "Use HH:mm format, e.g. 09:00")]
        public string AutoSyncStartTime { get; set; } = "00:00";

        [Display(Name = "Auto sync end time (HH:mm)")]
        [RegularExpression(@"^\d{2}:\d{2}$", ErrorMessage = "Use HH:mm format, e.g. 18:00")]
        public string AutoSyncEndTime { get; set; } = "23:59";

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
    }
}
