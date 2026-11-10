using DatabaseSync.Core.DTOs;

namespace DatabaseSync.Core.Interfaces;

/// <summary>
/// Orchestrates a full Main → secondary databases synchronisation cycle.
/// </summary>
public interface ISyncOrchestrator
{
    Task<SyncCycleResult> RunAsync(CancellationToken cancellationToken = default);
}
