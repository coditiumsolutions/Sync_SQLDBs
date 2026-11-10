using DatabaseSync.Core.DTOs;
using DatabaseSync.Core.Enums;

namespace DatabaseSync.Core.Interfaces;

/// <summary>
/// Runs one differential cycle if no other cycle is in progress.
/// A busy runner skips the trigger. Manual requests stay queued by <see cref="IManualSyncRequestStore"/>.
/// </summary>
public interface ISyncCycleRunner
{
    Task<SyncTriggerOutcome> TryRunAsync(SyncTriggerKind trigger, CancellationToken cancellationToken = default);
}
