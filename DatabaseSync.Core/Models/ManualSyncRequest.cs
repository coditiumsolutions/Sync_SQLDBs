namespace DatabaseSync.Core.Models;

/// <summary>A single queued manual sync. A newer write replaces the previous request.</summary>
public sealed class ManualSyncRequest
{
    public Guid RequestId { get; set; }

    public DateTimeOffset RequestedAtUtc { get; set; }
}
