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

    /// <summary>
    /// Eight-byte row token: BINARY_CHECKSUM and CHECKSUM of the data columns.
    /// Used to name the keys inside a range the grouped checksum already called mismatched.
    /// SHA-256 of every row in that range was slower than copying the rows on the wide link.
    /// </summary>
    public static string BuildCheapRowTokenExpression(TableSchema table, string? qualifier = null)
    {
        ArgumentNullException.ThrowIfNull(table);
        var dataColumns = DataColumns(table);
        if (dataColumns.Count == 0)
        {
            return "CONVERT(varbinary(8), 0x)";
        }

        var arguments = string.Join(", ", dataColumns.Select(column => ChecksumArgument(column, Qualify(qualifier, column.Name))));
        return $"CONVERT(binary(4), BINARY_CHECKSUM({arguments})) + CONVERT(binary(4), CHECKSUM({arguments}))";
    }

    /// <summary>
    /// SHA-256 of the data columns in ordinal order. CONCAT keeps the expression off the
    /// varbinary(max) concatenation path, which was several times slower on the target.
    /// Each column is preceded by NCHAR(30). NULL is NCHAR(0). A value is NCHAR(1), its
    /// character length, and the characters, so an empty value is not NULL and columns cannot blur.
    /// </summary>
    public static string BuildCanonicalRowHashExpression(TableSchema table, string? qualifier = null)
    {
        ArgumentNullException.ThrowIfNull(table);
        var dataColumns = DataColumns(table);
        if (dataColumns.Count == 0)
        {
            return "HASHBYTES('SHA2_256', 0x)";
        }

        var pieces = dataColumns.Select(column =>
        {
            var quoted = Qualify(qualifier, column.Name);
            var text = CanonicalText(column, quoted);
            return $"NCHAR(30), CASE WHEN {quoted} IS NULL THEN NCHAR(0) ELSE NCHAR(1) + CONVERT(nvarchar(20), LEN({text})) + N':' + ({text}) END";
        });
        return $"HASHBYTES('SHA2_256', CONVERT(varbinary(max), CONCAT({string.Join(", ", pieces)})))";
    }

    /// <summary>
    /// One SHA-256 over the concatenated column bytes. Used when a table has no single integer
    /// key and the fingerprints are joined on the target.
    /// </summary>
    public static string BuildCompactRowHashExpression(TableSchema table)
    {
        var dataColumns = DataColumns(table);
        if (dataColumns.Count == 0)
        {
            return "CONVERT(varbinary(32), 0x)";
        }

        var parts = dataColumns.Select(column =>
        {
            var quoted = SqlQuoting.Quote(column.Name);
            var payload = column.SqlTypeName.ToLowerInvariant() switch
            {
                "xml" or "sql_variant" or "hierarchyid" or "text" or "ntext"
                    => $"CONVERT(varbinary(max), CONVERT(nvarchar(max), {quoted}))",
                _ => $"CONVERT(varbinary(max), {quoted})"
            };
            return $"CASE WHEN {quoted} IS NULL THEN 0x00 ELSE 0x01 + {payload} END";
        });
        return $"HASHBYTES('SHA2_256', {string.Join(" + ", parts)})";
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

    private static string Qualify(string? qualifier, string name) =>
        string.IsNullOrEmpty(qualifier)
            ? SqlQuoting.Quote(name)
            : qualifier + "." + SqlQuoting.Quote(name);

    private static string ChecksumArgument(ColumnSchema column, string quoted) =>
        column.SqlTypeName.ToLowerInvariant() switch
        {
            "xml" or "sql_variant" or "hierarchyid" or "text" or "ntext" => $"CONVERT(nvarchar(max), {quoted})",
            _ => quoted
        };

    private static string CanonicalText(ColumnSchema column, string quoted) =>
        column.SqlTypeName.ToLowerInvariant() switch
        {
            "xml" or "sql_variant" or "hierarchyid" or "text" or "ntext" => $"CONVERT(nvarchar(max), {quoted})",
            "binary" or "varbinary" or "image" => $"CONVERT(nvarchar(max), CONVERT(varchar(max), {quoted}, 2))",
            "datetime" or "datetime2" or "smalldatetime" => $"CONVERT(nvarchar(40), {quoted}, 126)",
            "datetimeoffset" => $"CONVERT(nvarchar(40), {quoted}, 127)",
            "float" or "real" => $"CONVERT(nvarchar(50), {quoted}, 2)",
            "bit" => $"CONVERT(nvarchar(1), {quoted})",
            _ => $"CONVERT(nvarchar(max), {quoted})"
        };

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
