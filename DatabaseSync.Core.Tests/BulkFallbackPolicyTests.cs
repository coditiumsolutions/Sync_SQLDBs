using DatabaseSync.Core.Diff;

namespace DatabaseSync.Core.Tests;

public class BulkFallbackPolicyTests
{
    [Fact]
    public void No_op_and_disabled_fallback_stay_on_the_differential_path()
    {
        Assert.False(BulkFallbackPolicy.Decide(enabled: true, sourceRows: 1_000, mismatchedRows: 0).UseBulkPath);
        Assert.False(BulkFallbackPolicy.Decide(enabled: false, sourceRows: 10_000, mismatchedRows: 10_000).UseBulkPath);
        Assert.Equal("differential", BulkFallbackPolicy.Decide(enabled: true, sourceRows: 100_000, mismatchedRows: 0).Reason);
    }

    [Fact]
    public void Small_table_rule_is_optional_and_off_by_default()
    {
        var enabled = BulkFallbackPolicy.Decide(enabled: true, sourceRows: 12_000, mismatchedRows: 1, smallTableRows: 12_000);
        var justOver = BulkFallbackPolicy.Decide(enabled: true, sourceRows: 12_001, mismatchedRows: 1, smallTableRows: 12_000);
        var tenK = BulkFallbackPolicy.Decide(enabled: true, sourceRows: 10_000, mismatchedRows: 2_500);

        Assert.True(enabled.UseBulkPath);
        Assert.Equal("small-table", enabled.Reason);
        Assert.False(justOver.UseBulkPath);
        Assert.False(tenK.UseBulkPath);
    }

    [Fact]
    public void Hundred_k_one_percent_stays_differential_and_ten_percent_falls_back()
    {
        var onePercent = BulkFallbackPolicy.Decide(enabled: true, sourceRows: 100_000, mismatchedRows: 3_000);
        var tenPercent = BulkFallbackPolicy.Decide(enabled: true, sourceRows: 100_000, mismatchedRows: 30_000);

        Assert.False(onePercent.UseBulkPath);
        Assert.True(tenPercent.UseBulkPath);
        Assert.Equal("mismatched-rows", tenPercent.Reason);
    }

    [Fact]
    public void Fraction_and_absolute_count_each_trigger_on_their_own()
    {
        var byFraction = BulkFallbackPolicy.Decide(enabled: true, sourceRows: 40_000, mismatchedRows: 9_000);
        var byCount = BulkFallbackPolicy.Decide(enabled: true, sourceRows: 200_000, mismatchedRows: 16_000);

        Assert.Equal("changed-fraction", byFraction.Reason);
        Assert.Equal("mismatched-rows", byCount.Reason);
        Assert.False(BulkFallbackPolicy.Decide(
            enabled: true,
            sourceRows: 200_000,
            mismatchedRows: 16_000,
            smallTableRows: 0,
            mismatchedRowThreshold: 0,
            changedFraction: 0).UseBulkPath);
    }

    [Fact]
    public void Bulk_conflict_predicates_keep_the_timestamp_rules()
    {
        var update = BulkFallbackConflict.UpdateIsNewer("t", "s", "[UpdatedAt]");
        var delete = BulkFallbackConflict.DeleteIsNewer("t", "[UpdatedAt]", "@mainMax");

        Assert.Contains("t.[UpdatedAt] IS NOT NULL", update, StringComparison.Ordinal);
        Assert.Contains("s.[UpdatedAt] IS NULL", update, StringComparison.Ordinal);
        Assert.Contains("t.[UpdatedAt] > s.[UpdatedAt]", update, StringComparison.Ordinal);
        Assert.Contains("t.[UpdatedAt] IS NOT NULL", delete, StringComparison.Ordinal);
        Assert.Contains("@mainMax IS NULL", delete, StringComparison.Ordinal);
        Assert.Contains("t.[UpdatedAt] > @mainMax", delete, StringComparison.Ordinal);
    }
}
