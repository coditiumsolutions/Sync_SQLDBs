using DatabaseSync.Core.Models;

namespace DatabaseSync.Core.Interfaces;

/// <summary>
/// Reads and writes the shared sync-config.json file.
/// </summary>
public interface ISyncConfigStore
{
    string ConfigFilePath { get; }

    SyncConfiguration Get();

    Task<SyncConfiguration> GetAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(SyncConfiguration configuration, CancellationToken cancellationToken = default);
}
