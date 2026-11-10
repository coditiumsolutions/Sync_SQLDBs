using DatabaseSync.Core.Models;

namespace DatabaseSync.Core.Interfaces;

public interface ISyncStatusStore
{
    string StatusFilePath { get; }

    SyncRuntimeStatus Get();

    Task WriteAsync(SyncRuntimeStatus status, CancellationToken cancellationToken = default);
}
