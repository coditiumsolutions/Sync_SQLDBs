using DatabaseSync.Core.Diff;
using DatabaseSync.Core.Enums;
using DatabaseSync.Core.Models;

namespace DatabaseSync.Core.Tests;

public class RowDiffEngineTests
{
    private static RowDiffEngine.RowFingerprint Row(string key, byte hash) =>
        new(key, [hash, 0]);

    [Fact]
    public void Diff_classifies_insert_update_delete_and_ignores_matches()
    {
        var diffs = RowDiffEngine.Diff(
            [Row("1", 1), Row("2", 2), Row("3", 9)],
            [Row("2", 2), Row("3", 3), Row("4", 4)]);

        Assert.Equal(
            [("1", DiffKind.Insert), ("3", DiffKind.Update), ("4", DiffKind.Delete)],
            diffs.Select(diff => (diff.Key, diff.Kind)).ToArray());
    }

    [Fact]
    public void Diff_empty_sides_produce_no_rows()
    {
        Assert.Empty(RowDiffEngine.Diff([], []));
    }

    [Fact]
    public void Diff_rejects_duplicate_keys()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            RowDiffEngine.Diff([Row("1", 1), Row("1", 2)], []));
        Assert.Contains("Duplicate primary key", error.Message, StringComparison.Ordinal);
    }
}

public class ConflictClassifierTests
{
    private static readonly DateTimeOffset Older = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Newer = new(2026, 11, 2, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Insert_is_never_a_conflict()
    {
        Assert.False(ConflictClassifier.IsConflict(DiffKind.Insert, Older, Newer, Context(true)));
        Assert.False(ConflictClassifier.IsConflict(DiffKind.Insert, null, null, Context(false, assume: true)));
    }

    [Fact]
    public void Update_conflicts_only_when_target_is_strictly_newer()
    {
        var context = Context(true, assume: false);
        Assert.True(ConflictClassifier.IsConflict(DiffKind.Update, Older, Newer, context));
        Assert.False(ConflictClassifier.IsConflict(DiffKind.Update, Newer, Older, context));
        Assert.False(ConflictClassifier.IsConflict(DiffKind.Update, Newer, Newer, context));
        Assert.False(ConflictClassifier.IsConflict(DiffKind.Update, Newer, null, context));
        Assert.True(ConflictClassifier.IsConflict(DiffKind.Update, null, Newer, context));
    }

    [Fact]
    public void Delete_uses_the_newest_timestamp_still_on_main()
    {
        var context = Context(true, mainMax: Older);
        Assert.True(ConflictClassifier.IsConflict(DiffKind.Delete, null, Newer, context));
        Assert.False(ConflictClassifier.IsConflict(DiffKind.Delete, null, Older, context));
        Assert.False(ConflictClassifier.IsConflict(DiffKind.Delete, null, null, context));
        Assert.True(ConflictClassifier.IsConflict(DiffKind.Delete, null, Newer, Context(true, mainMax: null)));
    }

    [Fact]
    public void Without_a_change_column_nothing_conflicts_unless_asked()
    {
        Assert.False(ConflictClassifier.IsConflict(DiffKind.Update, Newer, Newer, Context(false)));
        Assert.False(ConflictClassifier.IsConflict(DiffKind.Delete, null, Newer, Context(false)));
        Assert.True(ConflictClassifier.IsConflict(DiffKind.Update, null, null, Context(false, assume: true)));
        Assert.True(ConflictClassifier.IsConflict(DiffKind.Delete, null, null, Context(true, assume: true)));
    }

    private static ConflictClassificationContext Context(bool hasColumn, bool assume = false, DateTimeOffset? mainMax = null) =>
        new()
        {
            HasChangeTimestampColumn = hasColumn,
            AssumeDifferingRowsConflict = assume,
            MainMaxChangeTimestamp = mainMax
        };
}

public class ConflictBackupPlannerTests
{
    [Fact]
    public void Small_sets_use_a_review_file()
    {
        var settings = new ConflictBackupSettings { FileRowThreshold = 1000, FileMaxBytes = 5_000_000 };
        Assert.Equal(ConflictBackupForm.JsonReviewFile, ConflictBackupPlanner.Choose(0, 0, settings));
        Assert.Equal(ConflictBackupForm.JsonReviewFile, ConflictBackupPlanner.Choose(1000, 5_000_000, settings));
    }

    [Fact]
    public void Large_sets_use_a_bulk_file_or_a_backup_table()
    {
        var file = new ConflictBackupSettings { FileRowThreshold = 1000, FileMaxBytes = 5_000_000 };
        Assert.Equal(ConflictBackupForm.BulkExportFile, ConflictBackupPlanner.Choose(1001, 10, file));
        Assert.Equal(ConflictBackupForm.BulkExportFile, ConflictBackupPlanner.Choose(1, 5_000_001, file));

        var table = new ConflictBackupSettings { FileRowThreshold = 1000, FileMaxBytes = 5_000_000, UseBackupTables = true };
        Assert.Equal(ConflictBackupForm.BackupTable, ConflictBackupPlanner.Choose(1001, 10, table));
    }
}

public class ChangeTimestampDetectorTests
{
    [Fact]
    public void Prefers_the_configured_name_and_ignores_rowversion_and_computed_columns()
    {
        var columns = new List<ColumnSchema>
        {
            Column("UpdatedAt", "timestamp", rowVersion: true),
            Column("ModifiedAt", "datetime", computed: true),
            Column("LastModified", "datetime2"),
            Column("UpdatedAt", "datetime")
        };

        var match = ChangeTimestampDetector.Detect(columns, ["UpdatedAt", "LastModified"]);
        Assert.Equal("UpdatedAt", match?.Name);
        Assert.Equal("datetime", match?.SqlTypeName);
    }

    [Fact]
    public void Empty_name_list_uses_the_built_in_names()
    {
        var match = ChangeTimestampDetector.Detect([Column("ModifiedAt", "datetimeoffset")], []);
        Assert.Equal("ModifiedAt", match?.Name);
    }

    [Fact]
    public void Returns_null_when_nothing_matches()
    {
        Assert.Null(ChangeTimestampDetector.Detect([Column("Name", "nvarchar")], ["ModifiedAt"]));
    }

    private static ColumnSchema Column(string name, string type, bool computed = false, bool rowVersion = false) =>
        new()
        {
            Name = name,
            SqlTypeName = type,
            IsComputed = computed,
            IsRowVersion = rowVersion
        };
}

public class RowHashSqlBuilderTests
{
    [Fact]
    public void Hash_excludes_keys_computed_columns_and_rowversion_and_marks_nulls()
    {
        var table = new TableSchema
        {
            SchemaName = "dbo",
            TableName = "Orders",
            Columns =
            [
                new ColumnSchema { Name = "Id", SqlTypeName = "int", IsPrimaryKey = true, Ordinal = 1 },
                new ColumnSchema { Name = "Name", SqlTypeName = "nvarchar", Ordinal = 2 },
                new ColumnSchema { Name = "Total", SqlTypeName = "xml", Ordinal = 3 },
                new ColumnSchema { Name = "Calc", SqlTypeName = "int", IsComputed = true, Ordinal = 4 },
                new ColumnSchema { Name = "Rv", SqlTypeName = "timestamp", IsRowVersion = true, Ordinal = 5 }
            ]
        };

        var hash = RowHashSqlBuilder.BuildRowHashExpression(table);
        Assert.Contains("ISNULL(HASHBYTES('SHA2_256'", hash, StringComparison.Ordinal);
        Assert.Contains("[Name]", hash, StringComparison.Ordinal);
        Assert.Contains("CONVERT(nvarchar(max), [Total])", hash, StringComparison.Ordinal);
        Assert.DoesNotContain("[Id]", hash, StringComparison.Ordinal);
        Assert.DoesNotContain("[Calc]", hash, StringComparison.Ordinal);
        Assert.DoesNotContain("[Rv]", hash, StringComparison.Ordinal);

        var wide = new TableSchema
        {
            SchemaName = "dbo",
            TableName = "Wide",
            Columns = Enumerable.Range(1, 129)
                .Select(index => new ColumnSchema { Name = $"C{index}", SqlTypeName = "int", Ordinal = index })
                .ToList()
        };
        var wideHash = RowHashSqlBuilder.BuildRowHashExpression(wide);
        Assert.Contains(") + HASHBYTES('SHA2_256'", wideHash, StringComparison.Ordinal);
    }

}

public class RowKeyTests
{
    [Fact]
    public void Byte_arrays_compare_by_content()
    {
        var left = new RowKey([new byte[] { 1, 2 }, 5]);
        var right = new RowKey([new byte[] { 1, 2 }, 5]);
        var other = new RowKey([new byte[] { 1, 3 }, 5]);

        Assert.Equal(left, right);
        Assert.NotEqual(left, other);
        Assert.Equal(left.GetHashCode(), right.GetHashCode());
    }
}

public class TimestampValueTests
{
    [Fact]
    public void DateTimeOffset_is_converted_to_utc_and_datetime_keeps_the_clock_value()
    {
        var offset = new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.FromHours(2));
        var utc = TimestampValue.Read(offset);
        Assert.Equal(TimeSpan.Zero, utc?.Offset);
        Assert.Equal(10, utc?.UtcDateTime.Hour);

        var clock = TimestampValue.Read(new DateTime(2026, 11, 10, 12, 0, 0));
        Assert.Equal(12, clock?.Hour);
        Assert.Equal(TimeSpan.Zero, clock?.Offset);
        Assert.Null(TimestampValue.Read(DBNull.Value));
    }
}
