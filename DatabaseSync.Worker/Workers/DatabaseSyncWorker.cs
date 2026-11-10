using DatabaseSync.Core.Enums;
using DatabaseSync.Core.Interfaces;
using DatabaseSync.Core.Models;
using DatabaseSync.Core.Scheduling;

namespace DatabaseSync.Worker.Workers;

public sealed class DatabaseSyncWorker : BackgroundService
{
    private readonly ISyncCycleRunner _cycleRunner;
    private readonly ISyncConfigStore _configStore;
    private readonly ISyncStatusStore _statusStore;
    private readonly IManualSyncRequestStore _requests;
    private readonly ILogger<DatabaseSyncWorker> _logger;

    public DatabaseSyncWorker(
        ISyncCycleRunner cycleRunner,
        ISyncConfigStore configStore,
        ISyncStatusStore statusStore,
        IManualSyncRequestStore requests,
        ILogger<DatabaseSyncWorker> logger)
    {
        _cycleRunner = cycleRunner ?? throw new ArgumentNullException(nameof(cycleRunner));
        _configStore = configStore ?? throw new ArgumentNullException(nameof(configStore));
        _statusStore = statusStore ?? throw new ArgumentNullException(nameof(statusStore));
        _requests = requests ?? throw new ArgumentNullException(nameof(requests));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = _configStore.Get().SyncSettings;
        _logger.LogInformation(
            "Coditium.DatabaseSync worker started. Scheduled={ScheduledEnabled} ({ScheduleCount} entries), Interval={IntervalEnabled} every {IntervalValue} {IntervalUnit}, PollSeconds={PollSeconds}, DryRun={DryRun}, Config={ConfigPath}. Manual sync runs when a request is queued. Overlapping triggers are skipped; one manual request stays queued.",
            settings.ScheduledSyncEnabled,
            settings.Schedules.Count,
            settings.IntervalSyncEnabled,
            settings.IntervalValue,
            settings.IntervalUnit,
            settings.TriggerPollSeconds,
            settings.DryRun,
            _configStore.ConfigFilePath);

        await Task.Yield();
        var loggedSkips = new HashSet<string>(StringComparer.Ordinal);
        DateTimeOffset? suppressIntervalUntil = null;

        while (!stoppingToken.IsCancellationRequested)
        {
            settings = _configStore.Get().SyncSettings;
            var poll = TimeSpan.FromSeconds(Math.Clamp(settings.TriggerPollSeconds, 1, 120));

            try
            {
                suppressIntervalUntil = await EvaluateAsync(settings, loggedSkips, suppressIntervalUntil, stoppingToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error while evaluating sync triggers.");
                var failed = _statusStore.Get();
                failed.State = "Failed";
                failed.Message = $"Unhandled sync error: {ex.Message}";
                failed.StartedAtUtc ??= DateTimeOffset.UtcNow;
                failed.FinishedAtUtc = DateTimeOffset.UtcNow;
                await _statusStore.WriteAsync(failed, stoppingToken).ConfigureAwait(false);
            }

            try
            {
                await Task.Delay(poll, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        _logger.LogInformation("Coditium.DatabaseSync worker is stopping.");
    }

    private async Task<DateTimeOffset?> EvaluateAsync(
        SyncSettings settings,
        HashSet<string> loggedSkips,
        DateTimeOffset? suppressIntervalUntil,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var zone = TimeZoneInfo.Local;
        var grace = TimeSpan.FromMinutes(settings.ScheduleGraceMinutes);
        var status = _statusStore.Get();
        var due = settings.ScheduledSyncEnabled
            ? SyncSchedulePlanner.GetDue(settings.Schedules, now, zone, grace, status.FiredScheduleOccurrences)
            : [];
        var intervalDue = settings.IntervalSyncEnabled
            && IntervalSchedule.IsDue(status.NextIntervalRunUtc, now)
            && (suppressIntervalUntil is null || now >= suppressIntervalUntil.Value);
        var manual = _requests.Read();
        var decision = SyncTriggerSelector.Select(
            manual is not null,
            settings.ScheduledSyncEnabled,
            due.Count > 0,
            settings.IntervalSyncEnabled,
            intervalDue);

        if (decision is null)
        {
            await PublishClockAsync(settings, now, zone, grace, cancellationToken).ConfigureAwait(false);
            return suppressIntervalUntil;
        }

        var outcome = await _cycleRunner.TryRunAsync(decision.Value.Kind, cancellationToken).ConfigureAwait(false);
        if (outcome.SkippedBecauseBusy)
        {
            var skipKey = decision.Value.Kind == SyncTriggerKind.Manual && manual is not null
                ? "manual:" + manual.RequestId
                : decision.Value.Kind + ":" + (due.FirstOrDefault().OccurrenceKey ?? now.ToString("yyyyMMddHHmm"));
            if (loggedSkips.Add(skipKey))
            {
                _logger.LogWarning(
                    "Trigger {Trigger} did not start. {Message}",
                    decision.Value.Kind,
                    outcome.Message);
                await RecordSkipAsync(decision.Value.Kind, outcome.Message, cancellationToken).ConfigureAwait(false);
            }

            if (decision.Value.Kind == SyncTriggerKind.Interval || intervalDue)
            {
                suppressIntervalUntil = IntervalSchedule.After(now, settings.IntervalValue, settings.IntervalUnit);
                await AdvanceIntervalAsync(settings, suppressIntervalUntil.Value, cancellationToken).ConfigureAwait(false);
            }

            return suppressIntervalUntil;
        }

        if (!outcome.Started)
        {
            _logger.LogWarning("Trigger {Trigger} did not start. {Message}", decision.Value.Kind, outcome.Message);
            return suppressIntervalUntil;
        }

        await ConsumeOccurrencesAsync(settings, due, decision.Value.Kind == SyncTriggerKind.Interval || intervalDue, now, zone, grace, cancellationToken)
            .ConfigureAwait(false);
        loggedSkips.Clear();
        return null;
    }

    private async Task PublishClockAsync(
        SyncSettings settings,
        DateTimeOffset now,
        TimeZoneInfo zone,
        TimeSpan grace,
        CancellationToken cancellationToken)
    {
        var status = _statusStore.Get();
        if (status.State == "Running")
        {
            return;
        }

        status.NextScheduledRunUtc = settings.ScheduledSyncEnabled
            ? SyncSchedulePlanner.GetNextRunUtc(settings.Schedules, now, zone, grace, status.FiredScheduleOccurrences)
            : null;
        if (settings.IntervalSyncEnabled)
        {
            status.NextIntervalRunUtc ??= now;
        }
        else
        {
            status.NextIntervalRunUtc = null;
        }

        PruneOccurrences(status, now);
        await _statusStore.WriteAsync(status, cancellationToken).ConfigureAwait(false);
    }

    private async Task ConsumeOccurrencesAsync(
        SyncSettings settings,
        IReadOnlyList<ScheduleHit> due,
        bool advanceInterval,
        DateTimeOffset now,
        TimeZoneInfo zone,
        TimeSpan grace,
        CancellationToken cancellationToken)
    {
        var status = _statusStore.Get();
        status.FiredScheduleOccurrences ??= new Dictionary<string, DateTimeOffset>();
        foreach (var hit in due)
        {
            status.FiredScheduleOccurrences[hit.OccurrenceKey] = hit.OccurrenceUtc;
            _logger.LogInformation(
                "Schedule occurrence {Occurrence} ({Description}) was consumed by the cycle that just started.",
                hit.OccurrenceKey,
                hit.Description);
        }

        if (advanceInterval && settings.IntervalSyncEnabled)
        {
            status.NextIntervalRunUtc = IntervalSchedule.After(now, settings.IntervalValue, settings.IntervalUnit);
            _logger.LogInformation(
                "Next interval sync is {NextIntervalUtc:u}. Missed interval ticks are not replayed.",
                status.NextIntervalRunUtc);
        }

        status.NextScheduledRunUtc = settings.ScheduledSyncEnabled
            ? SyncSchedulePlanner.GetNextRunUtc(settings.Schedules, now, zone, grace, status.FiredScheduleOccurrences)
            : null;
        PruneOccurrences(status, now);
        await _statusStore.WriteAsync(status, cancellationToken).ConfigureAwait(false);
    }

    private async Task AdvanceIntervalAsync(SyncSettings settings, DateTimeOffset nextUtc, CancellationToken cancellationToken)
    {
        if (!settings.IntervalSyncEnabled)
        {
            return;
        }

        var status = _statusStore.Get();
        if (status.State == "Running")
        {
            _logger.LogInformation(
                "Interval trigger skipped while another sync is running. Next interval is held until {NextIntervalUtc:u} and the missed tick is not replayed.",
                nextUtc);
            return;
        }

        status.NextIntervalRunUtc = nextUtc;
        _logger.LogWarning(
            "Interval sync skipped because a sync is already running. Next interval at {NextIntervalUtc:u}.",
            status.NextIntervalRunUtc);
        await _statusStore.WriteAsync(status, cancellationToken).ConfigureAwait(false);
    }

    private async Task RecordSkipAsync(SyncTriggerKind trigger, string message, CancellationToken cancellationToken)
    {
        var status = _statusStore.Get();
        if (status.State == "Running")
        {
            return;
        }

        status.LastSkippedTrigger = trigger.ToString();
        status.LastSkipReason = message;
        status.LastSkippedAtUtc = DateTimeOffset.UtcNow;
        await _statusStore.WriteAsync(status, cancellationToken).ConfigureAwait(false);
    }

    private static void PruneOccurrences(SyncRuntimeStatus status, DateTimeOffset now)
    {
        if (status.FiredScheduleOccurrences is null || status.FiredScheduleOccurrences.Count == 0)
        {
            return;
        }

        var cutoff = now.AddDays(-14);
        var stale = status.FiredScheduleOccurrences
            .Where(pair => pair.Value < cutoff)
            .Select(pair => pair.Key)
            .ToList();
        foreach (var key in stale)
        {
            status.FiredScheduleOccurrences.Remove(key);
        }
    }
}
