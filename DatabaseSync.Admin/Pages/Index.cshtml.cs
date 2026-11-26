using DatabaseSync.Admin.Services;
using DatabaseSync.Core.Interfaces;
using DatabaseSync.Core.Models;
using DatabaseSync.Core.Scheduling;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DatabaseSync.Admin.Pages;

public class IndexModel : PageModel
{
    private readonly ISyncConfigStore _configStore;
    private readonly IWorkerServiceControl _workerServiceControl;
    private readonly ISyncStatusStore _statusStore;
    private readonly ManualSyncHost _manualSyncHost;

    public IndexModel(
        ISyncConfigStore configStore,
        IWorkerServiceControl workerServiceControl,
        ISyncStatusStore statusStore,
        ManualSyncHost manualSyncHost)
    {
        _configStore = configStore;
        _workerServiceControl = workerServiceControl;
        _statusStore = statusStore;
        _manualSyncHost = manualSyncHost;
    }

    public SyncConfiguration Configuration { get; private set; } = new();

    public WorkerServiceStatusInfo ServiceStatus { get; private set; } = new();

    public SyncRuntimeStatus SyncStatus { get; private set; } = new();

    public DateTimeOffset? NextScheduledRunUtc { get; private set; }

    public DateTimeOffset? NextIntervalRunUtc { get; private set; }

    public ConflictReview Conflicts { get; private set; } = new();

    public bool OneWaySyncEnabled { get; private set; } = true;

    public async Task OnGetAsync()
    {
        await LoadAsync();
    }

    public IActionResult OnGetSyncStatus()
    {
        var status = _statusStore.Get();
        var settings = _configStore.Get().SyncSettings;
        var (scheduled, interval) = NextRuns(settings, status);
        return new JsonResult(new
        {
            status.State,
            status.Message,
            status.Trigger,
            status.LastTrigger,
            status.DryRun,
            status.RowsInserted,
            status.RowsUpdated,
            status.RowsDeleted,
            status.RowsConflicted,
            status.EstimatedBytesTransferred,
            status.SuccessfulTables,
            status.FailedTables,
            status.LastRunStartedUtc,
            status.LastRunFinishedUtc,
            status.LastManualRunUtc,
            status.LastScheduledRunUtc,
            status.LastIntervalRunUtc,
            status.LastSkippedTrigger,
            status.LastSkipReason,
            status.LastSkippedAtUtc,
            NextScheduledRunUtc = scheduled,
            NextIntervalRunUtc = interval,
            status.Servers,
            ScheduledSyncEnabled = settings.ScheduledSyncEnabled,
            IntervalSyncEnabled = settings.IntervalSyncEnabled,
            IntervalValue = settings.IntervalValue,
            IntervalUnit = settings.IntervalUnit.ToString()
        });
    }

    public IActionResult OnPostSyncNow()
    {
        _manualSyncHost.Queue();
        TempData["StatusMessage"] =
            "Manual sync requested. It starts immediately unless another sync is running, in which case it runs once when that cycle finishes.";
        return RedirectToPage();
    }

    public IActionResult OnPostStartService()
    {
        var result = _workerServiceControl.Start();
        TempData[result.StatusText == "Error" ? "ErrorMessage" : "StatusMessage"] =
            result.Message ?? $"Worker status: {result.StatusText}";
        return RedirectToPage();
    }

    public IActionResult OnPostStopService()
    {
        var result = _workerServiceControl.Stop();
        TempData[result.StatusText == "Error" ? "ErrorMessage" : "StatusMessage"] =
            result.Message ?? $"Worker status: {result.StatusText}";
        return RedirectToPage();
    }

    private async Task LoadAsync()
    {
        Configuration = await _configStore.GetAsync();
        ServiceStatus = _workerServiceControl.GetStatus();
        SyncStatus = _statusStore.Get();
        OneWaySyncEnabled = Configuration.SyncSettings.SyncMainToOthers;
        (NextScheduledRunUtc, NextIntervalRunUtc) = NextRuns(Configuration.SyncSettings, SyncStatus);
        var folder = ConflictLogReview.ResolveFolder(
            _configStore.ConfigFilePath,
            Configuration.SyncSettings.ConflictBackupFolder);
        Conflicts = ConflictLogReview.Read(folder);
    }

    private static (DateTimeOffset? Scheduled, DateTimeOffset? Interval) NextRuns(
        SyncSettings settings,
        SyncRuntimeStatus status)
    {
        var now = DateTimeOffset.UtcNow;
        var zone = TimeZoneInfo.Local;
        var grace = TimeSpan.FromMinutes(settings.ScheduleGraceMinutes);
        var scheduled = settings.ScheduledSyncEnabled
            ? SyncSchedulePlanner.GetNextRunUtc(settings.Schedules, now, zone, grace, status.FiredScheduleOccurrences)
            : null;
        var interval = settings.IntervalSyncEnabled
            ? status.NextIntervalRunUtc ?? now
            : (DateTimeOffset?)null;
        return (scheduled, interval);
    }
}
