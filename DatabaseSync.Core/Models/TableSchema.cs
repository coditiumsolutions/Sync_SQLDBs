namespace DatabaseSync.Core.Models;

/// <summary>
/// User-table metadata discovered from SQL Server for synchronisation.
/// </summary>
public sealed class TableSchema
{
    public required string SchemaName { get; init; }

    public required string TableName { get; init; }

    public required IReadOnlyList<ColumnSchema> Columns { get; init; }

    public string FullName => $"[{Escape(SchemaName)}].[{Escape(TableName)}]";

    public IReadOnlyList<ColumnSchema> PrimaryKeyColumns =>
        Columns.Where(c => c.IsPrimaryKey).OrderBy(c => c.Ordinal).ToList();

    public IReadOnlyList<ColumnSchema> CopyableColumns =>
        Columns.Where(c => c.IsCopyable).OrderBy(c => c.Ordinal).ToList();

    public bool HasIdentity => Columns.Any(c => c.IsIdentity);

    public bool HasPrimaryKey => PrimaryKeyColumns.Count > 0;

    private static string Escape(string identifier) => identifier.Replace("]", "]]", StringComparison.Ordinal);
}
