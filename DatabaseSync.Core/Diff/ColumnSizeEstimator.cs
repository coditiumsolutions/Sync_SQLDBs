using DatabaseSync.Core.Models;

namespace DatabaseSync.Core.Diff;

/// <summary>
/// Rough per-column byte estimate used only for transfer logging. Not a SQL length.
/// </summary>
public static class ColumnSizeEstimator
{
    public static int EstimateRowBytes(IEnumerable<ColumnSchema> columns)
    {
        ArgumentNullException.ThrowIfNull(columns);
        var total = 0;
        foreach (var column in columns)
        {
            total += Estimate(column);
        }

        return total;
    }

    public static int Estimate(ColumnSchema column)
    {
        ArgumentNullException.ThrowIfNull(column);
        return column.SqlTypeName.ToLowerInvariant() switch
        {
            "bit" or "tinyint" => 1,
            "smallint" => 2,
            "int" or "real" or "smallmoney" => 4,
            "bigint" or "float" or "money" or "datetime" or "datetime2" or "smalldatetime" => 8,
            "datetimeoffset" => 10,
            "date" => 3,
            "time" => 5,
            "uniqueidentifier" => 16,
            "rowversion" or "timestamp" => 8,
            _ => 32
        };
    }
}
