using DatabaseSync.Core.Enums;

namespace DatabaseSync.Core.DTOs;

public sealed record SyncTriggerOutcome
{
    public required SyncTriggerKind Trigger { get; init; }

    public bool Started { get; init; }

    public bool SkippedBecauseBusy { get; init; }

    public SyncTriggerKind? ActiveTrigger { get; init; }

    public SyncCycleResult? Cycle { get; init; }

    public string Message { get; init; } = string.Empty;

    public static SyncTriggerOutcome Skipped(SyncTriggerKind trigger, SyncTriggerKind? active, string message) =>
        new()
        {
            Trigger = trigger,
            Started = false,
            SkippedBecauseBusy = true,
            ActiveTrigger = active,
            Message = message
        };
}
