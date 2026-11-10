using DatabaseSync.Core.Enums;

namespace DatabaseSync.Core.Scheduling;

public readonly record struct TriggerDecision(SyncTriggerKind Kind, string Reason);

/// <summary>
/// Picks at most one trigger. Manual beats a due schedule, which beats a due interval.
/// </summary>
public static class SyncTriggerSelector
{
    public static TriggerDecision? Select(
        bool manualPending,
        bool scheduledEnabled,
        bool scheduleDue,
        bool intervalEnabled,
        bool intervalDue)
    {
        if (manualPending)
        {
            return new TriggerDecision(SyncTriggerKind.Manual, "Manual sync request is pending.");
        }

        if (scheduledEnabled && scheduleDue)
        {
            return new TriggerDecision(SyncTriggerKind.Scheduled, "A schedule occurrence is due.");
        }

        if (intervalEnabled && intervalDue)
        {
            return new TriggerDecision(SyncTriggerKind.Interval, "Interval sync is due.");
        }

        return null;
    }
}
