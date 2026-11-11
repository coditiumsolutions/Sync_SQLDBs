using DatabaseSync.Core.Diff;
using DatabaseSync.Core.Models;

namespace DatabaseSync.Core.Tests;

public class RangeHashPlannerTests
{
    [Fact]
    public void Small_tables_are_a_single_bucket()
    {
        Assert.Equal(10_000, RangeHashPlanner.ChooseWidth(1, 10_000));
        Assert.Contains("HASHBYTES('SHA2_256'", RowHashSqlBuilder.BuildCompactRowHashExpression(new TableSchema
        {
            SchemaName = "bench",
            TableName = "Customer",
            Columns =
            [
                new ColumnSchema { Name = "CustomerId", SqlTypeName = "int", IsPrimaryKey = true, Ordinal = 1 },
                new ColumnSchema { Name = "Name", SqlTypeName = "nvarchar", Ordinal = 2 },
                new ColumnSchema { Name = "Notes", SqlTypeName = "nvarchar", Ordinal = 3 }
            ]
        }), StringComparison.Ordinal);
        Assert.Equal(0, RangeHashPlanner.BucketIndex(1, 1, 10_000));
        Assert.Equal(0, RangeHashPlanner.BucketIndex(10_000, 1, 10_000));
    }

    [Fact]
    public void Tables_under_one_million_rows_ship_one_fingerprint_stream()
    {
        Assert.True(RangeHashPlanner.ShipAllFingerprints(10_000));
        Assert.True(RangeHashPlanner.ShipAllFingerprints(100_000));
        Assert.True(RangeHashPlanner.ShipAllFingerprints(999_999));
        Assert.False(RangeHashPlanner.ShipAllFingerprints(1_000_000));
        Assert.False(RangeHashPlanner.ShipAllFingerprints(2_000_000));
    }

    [Fact]
    public void Canonical_hash_is_ordered_delimited_and_null_safe()
    {
        var hash = RowHashSqlBuilder.BuildCanonicalRowHashExpression(new TableSchema
        {
            SchemaName = "bench",
            TableName = "Customer",
            Columns =
            [
                new ColumnSchema { Name = "CustomerId", SqlTypeName = "int", IsPrimaryKey = true, Ordinal = 1 },
                new ColumnSchema { Name = "Notes", SqlTypeName = "xml", Ordinal = 3 },
                new ColumnSchema { Name = "Name", SqlTypeName = "nvarchar", Ordinal = 2 },
                new ColumnSchema { Name = "Rv", SqlTypeName = "timestamp", IsRowVersion = true, Ordinal = 4 }
            ]
        }, qualifier: "src");

        Assert.StartsWith("HASHBYTES('SHA2_256'", hash, StringComparison.Ordinal);
        Assert.Contains("CONCAT(NCHAR(30), CASE WHEN src.[Name] IS NULL THEN NCHAR(0) ELSE NCHAR(1) + CONVERT(nvarchar(20), LEN(", hash, StringComparison.Ordinal);
        Assert.Contains("NCHAR(30), CASE WHEN src.[Notes] IS NULL THEN NCHAR(0) ELSE NCHAR(1)", hash, StringComparison.Ordinal);
        Assert.Contains("CONVERT(nvarchar(max), src.[Notes])", hash, StringComparison.Ordinal);
        var nameAt = hash.IndexOf("src.[Name]", StringComparison.Ordinal);
        var notesAt = hash.IndexOf("src.[Notes]", StringComparison.Ordinal);
        Assert.True(nameAt >= 0 && notesAt > nameAt);
        Assert.DoesNotContain("CustomerId", hash, StringComparison.Ordinal);
        Assert.DoesNotContain("[Rv]", hash, StringComparison.Ordinal);
        Assert.Equal(1, CountOf(hash, "HASHBYTES"));
    }

    [Fact]
    public void Mismatched_range_token_is_two_checksums()
    {
        var token = RowHashSqlBuilder.BuildCheapRowTokenExpression(new TableSchema
        {
            SchemaName = "bench",
            TableName = "OrderLine",
            Columns =
            [
                new ColumnSchema { Name = "OrderLineId", SqlTypeName = "bigint", IsPrimaryKey = true, Ordinal = 1 },
                new ColumnSchema { Name = "Qty", SqlTypeName = "int", Ordinal = 2 },
                new ColumnSchema { Name = "Sku", SqlTypeName = "varchar", Ordinal = 3 }
            ]
        });

        Assert.Contains("BINARY_CHECKSUM([Qty], [Sku])", token, StringComparison.Ordinal);
        Assert.Contains("CHECKSUM([Qty], [Sku])", token, StringComparison.Ordinal);
        Assert.DoesNotContain("OrderLineId", token, StringComparison.Ordinal);
        Assert.DoesNotContain("HASHBYTES", token, StringComparison.Ordinal);
    }

    [Fact]
    public void Sixth_interval_cycle_is_a_full_hash()
    {
        Assert.True(IntervalHashPolicy.UseFullHash(hasWatermark: false, cyclesSinceFullHash: 0));
        Assert.False(IntervalHashPolicy.UseFullHash(hasWatermark: true, cyclesSinceFullHash: 0));
        Assert.False(IntervalHashPolicy.UseFullHash(hasWatermark: true, cyclesSinceFullHash: 4));
        Assert.True(IntervalHashPolicy.UseFullHash(hasWatermark: true, cyclesSinceFullHash: 5));
    }

    private static int CountOf(string text, string token)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(token, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += token.Length;
        }

        return count;
    }

    [Fact]
    public void Larger_tables_use_several_wide_buckets()
    {
        var width = RangeHashPlanner.ChooseWidth(1, 100_000);
        Assert.Equal(5_000, width);
        Assert.InRange((100_000 - 1) / width + 1, 2, 64);
    }

    [Fact]
    public void Million_key_span_stays_within_the_bucket_cap()
    {
        var width = RangeHashPlanner.ChooseWidth(1, 1_000_000);
        var buckets = (1_000_000 - 1) / width + 1;
        Assert.InRange(buckets, 2, 64);
        Assert.True(width > RangeHashPlanner.LeafSpan);
    }

    [Fact]
    public void Equal_bucket_hashes_are_not_mismatches()
    {
        var hash = new byte[] { 1, 2, 3, 4 };
        var source = new Dictionary<long, (long, byte[])> { [0] = (10, hash), [1] = (10, hash) };
        var target = new Dictionary<long, (long, byte[])> { [0] = (10, (byte[])hash.Clone()), [1] = (10, (byte[])hash.Clone()) };
        Assert.Empty(RangeHashPlanner.MismatchedBuckets(source, target));
    }

    [Fact]
    public void Count_or_hash_differences_and_one_sided_buckets_are_mismatches()
    {
        var same = new byte[] { 9, 9 };
        var other = new byte[] { 1, 2 };
        var source = new Dictionary<long, (long, byte[])>
        {
            [0] = (10, same),
            [1] = (4, other),
            [3] = (2, same)
        };
        var target = new Dictionary<long, (long, byte[])>
        {
            [0] = (10, same),
            [1] = (4, new byte[] { 3, 3 }),
            [2] = (5, same)
        };

        Assert.Equal(new long[] { 1, 2, 3 }, RangeHashPlanner.MismatchedBuckets(source, target));
    }

    [Fact]
    public void Bucket_bounds_stop_at_the_global_max()
    {
        var range = RangeHashPlanner.Bounds(origin: 1, width: 5_000, index: 1, globalMax: 8_000);
        Assert.Equal(5_001, range.Start);
        Assert.Equal(8_000, range.EndInclusive);
        Assert.Equal(3_000, range.Span);
    }
}
