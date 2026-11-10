using DatabaseSync.Core.Enums;

namespace DatabaseSync.Core.Diff;

/// <summary>
/// Primary-key fingerprint comparison. The SQL differential path applies the same three rules:
/// key only on main → insert, key on both with a different hash → update, key only on target → delete.
/// </summary>
public static class RowDiffEngine
{
    public readonly record struct RowFingerprint(string Key, byte[] Hash);

    public readonly record struct RowDiff(string Key, DiffKind Kind);

    public static IReadOnlyList<RowDiff> Diff(
        IEnumerable<RowFingerprint> source,
        IEnumerable<RowFingerprint> target)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);

        var sourceMap = ToMap(source, "source");
        var targetMap = ToMap(target, "target");
        var diffs = new List<RowDiff>();

        foreach (var (key, hash) in sourceMap)
        {
            if (!targetMap.TryGetValue(key, out var targetHash))
            {
                diffs.Add(new RowDiff(key, DiffKind.Insert));
            }
            else if (!hash.AsSpan().SequenceEqual(targetHash))
            {
                diffs.Add(new RowDiff(key, DiffKind.Update));
            }
        }

        foreach (var key in targetMap.Keys)
        {
            if (!sourceMap.ContainsKey(key))
            {
                diffs.Add(new RowDiff(key, DiffKind.Delete));
            }
        }

        return diffs;
    }

    private static Dictionary<string, byte[]> ToMap(IEnumerable<RowFingerprint> rows, string side)
    {
        var map = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (string.IsNullOrEmpty(row.Key))
            {
                throw new ArgumentException($"A {side} fingerprint key is empty.");
            }

            if (row.Hash is null || row.Hash.Length == 0)
            {
                throw new ArgumentException($"Fingerprint '{row.Key}' on {side} has no hash.");
            }

            if (!map.TryAdd(row.Key, row.Hash))
            {
                throw new InvalidOperationException(
                    $"Duplicate primary key '{row.Key}' in the {side} fingerprint set.");
            }
        }

        return map;
    }
}
