using DatabaseSync.Core.Interfaces;
using DatabaseSync.Core.Models;

namespace DatabaseSync.Worker.Workers;

public sealed class DatabaseSyncWorker : BackgroundService
{
    private readonly IConnectivityCheckService _connectivityCheckService;
    private readonly ISyncOrchestrator _syncOrchestrator;
    private readonly ISyncConfigStore _configStore;
    private readonly ISyncStatusStore _statusStore;
    private readonly ISqlConnectionFactory _connectionFactory;
    private readonly ILogger<DatabaseSyncWorker> _logger;

    public DatabaseSyncWorker(
        IConnectivityCheckService connectivityCheckService,
        ISyncOrchestrator syncOrchestrator,
        ISyncConfigStore configStore,
        ISyncStatusStore statusStore,
        ISqlConnectionFactory connectionFactory,
        ILogger<DatabaseSyncWorker> logger)
    {
        _connectivityCheckService = connectivityCheckService
            ?? throw new ArgumentNullException(nameof(connectivityCheckService));
        _syncOrchestrator = syncOrchestrator ?? throw new ArgumentNullException(nameof(syncOrchestrator));
        _configStore = configStore ?? throw new ArgumentNullException(nameof(configStore));
        _statusStore = statusStore ?? throw new ArgumentNullException(nameof(statusStore));
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = _configStore.Get().SyncSettings;
        _logger.LogInformation(
            "Coditium.DatabaseSync worker started. Synchronisation enabled: {EnableSynchronization}; IntervalMinutes={IntervalMinutes}; Window={Start}-{End}; Config={ConfigPath}",
            settings.EnableSynchronization,
            settings.AutoSyncEveryMinutes,
            settings.AutoSyncStartTime,
            settings.AutoSyncEndTime,
            _configStore.ConfigFilePath);

        await Task.Yield();

        while (!stoppingToken.IsCancellationRequested)
        {
            settings = _configStore.Get().SyncSettings;
            var interval = TimeSpan.FromSeconds(settings.GetEffectiveIntervalSeconds());

            try
            {
                await RunCycleAsync(settings, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error during the synchronisation cycle.");
                await _statusStore.WriteAsync(new SyncRuntimeStatus
                {
                    State = "Failed",
                    Message = $"Unhandled sync error: {ex.Message}",
                    StartedAtUtc = DateTimeOffset.UtcNow,
                    FinishedAtUtc = DateTimeOffset.UtcNow
                }, stoppingToken).ConfigureAwait(false);
            }

            try
            {
                await Task.Delay(interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        _logger.LogInformation("Coditium.DatabaseSync worker is stopping.");
    }

    private async Task RunCycleAsync(SyncSettings settings, CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        var localNow = DateTime.Now;

        if (!settings.IsWithinAutoSyncWindow(localNow))
        {
            var skipMessage =
                $"Automatic sync skipped outside configured time window ({settings.AutoSyncStartTime} - {settings.AutoSyncEndTime}). Current local time: {localNow:HH:mm}.";
            _logger.LogInformation(skipMessage);

            await _statusStore.WriteAsync(new SyncRuntimeStatus
            {
                State = "Skipped",
                Message = skipMessage,
                StartedAtUtc = started,
                FinishedAtUtc = DateTimeOffset.UtcNow,
                Servers = BuildIdleServerReports("Skipped", skipMessage)
            }, cancellationToken).ConfigureAwait(false);
            return;
        }

        _logger.LogInformation("Beginning synchronisation cycle at {TimestampUtc:u}.", started);

        await _statusStore.WriteAsync(new SyncRuntimeStatus
        {
            State = "Running",
            Message = "Database synchronisation is in progress...",
            StartedAtUtc = started
        }, cancellationToken).ConfigureAwait(false);

        var connectivity = await _connectivityCheckService
            .CheckAllDatabasesAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var result in connectivity)
        {
            var roleTag = result.IsMain ? "MAIN" : "TARGET";
            if (result.IsSuccessful)
            {
                _logger.LogInformation(
                    "Database status [{RoleTag}]: {Name} => Connected ({Server}/{Database}) in {ElapsedMs} ms.",
                    roleTag,
                    result.ConnectionName,
                    result.Server,
                    result.Database,
                    result.Elapsed.TotalMilliseconds);
            }
            else
            {
                _logger.LogWarning(
                    "Database status [{RoleTag}]: {Name} => {Status} ({Server}/{Database}). Error: {ErrorMessage}",
                    roleTag,
                    result.ConnectionName,
                    result.Status,
                    result.Server,
                    result.Database,
                    result.ErrorMessage);
            }
        }

        if (!settings.EnableSynchronization)
        {
            var disabledMessage = "Connectivity check completed. Synchronisation is disabled in settings.";
            await _statusStore.WriteAsync(new SyncRuntimeStatus
            {
                State = "Succeeded",
                Message = disabledMessage,
                StartedAtUtc = started,
                FinishedAtUtc = DateTimeOffset.UtcNow,
                Servers = connectivity.Select(c => new ServerSyncReport
                {
                    ConnectionId = c.ConnectionId,
                    Name = c.ConnectionName,
                    Server = c.Server,
                    Database = c.Database,
                    IsMain = c.IsMain,
                    Outcome = c.IsSuccessful ? "Connected" : "Failed",
                    Reason = c.IsSuccessful
                        ? "Sync disabled in settings (connectivity only)."
                        : c.ErrorMessage
                }).ToList()
            }, cancellationToken).ConfigureAwait(false);
            return;
        }

        var syncResult = await _syncOrchestrator.RunAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Sync cycle finished. Success={Success}; Main={Main}; Targets={Targets}; TablesOk={Ok}; TablesFailed={Failed}; Error={Error}",
            syncResult.Success,
            syncResult.MainConnectionName,
            string.Join(",", syncResult.TargetsSynced),
            syncResult.SuccessfulTables,
            syncResult.FailedTables,
            syncResult.ErrorMessage);

        var serverReports = BuildServerReports(connectivity, syncResult);

        var successMessage = syncResult.Success
            ? $"Database sync successfully completed. Main='{syncResult.MainConnectionName}', Targets={string.Join(", ", syncResult.TargetsSynced)}, Tables OK={syncResult.SuccessfulTables}."
            : $"Database sync finished with errors. {syncResult.ErrorMessage} Tables OK={syncResult.SuccessfulTables}, Failed={syncResult.FailedTables}.";

        await _statusStore.WriteAsync(new SyncRuntimeStatus
        {
            State = syncResult.Success ? "Succeeded" : "Failed",
            Message = successMessage,
            MainConnectionName = syncResult.MainConnectionName,
            TargetsSynced = syncResult.TargetsSynced,
            SuccessfulTables = syncResult.SuccessfulTables,
            FailedTables = syncResult.FailedTables,
            StartedAtUtc = syncResult.StartedAtUtc,
            FinishedAtUtc = syncResult.FinishedAtUtc,
            Servers = serverReports
        }, cancellationToken).ConfigureAwait(false);
    }

    private List<ServerSyncReport> BuildIdleServerReports(string outcome, string reason) =>
        _connectionFactory.GetEnabledConnections()
            .Select(c => new ServerSyncReport
            {
                ConnectionId = c.Id,
                Name = c.Name,
                Server = c.Server,
                Database = c.Database,
                IsMain = c.IsMain,
                Outcome = outcome,
                Reason = reason
            }).ToList();

    private static List<ServerSyncReport> BuildServerReports(
        IReadOnlyList<Core.DTOs.DatabaseConnectivityResult> connectivity,
        Core.DTOs.SyncCycleResult syncResult)
    {
        var reports = new List<ServerSyncReport>();

        foreach (var conn in connectivity)
        {
            if (conn.IsMain)
            {
                reports.Add(new ServerSyncReport
                {
                    ConnectionId = conn.ConnectionId,
                    Name = conn.ConnectionName,
                    Server = conn.Server,
                    Database = conn.Database,
                    IsMain = true,
                    Outcome = conn.IsSuccessful ? "Source" : "Failed",
                    Reason = conn.IsSuccessful
                        ? "Main source database. Data is copied from here to targets."
                        : $"Main database unreachable: {conn.ErrorMessage}"
                });
                continue;
            }

            if (!conn.IsSuccessful)
            {
                reports.Add(new ServerSyncReport
                {
                    ConnectionId = conn.ConnectionId,
                    Name = conn.ConnectionName,
                    Server = conn.Server,
                    Database = conn.Database,
                    IsMain = false,
                    Outcome = "Failed",
                    Reason = $"Could not connect: {conn.ErrorMessage}"
                });
                continue;
            }

            var tableResults = syncResult.TableResults
                .Where(t => t.TargetConnectionId == conn.ConnectionId)
                .ToList();

            if (tableResults.Count == 0)
            {
                var syncedByName = syncResult.TargetsSynced.Contains(conn.ConnectionName);
                reports.Add(new ServerSyncReport
                {
                    ConnectionId = conn.ConnectionId,
                    Name = conn.ConnectionName,
                    Server = conn.Server,
                    Database = conn.Database,
                    IsMain = false,
                    Outcome = syncedByName ? "Synced" : "Skipped",
                    Reason = syncedByName
                        ? "Target reached, but no table results were recorded."
                        : (syncResult.ErrorMessage ?? "Target was skipped during sync.")
                });
                continue;
            }

            var ok = tableResults.Count(t => t.Success);
            var failed = tableResults.Count(t => !t.Success);
            var firstError = tableResults.FirstOrDefault(t => !t.Success)?.ErrorMessage;

            reports.Add(new ServerSyncReport
            {
                ConnectionId = conn.ConnectionId,
                Name = conn.ConnectionName,
                Server = conn.Server,
                Database = conn.Database,
                IsMain = false,
                Outcome = failed == 0 ? "Synced" : "Failed",
                TablesOk = ok,
                TablesFailed = failed,
                Reason = failed == 0
                    ? $"All {ok} tables synchronised successfully."
                    : $"Failed tables: {failed}/{tableResults.Count}. First error: {firstError}"
            });
        }

        return reports;
    }
}
