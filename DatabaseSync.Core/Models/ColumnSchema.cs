namespace DatabaseSync.Core.Models;

/// <summary>
/// Column metadata used when building sync SELECT / MERGE statements.
/// </summary>
public sealed class ColumnSchema
{
    public required string Name { get; init; }

    public required string SqlTypeName { get; init; }

    public bool IsPrimaryKey { get; init; }

    public bool IsIdentity { get; init; }

    public bool IsComputed { get; init; }

    public bool IsRowVersion { get; init; }

    public int Ordinal { get; init; }

    /// <summary>Columns that can be copied from source to target.</summary>
    public bool IsCopyable => !IsComputed && !IsRowVersion;
}
