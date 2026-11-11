using DatabaseSync.Core.DTOs;
using DatabaseSync.Core.Enums;
using DatabaseSync.Core.Interfaces;
using DatabaseSync.Core.Models;
using DatabaseSync.Core.Scheduling;
using DatabaseSync.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DatabaseSync.Infrastructure.Services;

public sealed class SyncCycleRunner : ISyncCycleRunner
{
    private readonly SyncOverlapGuard _guard = new();
    private readonly ISyncOrchestrator _orchestrator;
    private readonly IConnectivityCheckService _connectivityCheckService;
    private readonly ISyncStatusStore _statusStore;
    private readonly ISyncConfigStore _configStore;
    private readonly IManualSyncRequestStore _requests;
    private readonly ILogger<SyncCycleRunner> _logger;
    private readonly string _lockPath;

    public SyncCycleRunner(
        ISyncOrchestrator orchestrator,
        IConnectivityCheckService connectivityCheckService,
        ISyncStatusStore statusStore,
        ISyncConfigStore configStore,
        IManualSyncRequestStore requests,
        IOptions<SyncConfigStoreOptions> options,
        ILogger<SyncCycleRunner> logger)
    {
        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
        _connectivityCheckService = connectivityCheckService ?? throw new ArgumentNullException(nameof(connectivityCheckService));
        _statusStore = statusStore ?? throw new ArgumentNullException(nameof(statusStore));
        _configStore = configStore ?? throw new ArgumentNullException(nameof(configStore));
        _requests = requests ?? throw new ArgumentNullException(nameof(requests));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var configPath = Path.IsPathRooted(options.Value.FilePath)
            ? options.Value.FilePath
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, options.Value.FilePath));
        var directory = Path.GetDirectoryName(configPath) ?? AppContext.BaseDirectory;
        _lockPath = Path.Combine(directory, "sync-run.lock");
    }

    public async Task<SyncTriggerOutcome> TryRunAsync(SyncTriggerKind trigger, CancellationToken cancellationToken = default)
    {
        var settings = _configStore.Get().SyncSettings;
        if (!settings.SyncMainToOthers)
        {
            const string disabled = "SyncMainToOthers is disabled, so no cycle was started.";
            _logger.LogWarning(disabled);
            return new SyncTriggerOutcome
            {
                Trigger = trigger,
                Started = false,
                Message = disabled
            };
        }

        if (!_guard.TryEnter(trigger, out var activeInProcess))
        {
            var message =
                $"Skipped {trigger} sync because a {activeInProcess} sync is already running in this process. Overlapping triggers are not queued.";
            _logger.LogWarning(message);
            return SyncTriggerOutcome.Skipped(trigger, activeInProcess, message);
        }

        if (!FileSyncRunLock.TryAcquire(_lockPath, out var fileLock))
        {
            _guard.Exit();
            var status = _statusStore.Get();
            SyncTriggerKind? running = status.State == "Running"
                && Enum.TryParse<SyncTriggerKind>(status.Trigger, out var parsed)
                    ? parsed
                    : null;
            var message = running is null
                ? $"Skipped {trigger} sync because another sync process holds the lock. Overlapping triggers are skipped. A manual request stays queued until the current cycle finishes."
                : $"Skipped {trigger} sync because a {running} sync is already running. Overlapping triggers are skipped. A manual request stays queued until the current cycle finishes.";
            _logger.LogWarning(message);
            return SyncTriggerOutcome.Skipped(trigger, running, message);
        }

        var manual = trigger == SyncTriggerKind.Manual ? _requests.Read() : null;
        try
        {
            return await ExecuteAsync(trigger, settings, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (manual is not null)
            {
                _requests.CompleteIfCurrent(manual.RequestId);
            }

            fileLock?.Dispose();
            _guard.Exit();
        }
    }

    private async Task<SyncTriggerOutcome> ExecuteAsync(
        SyncTriggerKind trigger,
        SyncSettings settings,
        CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        var existing = _statusStore.Get();
        var running = CopyBookkeeping(existing);
        running.State = "Running";
        running.Trigger = trigger.ToString();
        running.DryRun = settings.DryRun;
        running.Message = settings.DryRun
            ? $"{trigger} differential sync is in progress (dry run; target data will not change)..."
            : $"{trigger} differential sync is in progress...";
        running.StartedAtUtc = started;
        running.FinishedAtUtc = null;
        await _statusStore.WriteAsync(running, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Sync trigger {Trigger} starting. DryRun={DryRun} Scheduled={Scheduled} Interval={Interval}",
            trigger,
            settings.DryRun,
            settings.ScheduledSyncEnabled,
            settings.IntervalSyncEnabled);

        try
        {
            var connectivity = await _connectivityCheckService
                .CheckAllDatabasesAsync(cancellationToken)
                .ConfigureAwait(false);

            foreach (var result in connectivity)
            {
                if (result.IsSuccessful)
                {
                    _logger.LogInformation(
                        "Database status [{Role}]: {Name} => Connected ({Server}/{Database}) in {ElapsedMs} ms.",
                        result.IsMain ? "MAIN" : "TARGET",
                        result.ConnectionName,
                        result.Server,
                        result.Database,
                        result.Elapsed.TotalMilliseconds);
                }
                else
                {
                    _logger.LogWarning(
                        "Database status [{Role}]: {Name} => {Status} ({Server}/{Database}). Error: {Error}",
                        result.IsMain ? "MAIN" : "TARGET",
                        result.ConnectionName,
                        result.Status,
                        result.Server,
                        result.Database,
                        result.ErrorMessage);
                }
            }

            var cycle = await _orchestrator.RunAsync(trigger, cancellationToken).ConfigureAwait(false);
            var finished = DateTimeOffset.UtcNow;
            var backups = cycle.TableResults
                .Select(result => result.BackupLocation)
                .Where(location => !string.IsNullOrWhiteSpace(location))
                .Distinct()
                .Take(3)
                .ToList();
            var prefix = cycle.DryRun ? "Dry run. " : string.Empty;
            var message = cycle.Success
                ? $"{prefix}{trigger} sync finished. Inserted={cycle.RowsInserted}, updated={cycle.RowsUpdated}, deleted={cycle.RowsDeleted}, conflicts={cycle.RowsConflicted}, estimated bytes={cycle.EstimatedBytesTransferred}."
                : $"{prefix}{trigger} sync finished with errors. {cycle.ErrorMessage} Inserted={cycle.RowsInserted}, updated={cycle.RowsUpdated}, deleted={cycle.RowsDeleted}, conflicts={cycle.RowsConflicted}.";
            if (backups.Count > 0)
            {
                message += " Backups: " + string.Join(" | ", backups);
            }

            var latest = _statusStore.Get();
            var status = CopyBookkeeping(latest);
            status.State = cycle.Success ? "Succeeded" : "Failed";
            status.Trigger = trigger.ToString();
            status.LastTrigger = trigger.ToString();
            status.DryRun = cycle.DryRun;
            status.Message = message;
            status.MainConnectionName = cycle.MainConnectionName;
            status.TargetsSynced = cycle.TargetsSynced.ToList();
            status.SuccessfulTables = cycle.SuccessfulTables;
            status.FailedTables = cycle.FailedTables;
            status.RowsInserted = cycle.RowsInserted;
            status.RowsUpdated = cycle.RowsUpdated;
            status.RowsDeleted = cycle.RowsDeleted;
            status.RowsConflicted = cycle.RowsConflicted;
            status.EstimatedBytesTransferred = cycle.EstimatedBytesTransferred;
            status.StartedAtUtc = cycle.StartedAtUtc;
            status.FinishedAtUtc = cycle.FinishedAtUtc;
            status.LastRunStartedUtc = started;
            status.LastRunFinishedUtc = finished;
            status.Servers = BuildServerReports(connectivity, cycle);
            switch (trigger)
            {
                case SyncTriggerKind.Manual:
                    status.LastManualRunUtc = finished;
                    break;
                case SyncTriggerKind.Scheduled:
                    status.LastScheduledRunUtc = finished;
                    break;
                case SyncTriggerKind.Interval:
                    status.LastIntervalRunUtc = finished;
                    break;
            }

            await _statusStore.WriteAsync(status, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "Sync trigger {Trigger} finished. Success={Success} Inserted={Inserted} Updated={Updated} Deleted={Deleted} Conflicted={Conflicted} EstimatedBytes={EstimatedBytes}",
                trigger,
                cycle.Success,
                cycle.RowsInserted,
                cycle.RowsUpdated,
                cycle.RowsDeleted,
                cycle.RowsConflicted,
                cycle.EstimatedBytesTransferred);

            return new SyncTriggerOutcome
            {
                Trigger = trigger,
                Started = true,
                Cycle = cycle,
                Message = message
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sync trigger {Trigger} failed.", trigger);
            var failed = CopyBookkeeping(_statusStore.Get());
            failed.State = "Failed";
            failed.Trigger = trigger.ToString();
            failed.LastTrigger = trigger.ToString();
            failed.Message = $"{trigger} sync failed: {ex.Message}";
            failed.StartedAtUtc = started;
            failed.FinishedAtUtc = DateTimeOffset.UtcNow;
            failed.LastRunStartedUtc = started;
            failed.LastRunFinishedUtc = failed.FinishedAtUtc;
            await _statusStore.WriteAsync(failed, cancellationToken).ConfigureAwait(false);
            return new SyncTriggerOutcome
            {
                Trigger = trigger,
                Started = true,
                Message = failed.Message,
                Cycle = new SyncCycleResult
                {
                    StartedAtUtc = started,
                    FinishedAtUtc = failed.FinishedAtUtc ?? DateTimeOffset.UtcNow,
                    Success = false,
                    Trigger = trigger,
                    DryRun = settings.DryRun,
                    ErrorMessage = ex.Message
                }
            };
        }
    }

    private static SyncRuntimeStatus CopyBookkeeping(SyncRuntimeStatus source) =>
        new()
        {
            FiredScheduleOccurrences = source.FiredScheduleOccurrences ?? new Dictionary<string, DateTimeOffset>(),
            NextScheduledRunUtc = source.NextScheduledRunUtc,
            NextIntervalRunUtc = source.NextIntervalRunUtc,
            LastManualRunUtc = source.LastManualRunUtc,
            LastScheduledRunUtc = source.LastScheduledRunUtc,
            LastIntervalRunUtc = source.LastIntervalRunUtc,
            LastSkippedTrigger = source.LastSkippedTrigger,
            LastSkipReason = source.LastSkipReason,
            LastSkippedAtUtc = source.LastSkippedAtUtc,
            LastRunStartedUtc = source.LastRunStartedUtc,
            LastRunFinishedUtc = source.LastRunFinishedUtc
        };

    private static List<ServerSyncReport> BuildServerReports(
        IReadOnlyList<DatabaseConnectivityResult> connectivity,
        SyncCycleResult syncResult)
    {
        var reports = new List<ServerSyncReport>();
        foreach (var connection in connectivity)
        {
            if (connection.IsMain)
            {
                reports.Add(new ServerSyncReport
                {
                    ConnectionId = connection.ConnectionId,
                    Name = connection.ConnectionName,
                    Server = connection.Server,
                    Database = connection.Database,
                    IsMain = true,
                    Outcome = connection.IsSuccessful ? "Source" : "Failed",
                    Reason = connection.IsSuccessful
                        ? "Main source database. Only its changes are sent to each target."
                        : $"Main database unreachable: {connection.ErrorMessage}"
                });
                continue;
            }

            if (!connection.IsSuccessful)
            {
                reports.Add(new ServerSyncReport
                {
                    ConnectionId = connection.ConnectionId,
                    Name = connection.ConnectionName,
                    Server = connection.Server,
                    Database = connection.Database,
                    IsMain = false,
                    Outcome = "Failed",
                    Reason = $"Could not connect: {connection.ErrorMessage}"
                });
                continue;
            }

            var tableResults = syncResult.TableResults
                .Where(result => result.TargetConnectionId == connection.ConnectionId)
                .ToList();
            if (tableResults.Count == 0)
            {
                var syncedByName = syncResult.TargetsSynced.Contains(connection.ConnectionName);
                reports.Add(new ServerSyncReport
                {
                    ConnectionId = connection.ConnectionId,
                    Name = connection.ConnectionName,
                    Server = connection.Server,
                    Database = connection.Database,
                    IsMain = false,
                    Outcome = syncedByName ? "Synced" : "Skipped",
                    Reason = syncedByName
                        ? "Target reached, but no table results were recorded."
                        : (syncResult.ErrorMessage ?? "Target was skipped during sync.")
                });
                continue;
            }

            var failed = tableResults.Count(result => !result.Success);
            var ok = tableResults.Count - failed;
            var firstError = tableResults.FirstOrDefault(result => !result.Success)?.ErrorMessage;
            reports.Add(new ServerSyncReport
            {
                ConnectionId = connection.ConnectionId,
                Name = connection.ConnectionName,
                Server = connection.Server,
                Database = connection.Database,
                IsMain = false,
                Outcome = failed == 0 ? "Synced" : "Failed",
                TablesOk = ok,
                TablesFailed = failed,
                Reason = failed == 0
                    ? $"Inserted {tableResults.Sum(result => result.RowsInserted)}, updated {tableResults.Sum(result => result.RowsUpdated)}, deleted {tableResults.Sum(result => result.RowsDeleted)}, conflicts {tableResults.Sum(result => result.RowsConflicted)}."
                    : $"Failed tables: {failed}/{tableResults.Count}. First error: {firstError}"
            });
        }

        return reports;
    }
}
