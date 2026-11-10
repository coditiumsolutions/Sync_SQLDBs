using System.Text;
using DatabaseSync.Core.Models;
using DatabaseSync.Core.Sql;

namespace DatabaseSync.Core.Diff;

/// <summary>
/// Builds a SHA2_256 row fingerprint expression. Primary keys, computed columns, and rowversion
/// are excluded. Each data column is hashed on its own (so a wide value is not truncated by an
/// 8,000-byte HASHBYTES input on SQL Server 2016+), then the column hashes are folded in groups.
/// NULL is a distinct marker (0x00), not the same hash as an empty value.
/// </summary>
public static class RowHashSqlBuilder
{
    public const int HashesPerGroup = 128;

    public static IReadOnlyList<ColumnSchema> DataColumns(TableSchema table)
    {
        ArgumentNullException.ThrowIfNull(table);
        return table.Columns
            .Where(column => column.IsCopyable && !column.IsPrimaryKey)
            .OrderBy(column => column.Ordinal)
            .ToList();
    }

    public static string BuildRowHashExpression(TableSchema table)
    {
        var dataColumns = DataColumns(table);
        if (dataColumns.Count == 0)
        {
            return "CONVERT(varbinary(32), 0x)";
        }

        var groups = new List<string>();
        for (var offset = 0; offset < dataColumns.Count; offset += HashesPerGroup)
        {
            var group = dataColumns.Skip(offset).Take(HashesPerGroup).Select(ColumnHashExpression);
            groups.Add($"HASHBYTES('SHA2_256', {string.Join(" + ", group)})");
        }

        return groups.Count == 1
            ? groups[0]
            : $"HASHBYTES('SHA2_256', {string.Join(" + ", groups)})";
    }

    public static string BuildFingerprintSelect(TableSchema table)
    {
        ArgumentNullException.ThrowIfNull(table);
        var keys = table.PrimaryKeyColumns;
        if (keys.Count == 0)
        {
            throw new InvalidOperationException($"Table {table.FullName} has no primary key.");
        }

        var projection = new StringBuilder();
        foreach (var key in keys)
        {
            projection.Append(SqlQuoting.Quote(key.Name)).Append(", ");
        }

        projection.Append(BuildRowHashExpression(table)).Append(" AS RowHash");
        return $"SELECT {projection} FROM {table.FullName}";
    }

    public static string ColumnHashExpression(ColumnSchema column)
    {
        ArgumentNullException.ThrowIfNull(column);
        var quoted = SqlQuoting.Quote(column.Name);
        var converted = column.SqlTypeName.ToLowerInvariant() switch
        {
            "xml" or "sql_variant" or "hierarchyid" or "text" or "ntext"
                => $"CONVERT(nvarchar(max), {quoted})",
            _ => $"CONVERT(varbinary(max), {quoted})"
        };

        return $"ISNULL(HASHBYTES('SHA2_256', {converted}), 0x00)";
    }
}
