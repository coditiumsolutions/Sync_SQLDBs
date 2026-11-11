using DatabaseSync.Core.DTOs;
using DatabaseSync.Core.Enums;

namespace DatabaseSync.Core.Interfaces;

/// <summary>
/// Orchestrates a full Main → secondary databases synchronisation cycle.
/// </summary>
public interface ISyncOrchestrator
{
    Task<SyncCycleResult> RunAsync(SyncTriggerKind trigger, CancellationToken cancellationToken = default);
}
