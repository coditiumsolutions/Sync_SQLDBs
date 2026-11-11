using DatabaseSync.Core.Enums;
using DatabaseSync.Core.Interfaces;

namespace DatabaseSync.Admin.Services;

/// <summary>
/// Queues one manual sync and runs it on a background task when this process can take the lock.
/// If a cycle is already running, the request file stays and the worker starts it afterwards.
/// </summary>
public sealed class ManualSyncHost
{
    private readonly IManualSyncRequestStore _requests;
    private readonly ISyncCycleRunner _runner;
    private readonly ILogger<ManualSyncHost> _logger;
    private int _running;

    public ManualSyncHost(
        IManualSyncRequestStore requests,
        ISyncCycleRunner runner,
        ILogger<ManualSyncHost> logger)
    {
        _requests = requests;
        _runner = runner;
        _logger = logger;
    }

    public void Queue()
    {
        _requests.Write();
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            return;
        }

        _ = Task.Run(RunAsync);
    }

    private async Task RunAsync()
    {
        try
        {
            while (_requests.Read() is not null)
            {
                var outcome = await _runner.TryRunAsync(SyncTriggerKind.Manual).ConfigureAwait(false);
                if (outcome.SkippedBecauseBusy || !outcome.Started)
                {
                    _logger.LogInformation("Manual sync was not started in the Admin site. {Message}", outcome.Message);
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Manual sync failed.");
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }
}
