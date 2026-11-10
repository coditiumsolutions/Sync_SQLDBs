using DatabaseSync.Core.Enums;

namespace DatabaseSync.Core.Diff;

/// <summary>
/// Decides whether a target row must be backed up before main's version is applied.
/// Main always wins. A conflict only changes whether the target's current row is preserved first.
/// </summary>
public sealed class ConflictClassificationContext
{
    /// <summary>True when the table has a comparable datetime change column.</summary>
    public bool HasChangeTimestampColumn { get; init; }

    /// <summary>
    /// When true, every update and delete is a conflict, including tables with no change column.
    /// Inserts are never conflicts.
    /// </summary>
    public bool AssumeDifferingRowsConflict { get; init; }

    /// <summary>
    /// Newest change timestamp currently stored on main. Used only for delete classification.
    /// </summary>
    public DateTimeOffset? MainMaxChangeTimestamp { get; init; }
}

public static class ConflictClassifier
{
    /// <summary>
    /// Update rule: the target is newer when its change timestamp is strictly greater than main's.
    /// A null target timestamp is not newer. A target timestamp with a null main timestamp is newer.
    /// Delete rule: there is no main row, so the target is newer when its timestamp is strictly greater
    /// than every change timestamp still present on main. If main has no timestamps and the target row
    /// does, the target is treated as newer. A null target timestamp is not newer.
    /// Without a change column, nothing is a conflict unless <see cref="ConflictClassificationContext.AssumeDifferingRowsConflict"/> is set.
    /// rowversion is not a change column: it is not comparable across servers.
    /// </summary>
    public static bool IsConflict(
        DiffKind kind,
        DateTimeOffset? mainTimestamp,
        DateTimeOffset? targetTimestamp,
        ConflictClassificationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (kind == DiffKind.Insert)
        {
            return false;
        }

        if (context.AssumeDifferingRowsConflict)
        {
            return true;
        }

        if (!context.HasChangeTimestampColumn)
        {
            return false;
        }

        if (kind == DiffKind.Update)
        {
            if (targetTimestamp is null)
            {
                return false;
            }

            if (mainTimestamp is null)
            {
                return true;
            }

            return targetTimestamp.Value > mainTimestamp.Value;
        }

        if (targetTimestamp is null)
        {
            return false;
        }

        if (context.MainMaxChangeTimestamp is null)
        {
            return true;
        }

        return targetTimestamp.Value > context.MainMaxChangeTimestamp.Value;
    }
}
