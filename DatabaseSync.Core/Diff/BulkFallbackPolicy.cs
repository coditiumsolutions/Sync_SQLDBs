namespace DatabaseSync.Core.Diff;

/// <summary>
/// Decides when a per-key differential apply is more expensive than one bulk copy of the table.
/// A no-op (no mismatched rows) always stays on the differential path.
/// </summary>
public static class BulkFallbackPolicy
{
    /// <summary>
    /// Optional. Tables this size or smaller take the bulk path as soon as anything differs.
    /// Default 0: a measured full copy of the 10k tables was slower than the per-key path, so this rule is off.
    /// </summary>
    public const int DefaultSmallTableRows = 0;

    /// <summary>
    /// Dirty-range row count that switches a larger table onto the bulk path.
    /// A 100k 1% change stayed near 3,000 dirty rows and beat a full copy (24 s versus 34 s).
    /// A 100k 10% change covered about 30,000 rows and lost (67 s versus 35 s).
    /// </summary>
    public const int DefaultMismatchedRows = 15_000;

    /// <summary>
    /// Or when the dirty ranges cover at least this share of the source table, and at least
    /// <see cref="DefaultFractionMinRows"/> rows. Twenty percent is above the bucket waste of a 1% change
    /// and below a real 10% change. The minimum keeps a 10k table, where a full copy was not cheaper, off this rule.
    /// </summary>
    public const double DefaultChangedFraction = 0.20;

    public const int DefaultFractionMinRows = 8_000;

    public static BulkFallbackDecision Decide(
        bool enabled,
        long sourceRows,
        long mismatchedRows,
        int smallTableRows = DefaultSmallTableRows,
        int mismatchedRowThreshold = DefaultMismatchedRows,
        double changedFraction = DefaultChangedFraction,
        int fractionMinRows = DefaultFractionMinRows)
    {
        if (!enabled || mismatchedRows <= 0)
        {
            return BulkFallbackDecision.Differential;
        }

        if (smallTableRows > 0 && sourceRows <= smallTableRows)
        {
            return new BulkFallbackDecision(true, "small-table");
        }

        if (mismatchedRowThreshold > 0 && mismatchedRows >= mismatchedRowThreshold)
        {
            return new BulkFallbackDecision(true, "mismatched-rows");
        }

        if (changedFraction > 0
            && fractionMinRows > 0
            && sourceRows > 0
            && mismatchedRows >= fractionMinRows
            && (double)mismatchedRows / sourceRows >= changedFraction)
        {
            return new BulkFallbackDecision(true, "changed-fraction");
        }

        return BulkFallbackDecision.Differential;
    }
}

public readonly record struct BulkFallbackDecision(bool UseBulkPath, string Reason)
{
    public static BulkFallbackDecision Differential { get; } = new(false, "differential");
}

/// <summary>
/// Timestamp predicates for the bulk-copy path. They match <see cref="ConflictClassifier"/>:
/// a null target time is not newer, and a target time with a null main time is newer.
/// </summary>
public static class BulkFallbackConflict
{
    public static string UpdateIsNewer(string targetAlias, string sourceAlias, string quotedColumn)
    {
        var target = targetAlias + "." + quotedColumn;
        var source = sourceAlias + "." + quotedColumn;
        return $"({target} IS NOT NULL AND ({source} IS NULL OR {target} > {source}))";
    }

    public static string DeleteIsNewer(string targetAlias, string quotedColumn, string parameterName)
    {
        var target = targetAlias + "." + quotedColumn;
        return $"({target} IS NOT NULL AND ({parameterName} IS NULL OR {target} > {parameterName}))";
    }
}
