namespace DatabaseSync.Core.Diff;

/// <summary>
/// Splits an integer primary-key span into a few wide buckets. Used only when shipping every
/// primary key and hash would be large (a million rows or more). Smaller tables ship one stream.
/// </summary>
public static class RangeHashPlanner
{
    /// <summary>
    /// Source tables with fewer rows than this ship one (primary key, SHA-256) bulk copy.
    /// At or above this, a single integer key is compared with server-side range hashes first.
    /// </summary>
    public const long BulkFingerprintRowLimit = 1_000_000;

    /// <summary>A mismatched bucket at or below this span is read as individual rows.</summary>
    public const long LeafSpan = 2_000;

    public static bool ShipAllFingerprints(long sourceRows) => sourceRows < BulkFingerprintRowLimit;

    public readonly record struct KeyRange(long Start, long EndInclusive)
    {
        public long Span => EndInclusive >= Start ? EndInclusive - Start + 1 : 0;
    }

    /// <summary>
    /// Tables up to 20,000 keys are one bucket (a no-op compares a single hash).
    /// Larger tables use about 5,000 keys per bucket, capped at 64 buckets.
    /// </summary>
    public static long ChooseWidth(long minInclusive, long maxInclusive)
    {
        var span = Math.Max(1, maxInclusive - minInclusive + 1);
        if (span <= 20_000)
        {
            return span;
        }

        const long target = 5_000;
        const int maxBuckets = 64;
        var bucketsAtTarget = (span + target - 1) / target;
        if (bucketsAtTarget <= maxBuckets)
        {
            return target;
        }

        return (span + maxBuckets - 1) / maxBuckets;
    }

    public static long BucketIndex(long key, long origin, long width) =>
        width <= 0 ? 0 : (key - origin) / width;

    public static KeyRange Bounds(long origin, long width, long index, long globalMax)
    {
        var start = origin + (index * width);
        var end = start + width - 1;
        if (end > globalMax)
        {
            end = globalMax;
        }

        if (end < start)
        {
            end = start;
        }

        return new KeyRange(start, end);
    }

    public static IReadOnlyList<long> MismatchedBuckets(
        IReadOnlyDictionary<long, (long Count, byte[] Hash)> source,
        IReadOnlyDictionary<long, (long Count, byte[] Hash)> target)
    {
        var indexes = new SortedSet<long>(source.Keys);
        indexes.UnionWith(target.Keys);
        var mismatched = new List<long>();
        foreach (var index in indexes)
        {
            var hasSource = source.TryGetValue(index, out var sourceDigest);
            var hasTarget = target.TryGetValue(index, out var targetDigest);
            var sourceCount = hasSource ? sourceDigest.Count : 0;
            var targetCount = hasTarget ? targetDigest.Count : 0;
            if (sourceCount == 0 && targetCount == 0)
            {
                continue;
            }

            var same = sourceCount == targetCount
                       && sourceCount > 0
                       && sourceDigest.Hash is not null
                       && targetDigest.Hash is not null
                       && sourceDigest.Hash.AsSpan().SequenceEqual(targetDigest.Hash);
            if (!same)
            {
                mismatched.Add(index);
            }
        }

        return mismatched;
    }
}
