using DatabaseSync.Core.Models;

namespace DatabaseSync.Core.Interfaces;

public interface IManualSyncRequestStore
{
    string RequestFilePath { get; }

    ManualSyncRequest Write(DateTimeOffset? requestedAtUtc = null);

    ManualSyncRequest? Read();

    /// <summary>Deletes the request when it is still the one that started, so a newer click stays queued.</summary>
    void CompleteIfCurrent(Guid requestId);
}
