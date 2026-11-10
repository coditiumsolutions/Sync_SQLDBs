using DatabaseSync.Core.Models;

namespace DatabaseSync.Core.Diff;

/// <summary>
/// Picks the column used to decide whether a target row is newer than main.
/// Only datetime, datetime2, datetimeoffset, and smalldatetime match.
/// rowversion/timestamp is intentionally ignored because those counters are not comparable across servers.
/// </summary>
public static class ChangeTimestampDetector
{
    public static readonly string[] DefaultNames =
    [
        "ModifiedAt",
        "UpdatedAt",
        "LastModified",
        "LastModifiedAt",
        "LastUpdated",
        "LastUpdatedAt",
        "ModifiedDate",
        "UpdatedDate",
        "UpdateDate",
        "ChangeDate",
        "ChangedAt",
        "DateModified",
        "DateUpdated",
        "ModifiedOn",
        "UpdatedOn",
        "LastChangeDate",
        "RowModifiedAt",
        "LastUpdate"
    ];

    private static readonly HashSet<string> DateTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "datetime",
        "datetime2",
        "datetimeoffset",
        "smalldatetime"
    };

    public static ColumnSchema? Detect(IReadOnlyList<ColumnSchema> columns, IReadOnlyList<string>? preferredNames)
    {
        ArgumentNullException.ThrowIfNull(columns);
        var names = preferredNames is { Count: > 0 } ? preferredNames : DefaultNames;

        foreach (var name in names)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var match = columns.FirstOrDefault(column =>
                column.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase)
                && !column.IsComputed
                && DateTypes.Contains(column.SqlTypeName));

            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }
}
