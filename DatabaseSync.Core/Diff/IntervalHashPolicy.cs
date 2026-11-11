namespace DatabaseSync.Core.Diff;

/// <summary>
/// Interval cycles can skip unchanged source rows with a watermark. Deletes and target-side
/// drift are caught by a full hash on every sixth cycle.
/// </summary>
public static class IntervalHashPolicy
{
    public const int FullHashEveryCycles = 6;

    public static bool UseFullHash(bool hasWatermark, int cyclesSinceFullHash) =>
        !hasWatermark || cyclesSinceFullHash >= FullHashEveryCycles - 1;
}
