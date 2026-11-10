using System.Text.Json.Serialization;
using DatabaseSync.Core.Enums;

namespace DatabaseSync.Core.Models;

/// <summary>
/// Worker synchronization behaviour settings.
/// Cycles start from a manual request, a local schedule, or an interval. Only one cycle runs at a time.
/// </summary>
public sealed class SyncSettings
{
    /// <summary>
    /// Legacy mirror. True when scheduled or interval sync is enabled.
    /// Not read after trigger config version 1. Use the mode flags instead.
    /// </summary>
    public bool EnableSynchronization { get; set; }

    /// <summary>
    /// Legacy mirror of the interval length, in seconds. Not used to decide when a cycle starts.
    /// </summary>
    public int SyncIntervalSeconds { get; set; } = 120;

    /// <summary>Legacy mirror of the interval length, in minutes.</summary>
    public int AutoSyncEveryMinutes { get; set; } = 2;

    /// <summary>Retired daily window start. Schedules replace it. Kept so old files still load.</summary>
    public string AutoSyncStartTime { get; set; } = "00:00";

    /// <summary>Retired daily window end. Schedules replace it. Kept so old files still load.</summary>
    public string AutoSyncEndTime { get; set; } = "23:59";

    /// <summary>
    /// 0 until <see cref="Scheduling.SyncSettingsMigrator"/> has copied the legacy switches onto interval sync.
    /// </summary>
    public int TriggerConfigVersion { get; set; }

    /// <summary>How often the worker looks for a due trigger, in seconds.</summary>
    public int TriggerPollSeconds { get; set; } = 5;

    /// <summary>
    /// A schedule occurrence still fires if the worker notices it within this many minutes.
    /// Older occurrences are missed and are not replayed.
    /// </summary>
    public int ScheduleGraceMinutes { get; set; } = 15;

    /// <summary>When true, each enabled schedule fires once per occurrence. Default off.</summary>
    public bool ScheduledSyncEnabled { get; set; }

    public List<SyncScheduleEntry> Schedules { get; set; } = [];

    /// <summary>
    /// When true, sync runs on a fixed interval. Off by default because each cycle hashes every syncable table.
    /// Missed interval ticks are not caught up.
    /// </summary>
    public bool IntervalSyncEnabled { get; set; }

    public int IntervalValue { get; set; } = 2;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public SyncIntervalUnit IntervalUnit { get; set; } = SyncIntervalUnit.Minutes;

    /// <summary>Delay between connection retry attempts, in seconds.</summary>
    public int RetryIntervalSeconds { get; set; } = 15;

    /// <summary>Maximum number of consecutive connection retry attempts per cycle.</summary>
    public int MaximumRetryAttempts { get; set; } = 2;

    /// <summary>When true, additional diagnostic details are written to the log.</summary>
    public bool EnableDetailedLogging { get; set; } = true;

    /// <summary>SQL command timeout used during table synchronisation.</summary>
    public int CommandTimeoutSeconds { get; set; } = 180;

    /// <summary>One-way sync from the nominated Main connection to all other enabled connections.</summary>
    public bool SyncMainToOthers { get; set; } = true;

    /// <summary>
    /// When true, cycles compute and log the differential and write conflict review files,
    /// but do not change target data or create backup tables.
    /// </summary>
    public bool DryRun { get; set; }

    /// <summary>SqlBulkCopy batch size for primary-key fingerprints.</summary>
    public int FingerprintBatchSize { get; set; } = 5000;

    /// <summary>How many differing keys are applied in one batch.</summary>
    public int DiffApplyBatchSize { get; set; } = 400;

    /// <summary>
    /// Conflict count at or below this (and <see cref="ConflictFileMaxBytes"/>) is written as one JSON review file.
    /// Above either ceiling the set is a JSONL bulk file, or a backup table when that option is on.
    /// </summary>
    public int ConflictFileRowThreshold { get; set; } = 1000;

    public long ConflictFileMaxBytes { get; set; } = 5_000_000;

    /// <summary>
    /// When true, a large conflict set is stored in a table under the sync schema on the target.
    /// Default false so production schemas stay unchanged.
    /// </summary>
    public bool UseConflictBackupTables { get; set; }

    /// <summary>
    /// When true, each conflict is also inserted into sync.ConflictLog on the target.
    /// Default false. The JSONL conflict log is always written.
    /// </summary>
    public bool WriteConflictLogTable { get; set; }

    /// <summary>
    /// Folder for review files, bulk exports, and conflict-log.jsonl.
    /// Relative paths are resolved next to sync-config.json.
    /// </summary>
    public string ConflictBackupFolder { get; set; } = "Conflicts";

    /// <summary>
    /// When true, every update and delete is backed up as a conflict, even when the table has no
    /// change-timestamp column or the target row is not newer. Default false.
    /// </summary>
    public bool AssumeDifferingRowsConflict { get; set; }

    /// <summary>
    /// First matching datetime column name wins. Empty uses the built-in name list.
    /// rowversion/timestamp columns are never selected.
    /// </summary>
    public List<string> ChangeTimestampColumnNames { get; set; } = [];

    public int GetEffectiveIntervalSeconds()
    {
        if (AutoSyncEveryMinutes > 0)
        {
            return Math.Max(60, AutoSyncEveryMinutes * 60);
        }

        return Math.Max(30, SyncIntervalSeconds);
    }

    public bool IsWithinAutoSyncWindow(DateTime localNow)
    {
        if (!TryParseTime(AutoSyncStartTime, out var start) ||
            !TryParseTime(AutoSyncEndTime, out var end))
        {
            return true;
        }

        var current = localNow.TimeOfDay;
        if (start <= end)
        {
            return current >= start && current <= end;
        }

        return current >= start || current <= end;
    }

    private static bool TryParseTime(string? value, out TimeSpan time)
    {
        time = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return TimeSpan.TryParse(value, out time);
    }
}
