using DatabaseSync.Core.Enums;

namespace DatabaseSync.Core.Diff;

public sealed class ConflictBackupSettings
{
    /// <summary>At or below this row count (and the byte ceiling) the set is a JSON review file.</summary>
    public int FileRowThreshold { get; init; } = 1000;

    /// <summary>At or below this UTF-8 size the set is a JSON review file.</summary>
    public long FileMaxBytes { get; init; } = 5_000_000;

    /// <summary>When the set is large, write a target backup table instead of a JSONL file.</summary>
    public bool UseBackupTables { get; init; }
}

public static class ConflictBackupPlanner
{
    public static ConflictBackupForm Choose(int conflictRows, long estimatedBytes, ConflictBackupSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (conflictRows < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(conflictRows));
        }

        if (estimatedBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(estimatedBytes));
        }

        var small = conflictRows <= Math.Max(1, settings.FileRowThreshold)
                    && estimatedBytes <= Math.Max(1, settings.FileMaxBytes);
        if (small)
        {
            return ConflictBackupForm.JsonReviewFile;
        }

        return settings.UseBackupTables
            ? ConflictBackupForm.BackupTable
            : ConflictBackupForm.BulkExportFile;
    }
}
