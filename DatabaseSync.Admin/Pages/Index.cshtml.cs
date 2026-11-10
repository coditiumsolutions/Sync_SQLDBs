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
    private readonly IDatabaseConnectionService _connectionService;
    private readonly IWorkerServiceControl _workerServiceControl;
    private readonly ISyncStatusStore _statusStore;
    private readonly ManualSyncHost _manualSyncHost;

    public IndexModel(
        ISyncConfigStore configStore,
        IDatabaseConnectionService connectionService,
        IWorkerServiceControl workerServiceControl,
        ISyncStatusStore statusStore,
        ManualSyncHost manualSyncHost)
    {
        _configStore = configStore;
        _connectionService = connectionService;
        _workerServiceControl = workerServiceControl;
        _statusStore = statusStore;
        _manualSyncHost = manualSyncHost;
    }

    public SyncConfiguration Configuration { get; private set; } = new();

    public string ConfigPath { get; private set; } = string.Empty;

    public WorkerServiceStatusInfo ServiceStatus { get; private set; } = new();

    public SyncRuntimeStatus SyncStatus { get; private set; } = new();

    public async Task OnGetAsync()
    {
        await LoadAsync();
    }

    public IActionResult OnGetSyncStatus()
    {
        var status = _statusStore.Get();
        var settings = _configStore.Get().SyncSettings;
        var now = DateTimeOffset.UtcNow;
        var zone = TimeZoneInfo.Local;
        var grace = TimeSpan.FromMinutes(settings.ScheduleGraceMinutes);
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
            status.LastRunStartedUtc,
            status.LastRunFinishedUtc,
            status.LastManualRunUtc,
            status.LastScheduledRunUtc,
            status.LastIntervalRunUtc,
            status.LastSkippedTrigger,
            status.LastSkipReason,
            status.LastSkippedAtUtc,
            NextScheduledRunUtc = settings.ScheduledSyncEnabled
                ? SyncSchedulePlanner.GetNextRunUtc(settings.Schedules, now, zone, grace, status.FiredScheduleOccurrences)
                : null,
            NextIntervalRunUtc = settings.IntervalSyncEnabled
                ? status.NextIntervalRunUtc ?? now
                : (DateTimeOffset?)null,
            status.Servers,
            ScheduledSyncEnabled = settings.ScheduledSyncEnabled,
            IntervalSyncEnabled = settings.IntervalSyncEnabled
        });
    }

    public IActionResult OnPostSyncNow()
    {
        _manualSyncHost.Queue();
        TempData["StatusMessage"] =
            "Manual sync requested. It starts immediately unless another sync is running, in which case it runs once when that cycle finishes.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostStartServiceAsync()
    {
        // Mark waiting state so UI can show progress immediately.
        await _statusStore.WriteAsync(new SyncRuntimeStatus
        {
            State = "Running",
            Message = "Sync service starting. Waiting for database synchronisation to complete...",
            StartedAtUtc = DateTimeOffset.UtcNow
        });

        var result = _workerServiceControl.Start();
        TempData[result.StatusText == "Error" ? "ErrorMessage" : "StatusMessage"] =
            result.Message ?? $"Service status: {result.StatusText}";

        if (result.StatusText != "Error")
        {
            TempData["StatusMessage"] =
                (result.Message ?? "Sync started.") + " Waiting for completion message...";
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostStopServiceAsync()
    {
        var result = _workerServiceControl.Stop();
        await _statusStore.WriteAsync(new SyncRuntimeStatus
        {
            State = "Idle",
            Message = "Sync service stopped.",
            FinishedAtUtc = DateTimeOffset.UtcNow
        });

        TempData[result.StatusText == "Error" ? "ErrorMessage" : "StatusMessage"] =
            result.Message ?? $"Service status: {result.StatusText}";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostTestAllAsync()
    {
        await LoadAsync();

        var ok = 0;
        var fail = 0;
        foreach (var connection in Configuration.Connections.Where(c => c.IsEnabled))
        {
            var result = await _connectionService.TestConnectionAsync(connection.Id);
            if (result.IsSuccessful)
            {
                ok++;
            }
            else
            {
                fail++;
            }
        }

        TempData["StatusMessage"] = $"Connectivity test finished. OK={ok}, Failed={fail}.";
        return RedirectToPage();
    }

    private async Task LoadAsync()
    {
        Configuration = await _configStore.GetAsync();
        ConfigPath = _configStore.ConfigFilePath;
        ServiceStatus = _workerServiceControl.GetStatus();
        SyncStatus = _statusStore.Get();
    }
}
