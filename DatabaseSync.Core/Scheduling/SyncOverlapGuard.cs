using DatabaseSync.Core.Enums;

namespace DatabaseSync.Core.Scheduling;

/// <summary>
/// In-process mutual exclusion for sync cycles. A second trigger is rejected until <see cref="Exit"/>.
/// Cross-process exclusion is a file lock around this guard; overlapping triggers are skipped, not queued,
/// except a single pending manual request which stays on disk until a cycle can start.
/// </summary>
public sealed class SyncOverlapGuard
{
    private readonly object _gate = new();
    private SyncTriggerKind? _active;

    public SyncTriggerKind? Active
    {
        get
        {
            lock (_gate)
            {
                return _active;
            }
        }
    }

    public bool TryEnter(SyncTriggerKind trigger, out SyncTriggerKind? active)
    {
        lock (_gate)
        {
            if (_active is not null)
            {
                active = _active;
                return false;
            }

            _active = trigger;
            active = null;
            return true;
        }
    }

    public void Exit()
    {
        lock (_gate)
        {
            _active = null;
        }
    }
}
