namespace DatabaseSync.Core.Diff;

/// <summary>
/// Compares primary-key values loaded through ADO.NET. byte arrays compare by content.
/// </summary>
public sealed class RowKey : IEquatable<RowKey>
{
    private readonly object?[] _values;
    private readonly int _hashCode;

    public RowKey(object?[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        _values = values;
        _hashCode = ComputeHash(values);
    }

    public IReadOnlyList<object?> Values => _values;

    public bool Equals(RowKey? other)
    {
        if (other is null || other._values.Length != _values.Length)
        {
            return false;
        }

        for (var i = 0; i < _values.Length; i++)
        {
            if (!ValueEquals(_values[i], other._values[i]))
            {
                return false;
            }
        }

        return true;
    }

    public override bool Equals(object? obj) => obj is RowKey other && Equals(other);

    public override int GetHashCode() => _hashCode;

    public static bool ValueEquals(object? left, object? right)
    {
        if (left is null or DBNull)
        {
            return right is null or DBNull;
        }

        if (right is null or DBNull)
        {
            return false;
        }

        if (left is byte[] leftBytes && right is byte[] rightBytes)
        {
            return leftBytes.AsSpan().SequenceEqual(rightBytes);
        }

        return left.Equals(right);
    }

    private static int ComputeHash(object?[] values)
    {
        var hash = new HashCode();
        foreach (var value in values)
        {
            switch (value)
            {
                case null or DBNull:
                    hash.Add(0);
                    break;
                case byte[] bytes:
                    hash.AddBytes(bytes);
                    break;
                default:
                    hash.Add(value);
                    break;
            }
        }

        return hash.ToHashCode();
    }
}
