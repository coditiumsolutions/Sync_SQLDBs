using System.Data;
using System.Diagnostics;
using System.Text.Json;
using System.Globalization;
using System.Text;
using Dapper;
using DatabaseSync.Core.Diff;
using DatabaseSync.Core.DTOs;
using DatabaseSync.Core.Enums;
using DatabaseSync.Core.Interfaces;
using DatabaseSync.Core.Models;
using DatabaseSync.Core.Sql;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace DatabaseSync.Infrastructure.Services;

/// <summary>
/// Differential Main → target sync.
/// Under one million rows, Main streams a primary key and SHA-256 into a temp table on the target.
/// The target hashes locally and one join decides inserts, updates, and deletes. Full rows move only
/// for keys that differ. Main always wins. A newer target row is snapshotted before it is overwritten.
/// </summary>
public sealed class TableSyncService : ITableSyncService
{
    private const int ApplyChunkRows = 4000;

    private readonly ISqlConnectionFactory _connectionFactory;
    private readonly ISyncConfigStore _configStore;
    private readonly ILogger<TableSyncService> _logger;
    private readonly object _watermarkGate = new();
    private readonly Dictionary<string, WatermarkState> _watermarks = new(StringComparer.OrdinalIgnoreCase);

    public TableSyncService(
        ISqlConnectionFactory connectionFactory,
        ISyncConfigStore configStore,
        ILogger<TableSyncService> logger)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _configStore = configStore ?? throw new ArgumentNullException(nameof(configStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<TableSyncResult> SyncTableAsync(
        string sourceConnectionId,
        string targetConnectionId,
        TableSchema table,
        SyncTableRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(request);

        var stopwatch = Stopwatch.StartNew();
        var target = _connectionFactory.GetConnection(targetConnectionId);
        var settings = _configStore.Get().SyncSettings;
        var timeout = settings.CommandTimeoutSeconds;
        var dryRun = request.DryRun || settings.DryRun;

        if (!table.HasPrimaryKey)
        {
            _logger.LogWarning(
                "Skipping {Table}: differential sync requires a primary key, so the table was not copied.",
                table.FullName);
            return Result(table, target, stopwatch, dryRun, success: true, skipped: true,
                error: "Skipped: table has no primary key.");
        }

        var copyable = table.CopyableColumns;
        if (copyable.Count == 0)
        {
            return Result(table, target, stopwatch, dryRun, success: false, skipped: false,
                error: "Table has no copyable columns.");
        }

        var changeColumn = ChangeTimestampDetector.Detect(table.Columns, settings.ChangeTimestampColumnNames);
        var rowVersion = table.Columns.FirstOrDefault(column => column.IsRowVersion);
        if (changeColumn is null)
        {
            _logger.LogInformation(
                "Table {Table} has no comparable change-timestamp column. Conflict rule: main is applied and the target row is not backed up unless AssumeDifferingRowsConflict is true (currently {Assume}). rowversion is not used because it is not comparable across servers.",
                table.FullName,
                settings.AssumeDifferingRowsConflict);
        }

        try
        {
            await using var sourceConnection = (SqlConnection)_connectionFactory.CreateConnection(sourceConnectionId);
            await using var targetConnection = (SqlConnection)_connectionFactory.CreateConnection(targetConnectionId);
            await sourceConnection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await targetConnection.OpenAsync(cancellationToken).ConfigureAwait(false);

            var targetCountTask = ScalarLongAsync(targetConnection, $"SELECT COUNT_BIG(1) FROM {table.FullName};", timeout, cancellationToken);
            var sourceCountTask = ScalarLongAsync(sourceConnection, $"SELECT COUNT_BIG(1) FROM {table.FullName};", timeout, cancellationToken);
            await Task.WhenAll(targetCountTask, sourceCountTask).ConfigureAwait(false);
            var targetRows = await targetCountTask.ConfigureAwait(false);

            if (targetRows == 0)
            {
                var copied = await CopyAllAsInsertsAsync(
                    sourceConnection, targetConnection, table, target, copyable, settings, request, dryRun, changeColumn, stopwatch, timeout, cancellationToken)
                    .ConfigureAwait(false);
                await RememberWatermarkAsync(sourceConnection, sourceConnectionId, targetConnectionId, table, changeColumn, rowVersion, request, copied.Success, fullHash: true, timeout, cancellationToken)
                    .ConfigureAwait(false);
                return copied;
            }

            var conflictsPossible = changeColumn is not null || settings.AssumeDifferingRowsConflict;
            var sourceRows = await sourceCountTask.ConfigureAwait(false);

            if (sourceRows == 0 && !conflictsPossible)
            {
                if (!dryRun)
                {
                    await DeleteChunkedAsync(targetConnection, table, timeout, cancellationToken).ConfigureAwait(false);
                }

                stopwatch.Stop();
                LogSummary(table, target, request, dryRun, changeColumn, sourceRows, inserted: 0, updated: 0, deleted: targetRows,
                    conflicted: 0, unchanged: 0, transferred: 0, bytes: targetRows * ColumnSizeEstimator.EstimateRowBytes(table.PrimaryKeyColumns),
                    backup: null, stopwatch);
                var cleared = Result(table, target, stopwatch, dryRun, true, false, sourceRows, targetRows, 0, 0, targetRows, 0, 0, 0,
                    targetRows * ColumnSizeEstimator.EstimateRowBytes(table.PrimaryKeyColumns), null, changeColumn?.Name);
                await RememberWatermarkAsync(sourceConnection, sourceConnectionId, targetConnectionId, table, changeColumn, rowVersion, request, cleared.Success, fullHash: true, timeout, cancellationToken)
                    .ConfigureAwait(false);
                return cleared;
            }

            var watermarkKey = WatermarkKey(sourceConnectionId, targetConnectionId, table);
            var fullHash = true;
            WatermarkState? watermark = null;
            lock (_watermarkGate)
            {
                if (request.Trigger == SyncTriggerKind.Interval && _watermarks.TryGetValue(watermarkKey, out var existing))
                {
                    watermark = existing;
                    fullHash = IntervalHashPolicy.UseFullHash(true, existing.CyclesSinceFullHash);
                }
            }

            var canFilter = changeColumn is not null || rowVersion is not null;
            string? sourcePredicate = null;
            List<(string Name, object Value)> sourceParameters = [];
            var includeDeletes = true;
            if (!fullHash && canFilter && watermark is not null && TryBuildWatermarkFilter(table, changeColumn, rowVersion, watermark, out sourcePredicate, out sourceParameters))
            {
                includeDeletes = false;
                _logger.LogInformation(
                    "Interval watermark for {Table}. Only source rows newer than the last cycle are hashed. Deletes wait for the full hash (cycle {Cycle} of {Every}).",
                    table.FullName,
                    watermark.CyclesSinceFullHash + 1,
                    IntervalHashPolicy.FullHashEveryCycles);
            }
            else
            {
                fullHash = true;
            }

            List<DiffKey> diffs;
            long fingerprintRows;
            long bytes;
            if (fullHash && IsSingleIntegerKey(table))
            {
                var ranged = await CollectIntegerRangeDiffsAsync(
                    sourceConnection, targetConnection, table, timeout, settings.FingerprintBatchSize, cancellationToken).ConfigureAwait(false);
                diffs = ranged.Diffs;
                fingerprintRows = ranged.SourceRows;
                bytes = ranged.Bytes;
            }
            else
            {
                var hashed = await CollectFingerprintDiffsAsync(
                    sourceConnection, targetConnection, table, timeout, settings.FingerprintBatchSize,
                    sourcePredicate, targetPredicate: null, sourceParameters, includeDeletes, cancellationToken)
                    .ConfigureAwait(false);
                diffs = hashed.Diffs;
                fingerprintRows = hashed.SourceRows;
                bytes = hashed.Bytes;
            }

            var inserted = diffs.Count(row => row.Kind == DiffKind.Insert);
            var updated = diffs.Count(row => row.Kind == DiffKind.Update);
            var deleted = diffs.Count(row => row.Kind == DiffKind.Delete);
            var unchanged = Math.Max(0, fingerprintRows - inserted - updated);
            var pkBytes = ColumnSizeEstimator.EstimateRowBytes(table.PrimaryKeyColumns);
            var rowBytes = ColumnSizeEstimator.EstimateRowBytes(copyable);

            if (diffs.Count == 0)
            {
                stopwatch.Stop();
                LogSummary(table, target, request, dryRun, changeColumn, fingerprintRows, 0, 0, 0, 0, unchanged, 0, bytes, null, stopwatch);
                var unchangedResult = Result(table, target, stopwatch, dryRun, true, false, fingerprintRows, 0, 0, 0, 0, 0, unchanged, 0, bytes, null, changeColumn?.Name);
                await RememberWatermarkAsync(sourceConnection, sourceConnectionId, targetConnectionId, table, changeColumn, rowVersion, request, unchangedResult.Success, fullHash, timeout, cancellationToken)
                    .ConfigureAwait(false);
                return unchangedResult;
            }

            var needsMainMax = changeColumn is not null && diffs.Any(row => row.Kind == DiffKind.Delete);
            var mainMaxTask = needsMainMax
                ? sourceConnection.ExecuteScalarAsync<object?>(new CommandDefinition(
                    $"SELECT MAX({Q(changeColumn!.Name)}) FROM {table.FullName};",
                    commandTimeout: timeout,
                    cancellationToken: cancellationToken))
                : Task.FromResult<object?>(null);
            var stagingTask = ExecuteAsync(targetConnection, $"""
                IF OBJECT_ID('tempdb..#SyncStaging') IS NOT NULL DROP TABLE #SyncStaging;
                SELECT TOP (0) * INTO #SyncStaging FROM {table.FullName};
                """, timeout, cancellationToken);
            await Task.WhenAll(mainMaxTask, stagingTask).ConfigureAwait(false);
            var mainMax = TimestampValue.Read(await mainMaxTask.ConfigureAwait(false));

            var classification = new ConflictClassificationContext
            {
                HasChangeTimestampColumn = changeColumn is not null,
                AssumeDifferingRowsConflict = settings.AssumeDifferingRowsConflict,
                MainMaxChangeTimestamp = mainMax
            };

            var backupSettings = new ConflictBackupSettings
            {
                FileRowThreshold = settings.ConflictFileRowThreshold,
                FileMaxBytes = settings.ConflictFileMaxBytes,
                UseBackupTables = settings.UseConflictBackupTables && !dryRun
            };
            IConflictTableWriter? tableWriter = backupSettings.UseBackupTables
                ? new BackupTableWriter(targetConnection, table, copyable, timeout, request.CycleId)
                : null;
            IConflictLogTable? logTable = settings.WriteConflictLogTable && !dryRun
                ? new ConflictLogTableWriter(targetConnection, table, target.Name, request.CycleId, timeout)
                : null;

            await using var backups = ConflictBackupSession.Create(
                backupSettings,
                _logger,
                request.BackupFolder,
                $"{table.SchemaName}.{table.TableName}",
                target.Name,
                request.CycleId,
                request.Trigger.ToString(),
                dryRun,
                tableWriter,
                logTable);

            var batchSize = Math.Min(ApplyChunkRows, Math.Max(1, settings.DiffApplyBatchSize));
            try
            {
                for (var offset = 0; offset < diffs.Count; offset += batchSize)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var batch = diffs.Skip(offset).Take(batchSize).ToList();
                    await ApplyBatchAsync(
                        sourceConnection,
                        targetConnection,
                        table,
                        copyable,
                        changeColumn,
                        batch,
                        classification,
                        backups,
                        dryRun,
                        timeout,
                        cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                await backups.CompleteAsync(cancellationToken).ConfigureAwait(false);
            }

            bytes += (inserted + updated) * rowBytes;
            bytes += deleted * pkBytes;
            stopwatch.Stop();
            LogSummary(table, target, request, dryRun, changeColumn, fingerprintRows, inserted, updated, deleted,
                backups.Count, unchanged, inserted + updated, bytes, backups.Location, stopwatch);

            var applied = Result(table, target, stopwatch, dryRun, true, false, fingerprintRows, inserted + updated + deleted,
                inserted, updated, deleted, backups.Count, unchanged, inserted + updated, bytes, backups.Location, changeColumn?.Name);
            await RememberWatermarkAsync(sourceConnection, sourceConnectionId, targetConnectionId, table, changeColumn, rowVersion, request, applied.Success, fullHash, timeout, cancellationToken)
                .ConfigureAwait(false);
            return applied;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            stopwatch.Stop();
            _logger.LogError(ex, "Failed differential sync of {Table} to {Target}.", table.FullName, target.Name);
            return Result(table, target, stopwatch, dryRun, false, false, error: ex.Message, changeColumn: changeColumn?.Name);
        }
    }

    private async Task<TableSyncResult> CopyAllAsInsertsAsync(
        SqlConnection sourceConnection,
        SqlConnection targetConnection,
        TableSchema table,
        SyncConnection target,
        IReadOnlyList<ColumnSchema> copyable,
        SyncSettings settings,
        SyncTableRequest request,
        bool dryRun,
        ColumnSchema? changeColumn,
        Stopwatch stopwatch,
        int timeout,
        CancellationToken cancellationToken)
    {
        var selectSql = $"SELECT {ColumnList(copyable)} FROM {table.FullName};";
        long rows;
        long bytes;
        if (dryRun)
        {
            rows = await ScalarLongAsync(sourceConnection, $"SELECT COUNT_BIG(1) FROM {table.FullName};", timeout, cancellationToken)
                .ConfigureAwait(false);
            bytes = rows * ColumnSizeEstimator.EstimateRowBytes(copyable);
        }
        else
        {
            await ExecuteAsync(targetConnection, $"""
                IF OBJECT_ID('tempdb..#SyncStaging') IS NOT NULL DROP TABLE #SyncStaging;
                SELECT TOP (0) * INTO #SyncStaging FROM {table.FullName};
                """, timeout, cancellationToken).ConfigureAwait(false);

            // Bulk copy into the temp table before IDENTITY_INSERT is turned on for the real table.
            // SQL Server allows IDENTITY_INSERT for only one table per session, and SqlBulkCopy
            // turns it on for a staging table that still has an identity column.
            rows = await BulkCopyQueryAsync(
                sourceConnection, targetConnection, selectSql, "#SyncStaging", copyable, timeout, settings.FingerprintBatchSize, transaction: null, cancellationToken)
                .ConfigureAwait(false);

            if (table.HasIdentity)
            {
                await ExecuteAsync(targetConnection, $"SET IDENTITY_INSERT {table.FullName} ON;", timeout, cancellationToken)
                    .ConfigureAwait(false);
            }

            try
            {
                await ExecuteAsync(targetConnection, BuildMergeSql(table, copyable), timeout, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (table.HasIdentity)
                {
                    await ExecuteAsync(targetConnection, $"SET IDENTITY_INSERT {table.FullName} OFF;", timeout, cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            bytes = rows * ColumnSizeEstimator.EstimateRowBytes(copyable);
        }

        stopwatch.Stop();
        _logger.LogInformation(
            "Differential sync {Table} => {Target}. Target was empty; all source rows are inserts. Trigger={Trigger} DryRun={DryRun} Inserted={Inserted} EstimatedBytes={EstimatedBytes} ElapsedMs={ElapsedMs}",
            table.FullName, target.Name, request.Trigger, dryRun, rows, bytes, stopwatch.ElapsedMilliseconds);

        return Result(table, target, stopwatch, dryRun, true, false, rows, rows, rows, 0, 0, 0, 0, rows, bytes, null, changeColumn?.Name);
    }

    private async Task ApplyBatchAsync(
        SqlConnection sourceConnection,
        SqlConnection targetConnection,
        TableSchema table,
        IReadOnlyList<ColumnSchema> copyable,
        ColumnSchema? changeColumn,
        IReadOnlyList<DiffKey> batch,
        ConflictClassificationContext classification,
        ConflictBackupSession backups,
        bool dryRun,
        int timeout,
        CancellationToken cancellationToken)
    {
        await Task.WhenAll(
            LoadBatchKeysAsync(sourceConnection, table, batch, timeout, cancellationToken),
            LoadBatchKeysAsync(targetConnection, table, batch, timeout, cancellationToken)).ConfigureAwait(false);

        var keyNames = table.PrimaryKeyColumns.Select(column => column.Name).ToArray();
        Dictionary<RowKey, DateTimeOffset?> mainStamps;
        Dictionary<RowKey, DateTimeOffset?> targetStamps;
        if (changeColumn is null)
        {
            mainStamps = [];
            targetStamps = [];
        }
        else
        {
            var mainStampTask = ReadStampsAsync(sourceConnection, table, changeColumn, "k.Op = 'U'", timeout, cancellationToken);
            var targetStampTask = ReadStampsAsync(targetConnection, table, changeColumn, "k.Op IN ('U', 'D')", timeout, cancellationToken);
            await Task.WhenAll(mainStampTask, targetStampTask).ConfigureAwait(false);
            mainStamps = await mainStampTask.ConfigureAwait(false);
            targetStamps = await targetStampTask.ConfigureAwait(false);
        }


        var conflicts = new List<DiffKey>();
        foreach (var row in batch)
        {
            if (row.Kind == DiffKind.Insert)
            {
                continue;
            }

            mainStamps.TryGetValue(row.ToKey(), out var mainStamp);
            targetStamps.TryGetValue(row.ToKey(), out var targetStamp);
            if (ConflictClassifier.IsConflict(row.Kind, mainStamp, targetStamp, classification))
            {
                conflicts.Add(row);
            }
        }

        if (conflicts.Count > 0)
        {
            await Task.WhenAll(
                LoadBatchKeysAsync(sourceConnection, table, conflicts, timeout, cancellationToken, "#ConflictKeys"),
                LoadBatchKeysAsync(targetConnection, table, conflicts, timeout, cancellationToken, "#ConflictKeys")).ConfigureAwait(false);

            var targetValueTask = ReadRowsAsync(targetConnection, table, copyable, "#ConflictKeys", "k.Op IN ('U', 'D')", timeout, cancellationToken);
            var mainValueTask = ReadRowsAsync(sourceConnection, table, copyable, "#ConflictKeys", "k.Op = 'U'", timeout, cancellationToken);
            await Task.WhenAll(targetValueTask, mainValueTask).ConfigureAwait(false);
            var targetValues = await targetValueTask.ConfigureAwait(false);
            var mainValues = await mainValueTask.ConfigureAwait(false);

            foreach (var conflict in conflicts)
            {
                var key = conflict.ToKey();
                targetValues.TryGetValue(key, out var targetRow);
                if (targetRow is null)
                {
                    continue;
                }

                mainValues.TryGetValue(key, out var mainRow);
                mainStamps.TryGetValue(key, out var mainStamp);
                targetStamps.TryGetValue(key, out var targetStamp);
                await backups.AddAsync(new ConflictRow
                {
                    Operation = conflict.Kind == DiffKind.Delete ? "Delete" : "Update",
                    KeyNames = keyNames,
                    KeyValues = conflict.Values,
                    TargetValues = targetRow,
                    MainValues = conflict.Kind == DiffKind.Update ? mainRow : null,
                    MainChangeTimestamp = mainStamp,
                    TargetChangeTimestamp = targetStamp
                }, cancellationToken).ConfigureAwait(false);
            }

            await backups.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        if (dryRun)
        {
            return;
        }

        var hasUpsert = batch.Any(row => row.Kind is DiffKind.Insert or DiffKind.Update);
        var hasDelete = batch.Any(row => row.Kind == DiffKind.Delete);
        if (hasUpsert)
        {
            await ExecuteAsync(targetConnection, "TRUNCATE TABLE #SyncStaging;", timeout, cancellationToken)
                .ConfigureAwait(false);
            var selectSql = $"""
                SELECT (
                    SELECT {ColumnList(copyable, "src")}
                    FROM {table.FullName} AS src
                    INNER JOIN #BatchKeys AS k ON {Join("src", "k", table)}
                    WHERE k.Op IN ('I', 'U')
                    FOR JSON PATH);
                """;
            await InsertJsonAsync(
                sourceConnection, targetConnection, selectSql, "#SyncStaging", copyable, timeout, cancellationToken)
                .ConfigureAwait(false);
        }


        var applySql = new StringBuilder();
        if (hasUpsert && table.HasIdentity)
        {
            applySql.Append("SET IDENTITY_INSERT ").Append(table.FullName).AppendLine(" ON;");
        }

        if (hasUpsert)
        {
            applySql.AppendLine(BuildMergeSql(table, copyable));
        }

        if (hasDelete)
        {
            applySql.AppendLine($"""
                DELETE tgt
                FROM {table.FullName} AS tgt
                INNER JOIN #BatchKeys AS k ON {Join("tgt", "k", table)}
                WHERE k.Op = 'D';
                """);
        }

        if (hasUpsert && table.HasIdentity)
        {
            applySql.Append("SET IDENTITY_INSERT ").Append(table.FullName).AppendLine(" OFF;");
        }

        if (applySql.Length > 0)
        {
            try
            {
                await ExecuteAsync(targetConnection, applySql.ToString(), timeout, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                if (table.HasIdentity)
                {
                    await ExecuteAsync(targetConnection, $"SET IDENTITY_INSERT {table.FullName} OFF;", timeout, cancellationToken)
                        .ConfigureAwait(false);
                }

                throw;
            }
        }

    }

    private static bool IsSingleIntegerKey(TableSchema table)
    {
        if (table.PrimaryKeyColumns.Count != 1)
        {
            return false;
        }

        return table.PrimaryKeyColumns[0].SqlTypeName.ToLowerInvariant() is "tinyint" or "smallint" or "int" or "bigint";
    }

    private async Task<CollectedDiff> CollectIntegerRangeDiffsAsync(
        SqlConnection sourceConnection,
        SqlConnection targetConnection,
        TableSchema table,
        int timeout,
        int batchSize,
        CancellationToken cancellationToken)
    {
        var pk = Q(table.PrimaryKeyColumns[0].Name);
        var sourceEdgeTask = ReadKeyEdgeAsync(sourceConnection, table, pk, timeout, cancellationToken);
        var targetEdgeTask = ReadKeyEdgeAsync(targetConnection, table, pk, timeout, cancellationToken);
        await Task.WhenAll(sourceEdgeTask, targetEdgeTask).ConfigureAwait(false);
        var sourceEdge = await sourceEdgeTask.ConfigureAwait(false);
        var targetEdge = await targetEdgeTask.ConfigureAwait(false);
        if (sourceEdge.Count == 0 || targetEdge.Count == 0 || sourceEdge.Min is null || sourceEdge.Max is null || targetEdge.Min is null || targetEdge.Max is null)
        {
            return await CollectFingerprintDiffsAsync(
                sourceConnection, targetConnection, table, timeout, batchSize,
                sourcePredicate: null, targetPredicate: null, sourceParameters: [], includeDeletes: true, cancellationToken)
                .ConfigureAwait(false);
        }

        var origin = Math.Min(sourceEdge.Min.Value, targetEdge.Min.Value);
        var end = Math.Max(sourceEdge.Max.Value, targetEdge.Max.Value);
        var width = RangeHashPlanner.ChooseWidth(origin, end);
        var sourceBucketTask = ReadRangeBucketsAsync(sourceConnection, table, pk, origin, width, null, timeout, cancellationToken);
        var targetBucketTask = ReadRangeBucketsAsync(targetConnection, table, pk, origin, width, null, timeout, cancellationToken);
        await Task.WhenAll(sourceBucketTask, targetBucketTask).ConfigureAwait(false);
        var sourceBuckets = await sourceBucketTask.ConfigureAwait(false);
        var targetBuckets = await targetBucketTask.ConfigureAwait(false);
        long bytes = (sourceBuckets.Count + targetBuckets.Count) * 48L;
        var mismatched = RangeHashPlanner.MismatchedBuckets(sourceBuckets, targetBuckets)
            .Select(index => RangeHashPlanner.Bounds(origin, width, index, end))
            .ToList();

        var depth = 0;
        while (mismatched.Any(range => range.Span > RangeHashPlanner.LeafSpan) && depth < 6)
        {
            var wide = mismatched.Where(range => range.Span > RangeHashPlanner.LeafSpan).ToList();
            var narrow = mismatched.Where(range => range.Span <= RangeHashPlanner.LeafSpan).ToList();
            var widest = wide.Max(range => range.Span);
            var finer = Math.Max(1, widest / 8);
            if (finer >= widest)
            {
                break;
            }

            var sourceRefineTask = ReadRangeBucketsAsync(sourceConnection, table, pk, origin, finer, wide, timeout, cancellationToken);
            var targetRefineTask = ReadRangeBucketsAsync(targetConnection, table, pk, origin, finer, wide, timeout, cancellationToken);
            await Task.WhenAll(sourceRefineTask, targetRefineTask).ConfigureAwait(false);
            sourceBuckets = await sourceRefineTask.ConfigureAwait(false);
            targetBuckets = await targetRefineTask.ConfigureAwait(false);
            bytes += (sourceBuckets.Count + targetBuckets.Count) * 48L;
            var refined = RangeHashPlanner.MismatchedBuckets(sourceBuckets, targetBuckets)
                .Select(index => RangeHashPlanner.Bounds(origin, finer, index, end))
                .ToList();
            mismatched = narrow.Concat(refined).ToList();
            depth++;
        }

        if (mismatched.Count == 0)
        {
            _logger.LogInformation(
                "Range hash matched {Table}. Buckets transferred={Buckets}. No row payloads.",
                table.FullName,
                bytes / 48);
            return new CollectedDiff([], sourceEdge.Count, bytes);
        }

        var predicate = string.Join(" OR ", mismatched.Select(range => $"({pk} >= {range.Start} AND {pk} <= {range.EndInclusive})"));
        var hashSql = RowHashSqlBuilder.BuildCheapRowTokenExpression(table);
        var sourceRowTask = ReadProjectedHashesAsync(sourceConnection, table, pk, hashSql, predicate, timeout, cancellationToken);
        var targetRowTask = ReadProjectedHashesAsync(targetConnection, table, pk, hashSql, predicate, timeout, cancellationToken);
        await Task.WhenAll(sourceRowTask, targetRowTask).ConfigureAwait(false);
        var sourceRows = await sourceRowTask.ConfigureAwait(false);
        var targetRows = await targetRowTask.ConfigureAwait(false);
        bytes += (sourceRows.Count + targetRows.Count) * (8L + ColumnSizeEstimator.EstimateRowBytes(table.PrimaryKeyColumns));
        var rowDiffs = RowDiffEngine.Diff(
            sourceRows.Select(row => new RowDiffEngine.RowFingerprint(row.Key.ToString(CultureInfo.InvariantCulture), row.Hash)),
            targetRows.Select(row => new RowDiffEngine.RowFingerprint(row.Key.ToString(CultureInfo.InvariantCulture), row.Hash)));
        var keyByText = new Dictionary<string, long>(sourceRows.Count + targetRows.Count);
        foreach (var row in sourceRows)
        {
            keyByText[row.Key.ToString(CultureInfo.InvariantCulture)] = row.Key;
        }

        foreach (var row in targetRows)
        {
            keyByText[row.Key.ToString(CultureInfo.InvariantCulture)] = row.Key;
        }

        var diffs = rowDiffs.Select(diff => new DiffKey(
            diff.Kind switch
            {
                DiffKind.Insert => 'I',
                DiffKind.Update => 'U',
                _ => 'D'
            },
            [CoerceKey(keyByText[diff.Key], table.PrimaryKeyColumns[0])])).ToList();
        _logger.LogInformation(
            "Range hash {Table}. MismatchedRanges={Ranges} Fingerprints={Fingerprints} Diffs={Diffs} PayloadBytes={Bytes}",
            table.FullName,
            mismatched.Count,
            sourceRows.Count,
            diffs.Count,
            bytes);
        return new CollectedDiff(diffs, sourceEdge.Count, bytes);
    }

    private async Task<CollectedDiff> CollectFingerprintDiffsAsync(
        SqlConnection sourceConnection,
        SqlConnection targetConnection,
        TableSchema table,
        int timeout,
        int batchSize,
        string? sourcePredicate,
        string? targetPredicate,
        IReadOnlyList<(string Name, object Value)> sourceParameters,
        bool includeDeletes,
        CancellationToken cancellationToken)
    {
        var hashSql = RowHashSqlBuilder.BuildCanonicalRowHashExpression(table);
        var keyList = string.Join(", ", table.PrimaryKeyColumns.Select(column => Q(column.Name)));
        var strippedKeys = StrippedKeyProjection(table);
        await ExecuteAsync(targetConnection, $"""
            IF OBJECT_ID('tempdb..#SourceFp') IS NOT NULL DROP TABLE #SourceFp;
            SELECT TOP (0) {strippedKeys}, CONVERT(varbinary(32), 0x) AS RowHash
            INTO #SourceFp
            FROM {table.FullName};
            """, timeout, cancellationToken).ConfigureAwait(false);
        var fingerprintRows = await BulkCopyFingerprintsAsync(
            sourceConnection, targetConnection, table, $"CONVERT(varbinary(32), {hashSql})", sourcePredicate, sourceParameters, timeout, batchSize, cancellationToken)
            .ConfigureAwait(false);
        await ExecuteAsync(targetConnection, $"CREATE CLUSTERED INDEX IX_SourceFp ON #SourceFp ({keyList});", timeout, cancellationToken)
            .ConfigureAwait(false);

        string targetSql;
        if (!includeDeletes)
        {
            var qualifiedKeys = string.Join(", ", table.PrimaryKeyColumns.Select(column => $"t.{Q(column.Name)}"));
            var qualifiedHash = RowHashSqlBuilder.BuildCanonicalRowHashExpression(table, "t");
            targetSql = $"""
                IF OBJECT_ID('tempdb..#TargetFp') IS NOT NULL DROP TABLE #TargetFp;
                SELECT {qualifiedKeys}, CONVERT(varbinary(32), {qualifiedHash}) AS RowHash
                INTO #TargetFp
                FROM {table.FullName} AS t
                INNER JOIN #SourceFp AS s ON {Join("t", "s", table)};
                CREATE CLUSTERED INDEX IX_TargetFp ON #TargetFp ({keyList});
                """;
        }
        else
        {
            var where = string.IsNullOrWhiteSpace(targetPredicate) ? string.Empty : "WHERE " + targetPredicate;
            targetSql = $"""
                IF OBJECT_ID('tempdb..#TargetFp') IS NOT NULL DROP TABLE #TargetFp;
                SELECT {keyList}, CONVERT(varbinary(32), {hashSql}) AS RowHash
                INTO #TargetFp
                FROM {table.FullName}
                {where};
                CREATE CLUSTERED INDEX IX_TargetFp ON #TargetFp ({keyList});
                """;
        }

        await ExecuteAsync(targetConnection, targetSql, timeout, cancellationToken).ConfigureAwait(false);
        var diffs = await ReadDiffsAsync(targetConnection, table, includeDeletes, timeout, cancellationToken).ConfigureAwait(false);
        var pkBytes = ColumnSizeEstimator.EstimateRowBytes(table.PrimaryKeyColumns);
        var bytes = fingerprintRows * (32L + pkBytes);
        _logger.LogInformation(
            "Fingerprint compare {Table}. SourceFingerprints={Rows} IncludeDeletes={Deletes} PayloadBytes={Bytes}",
            table.FullName,
            fingerprintRows,
            includeDeletes,
            bytes);
        return new CollectedDiff(diffs, fingerprintRows, bytes);
    }

    private static async Task<(long Count, long? Min, long? Max)> ReadKeyEdgeAsync(
        SqlConnection connection,
        TableSchema table,
        string pk,
        int timeout,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT_BIG(1), MIN({pk}), MAX({pk}) FROM {table.FullName};";
        command.CommandTimeout = timeout;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        var count = reader.GetInt64(0);
        long? min = reader.IsDBNull(1) ? null : Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture);
        long? max = reader.IsDBNull(2) ? null : Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture);
        return (count, min, max);
    }

    private static async Task<Dictionary<long, (long Count, byte[] Hash)>> ReadRangeBucketsAsync(
        SqlConnection connection,
        TableSchema table,
        string pk,
        long origin,
        long width,
        IReadOnlyList<RangeHashPlanner.KeyRange>? restrict,
        int timeout,
        CancellationToken cancellationToken)
    {
        var filter = restrict is { Count: > 0 }
            ? "WHERE " + string.Join(" OR ", restrict.Select(range => $"({pk} >= {range.Start} AND {pk} <= {range.EndInclusive})"))
            : string.Empty;
        var checksumColumns = string.Join(", ", table.CopyableColumns.Select(column => Q(column.Name)));
        if (string.IsNullOrEmpty(checksumColumns))
        {
            checksumColumns = pk;
        }

        // One scan and two aggregates. SHA-256 of every row is reserved for buckets that actually differ.
        var sql = $"""
            SELECT ((CONVERT(bigint, {pk}) - @origin) / @width) AS Bucket,
                   COUNT_BIG(*) AS Cnt,
                   CHECKSUM_AGG(BINARY_CHECKSUM({checksumColumns})) AS Agg,
                   SUM(CAST(BINARY_CHECKSUM({checksumColumns}) AS bigint)) AS AggSum
            FROM {table.FullName}
            {filter}
            GROUP BY ((CONVERT(bigint, {pk}) - @origin) / @width);
            """;
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = timeout;
        var originParameter = command.CreateParameter();
        originParameter.ParameterName = "@origin";
        originParameter.Value = origin;
        command.Parameters.Add(originParameter);
        var widthParameter = command.CreateParameter();
        widthParameter.ParameterName = "@width";
        widthParameter.Value = width;
        command.Parameters.Add(widthParameter);
        var buckets = new Dictionary<long, (long Count, byte[] Hash)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var index = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture);
            var count = reader.GetInt64(1);
            var agg = reader.IsDBNull(2) ? 0 : Convert.ToInt32(reader.GetValue(2), CultureInfo.InvariantCulture);
            var sum = reader.IsDBNull(3) ? 0L : Convert.ToInt64(reader.GetValue(3), CultureInfo.InvariantCulture);
            var hash = new byte[12];
            BitConverter.GetBytes(agg).CopyTo(hash, 0);
            BitConverter.GetBytes(sum).CopyTo(hash, 4);
            buckets[index] = (count, hash);
        }

        return buckets;
    }

    private static async Task<List<(long Key, byte[] Hash)>> ReadProjectedHashesAsync(
        SqlConnection connection,
        TableSchema table,
        string pk,
        string hashSql,
        string predicate,
        int timeout,
        CancellationToken cancellationToken)
    {
        var rows = new List<(long Key, byte[] Hash)>();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT CONVERT(bigint, {pk}) AS Pk, CONVERT(varbinary(8), {hashSql}) AS RowHash
            FROM {table.FullName}
            WHERE {predicate};
            """;
        command.CommandTimeout = timeout;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var hash = reader.IsDBNull(1) ? [] : (byte[])reader.GetValue(1);
            rows.Add((reader.GetInt64(0), hash));
        }

        return rows;
    }

    private sealed record CollectedDiff(List<DiffKey> Diffs, long SourceRows, long Bytes);

    private static async Task<long> BulkCopyFingerprintsAsync(
        SqlConnection sourceConnection,
        SqlConnection targetConnection,
        TableSchema table,
        string hashSql,
        string? sourcePredicate,
        IReadOnlyList<(string Name, object Value)> sourceParameters,
        int timeout,
        int batchSize,
        CancellationToken cancellationToken)
    {
        var keyList = string.Join(", ", table.PrimaryKeyColumns.Select(column => Q(column.Name)));
        var where = string.IsNullOrWhiteSpace(sourcePredicate) ? string.Empty : "WHERE " + sourcePredicate;
        await using var command = sourceConnection.CreateCommand();
        command.CommandText = $"SELECT {keyList}, {hashSql} AS RowHash FROM {table.FullName} {where};";
        command.CommandTimeout = timeout;
        foreach (var (name, value) in sourceParameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            if (value is byte[] bytes)
            {
                parameter.SqlDbType = SqlDbType.VarBinary;
                parameter.Size = bytes.Length;
            }

            command.Parameters.Add(parameter);
        }
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        using var bulk = new SqlBulkCopy(targetConnection, SqlBulkCopyOptions.TableLock, null)
        {
            DestinationTableName = "#SourceFp",
            BulkCopyTimeout = timeout,
            BatchSize = batchSize,
            EnableStreaming = true
        };
        foreach (var column in table.PrimaryKeyColumns)
        {
            bulk.ColumnMappings.Add(column.Name, column.Name);
        }

        bulk.ColumnMappings.Add("RowHash", "RowHash");
        await bulk.WriteToServerAsync(reader, cancellationToken).ConfigureAwait(false);
        return bulk.RowsCopied;
    }

    private static async Task InsertJsonAsync(
        SqlConnection sourceConnection,
        SqlConnection targetConnection,
        string sql,
        string destination,
        IReadOnlyList<ColumnSchema> columns,
        int timeout,
        CancellationToken cancellationToken)
    {
        await using var command = sourceConnection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = timeout;
        var payload = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        if (string.IsNullOrEmpty(payload))
        {
            return;
        }

        var columnList = string.Join(", ", columns.Select(column => Q(column.Name)));
        var schema = string.Join(", ", columns.Select(column => $"{Q(column.Name)} {OpenJsonType(column)}"));
        var identity = columns.Any(column => column.IsIdentity);
        var insert = new StringBuilder();
        if (identity)
        {
            insert.Append("SET IDENTITY_INSERT ").Append(destination).AppendLine(" ON;");
        }

        insert.Append("INSERT INTO ").Append(destination).Append(" (").Append(columnList).AppendLine(")");
        insert.Append("SELECT ").AppendLine(columnList);
        insert.Append("FROM OPENJSON(@payload) WITH (").Append(schema).AppendLine(");");
        if (identity)
        {
            insert.Append("SET IDENTITY_INSERT ").Append(destination).AppendLine(" OFF;");
        }

        await targetConnection.ExecuteAsync(new CommandDefinition(
            insert.ToString(),
            new { payload },
            commandTimeout: timeout,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    private static string OpenJsonType(ColumnSchema column) => column.SqlTypeName.ToLowerInvariant() switch
    {
        "tinyint" => "tinyint",
        "smallint" => "smallint",
        "int" => "int",
        "bigint" => "bigint",
        "bit" => "bit",
        "decimal" or "numeric" or "money" or "smallmoney" => "decimal(38, 10)",
        "float" => "float",
        "real" => "real",
        "date" => "date",
        "time" => "time",
        "datetime" or "datetime2" or "smalldatetime" => "datetime2",
        "datetimeoffset" => "datetimeoffset",
        "uniqueidentifier" => "uniqueidentifier",
        "binary" or "varbinary" or "image" => "varbinary(max)",
        _ => "nvarchar(max)"
    };

    private static async Task<long> BulkCopyQueryAsync(
        SqlConnection sourceConnection,
        SqlConnection targetConnection,
        string sql,
        string destination,
        IReadOnlyList<ColumnSchema> columns,
        int timeout,
        int batchSize,
        SqlTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = sourceConnection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = timeout;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        using var bulk = new SqlBulkCopy(targetConnection, SqlBulkCopyOptions.KeepIdentity, transaction)
        {
            DestinationTableName = destination,
            BulkCopyTimeout = timeout,
            BatchSize = Math.Max(1, batchSize),
            EnableStreaming = true
        };
        foreach (var column in columns)
        {
            bulk.ColumnMappings.Add(column.Name, column.Name);
        }

        await bulk.WriteToServerAsync(reader, cancellationToken).ConfigureAwait(false);
        return bulk.RowsCopied;
    }

    private static async Task<List<DiffKey>> ReadDiffsAsync(
        SqlConnection targetConnection,
        TableSchema table,
        bool includeDeletes,
        int timeout,
        CancellationToken cancellationToken)
    {
        var rows = new List<DiffKey>();
        await using var command = targetConnection.CreateCommand();
        command.CommandText = BuildDiffSql(table, includeDeletes);
        command.CommandTimeout = timeout;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var width = table.PrimaryKeyColumns.Count;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var values = new object?[width];
            for (var i = 0; i < width; i++)
            {
                values[i] = reader.IsDBNull(i + 1) ? null : reader.GetValue(i + 1);
            }

            rows.Add(new DiffKey(ParseOp(reader.GetValue(0)), values));
        }

        return rows;
    }

    private static async Task<Dictionary<RowKey, DateTimeOffset?>> ReadStampsAsync(
        SqlConnection connection,
        TableSchema table,
        ColumnSchema changeColumn,
        string opFilter,
        int timeout,
        CancellationToken cancellationToken)
    {
        var keys = table.PrimaryKeyColumns;
        var projection = string.Join(", ", keys.Select(column => $"k.{Q(column.Name)}"));
        var sql = $"""
            SELECT {projection}, src.{Q(changeColumn.Name)} AS __ChangeStamp
            FROM {table.FullName} AS src
            INNER JOIN #BatchKeys AS k ON {Join("src", "k", table)}
            WHERE {opFilter};
            """;

        var map = new Dictionary<RowKey, DateTimeOffset?>();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = timeout;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var values = new object?[keys.Count];
            for (var i = 0; i < keys.Count; i++)
            {
                values[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }

            var stamp = reader.IsDBNull(keys.Count) ? null : TimestampValue.Read(reader.GetValue(keys.Count));
            map[new RowKey(values)] = stamp;
        }

        return map;
    }

    private static async Task<Dictionary<RowKey, Dictionary<string, object?>>> ReadRowsAsync(
        SqlConnection connection,
        TableSchema table,
        IReadOnlyList<ColumnSchema> columns,
        string keyTable,
        string opFilter,
        int timeout,
        CancellationToken cancellationToken)
    {
        var projection = string.Join(", ", columns.Select(column =>
            $"{(column.IsPrimaryKey ? "k" : "src")}.{Q(column.Name)}"));
        var sql = $"""
            SELECT {projection}
            FROM {table.FullName} AS src
            INNER JOIN {keyTable} AS k ON {Join("src", "k", table)}
            WHERE {opFilter};
            """;

        var map = new Dictionary<RowKey, Dictionary<string, object?>>();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = timeout;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var keyOrdinals = table.PrimaryKeyColumns.Select(column => reader.GetOrdinal(column.Name)).ToArray();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var values = new object?[keyOrdinals.Length];
            for (var i = 0; i < keyOrdinals.Length; i++)
            {
                values[i] = reader.IsDBNull(keyOrdinals[i]) ? null : reader.GetValue(keyOrdinals[i]);
            }

            var row = new Dictionary<string, object?>(reader.FieldCount, StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++)
            {
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }

            map[new RowKey(values)] = row;
        }

        return map;
    }

    private static async Task CreateBatchKeysAsync(
        SqlConnection connection,
        TableSchema table,
        int timeout,
        CancellationToken cancellationToken,
        string tableName = "#BatchKeys")
    {
        var strippedKeys = StrippedKeyProjection(table);
        await ExecuteAsync(connection, $"""
            IF OBJECT_ID('tempdb..{tableName}') IS NOT NULL DROP TABLE {tableName};
            SELECT TOP (0) CAST(N'I' AS char(1)) AS Op, {strippedKeys}
            INTO {tableName}
            FROM {table.FullName};
            """, timeout, cancellationToken).ConfigureAwait(false);
    }

    private static async Task LoadBatchKeysAsync(
        SqlConnection connection,
        TableSchema table,
        IReadOnlyList<DiffKey> rows,
        int timeout,
        CancellationToken cancellationToken,
        string tableName = "#BatchKeys")
    {
        await CreateBatchKeysAsync(connection, table, timeout, cancellationToken, tableName).ConfigureAwait(false);
        await InsertBatchKeysAsync(connection, table, rows, timeout, cancellationToken, tableName).ConfigureAwait(false);
    }

    private static async Task InsertBatchKeysAsync(
        SqlConnection connection,
        TableSchema table,
        IReadOnlyList<DiffKey> rows,
        int timeout,
        CancellationToken cancellationToken,
        string tableName = "#BatchKeys")
    {
        if (rows.Count == 0)
        {
            return;
        }

        var keys = table.PrimaryKeyColumns;
        var payload = JsonSerializer.Serialize(rows.Select(row =>
        {
            var item = new Dictionary<string, object?> { ["Op"] = row.Op.ToString() };
            for (var column = 0; column < keys.Count; column++)
            {
                var value = row.Values[column];
                item[keys[column].Name] = value is null or DBNull ? null : value;
            }

            return item;
        }));
        var columnList = "Op, " + string.Join(", ", keys.Select(key => Q(key.Name)));
        var schema = "Op char(1), " + string.Join(", ", keys.Select(key => $"{Q(key.Name)} {OpenJsonType(key)}"));
        await connection.ExecuteAsync(new CommandDefinition(
            $"""
            INSERT INTO {tableName} ({columnList})
            SELECT {columnList}
            FROM OPENJSON(@payload) WITH ({schema});
            """,
            new { payload },
            commandTimeout: timeout,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    private static object CoerceKey(object? value, ColumnSchema column)
    {
        if (value is null or DBNull)
        {
            return DBNull.Value;
        }

        return column.SqlTypeName.ToLowerInvariant() switch
        {
            "tinyint" => Convert.ToByte(value, CultureInfo.InvariantCulture),
            "smallint" => Convert.ToInt16(value, CultureInfo.InvariantCulture),
            "int" => Convert.ToInt32(value, CultureInfo.InvariantCulture),
            "bigint" => Convert.ToInt64(value, CultureInfo.InvariantCulture),
            "bit" => Convert.ToBoolean(value, CultureInfo.InvariantCulture),
            "float" => Convert.ToDouble(value, CultureInfo.InvariantCulture),
            "real" => Convert.ToSingle(value, CultureInfo.InvariantCulture),
            "decimal" or "numeric" or "money" or "smallmoney" => Convert.ToDecimal(value, CultureInfo.InvariantCulture),
            _ => value
        };
    }

    private static string BuildDiffSql(TableSchema table, bool includeDeletes)
    {
        var keys = table.PrimaryKeyColumns;
        var join = Join("s", "t", table);
        var sourceKeys = string.Join(", ", keys.Select(key => $"s.{Q(key.Name)}"));
        var targetKeys = string.Join(", ", keys.Select(key => $"t.{Q(key.Name)}"));
        var first = Q(keys[0].Name);
        var sql = $"""
            SELECT CAST('I' AS char(1)) AS Op, {sourceKeys}
            FROM #SourceFp AS s
            LEFT JOIN #TargetFp AS t ON {join}
            WHERE t.{first} IS NULL
            UNION ALL
            SELECT CAST('U' AS char(1)), {sourceKeys}
            FROM #SourceFp AS s
            INNER JOIN #TargetFp AS t ON {join}
            WHERE s.RowHash <> t.RowHash
            """;
        if (!includeDeletes)
        {
            return sql;
        }

        return sql + $"""

            UNION ALL
            SELECT CAST('D' AS char(1)), {targetKeys}
            FROM #TargetFp AS t
            LEFT JOIN #SourceFp AS s ON {join}
            WHERE s.{first} IS NULL
            """;
    }

    private static string BuildMergeSql(TableSchema table, IReadOnlyList<ColumnSchema> copyable)
    {
        var onClause = Join("target", "source", table);
        var updateColumns = copyable.Where(column => !column.IsPrimaryKey).ToList();
        var sql = new StringBuilder();
        sql.Append("MERGE ").Append(table.FullName).AppendLine(" AS target");
        sql.AppendLine("USING #SyncStaging AS source");
        sql.Append("ON ").AppendLine(onClause);
        if (updateColumns.Count > 0)
        {
            var setClause = string.Join(", ", updateColumns.Select(column => $"target.{Q(column.Name)} = source.{Q(column.Name)}"));
            sql.AppendLine("WHEN MATCHED THEN");
            sql.Append("  UPDATE SET ").AppendLine(setClause);
        }

        var insertCols = string.Join(", ", copyable.Select(column => Q(column.Name)));
        var insertVals = string.Join(", ", copyable.Select(column => $"source.{Q(column.Name)}"));
        sql.AppendLine("WHEN NOT MATCHED BY TARGET THEN");
        sql.Append("  INSERT (").Append(insertCols).Append(") VALUES (").Append(insertVals).AppendLine(");");
        return sql.ToString();
    }

    private static string Join(string left, string right, TableSchema table) =>
        string.Join(" AND ", table.PrimaryKeyColumns.Select(column => $"{left}.{Q(column.Name)} = {right}.{Q(column.Name)}"));

    private static string ColumnList(IReadOnlyList<ColumnSchema> columns, string? alias = null)
    {
        var prefix = string.IsNullOrEmpty(alias) ? string.Empty : alias + ".";
        return string.Join(", ", columns.Select(column => prefix + Q(column.Name)));
    }

    private static string Q(string identifier) => SqlQuoting.Quote(identifier);

    /// <summary>
    /// Identity columns keep the IDENTITY property through SELECT INTO. Temp tables that we fill
    /// with explicit key values must cast those columns so the property is dropped.
    /// </summary>
    private static string StrippedKeyProjection(TableSchema table) =>
        string.Join(", ", table.PrimaryKeyColumns.Select(column =>
        {
            var quoted = Q(column.Name);
            return column.IsIdentity
                ? $"CAST({quoted} AS {CastSqlType(column)}) AS {quoted}"
                : quoted;
        }));

    private static string CastSqlType(ColumnSchema column) => column.SqlTypeName.ToLowerInvariant() switch
    {
        "nvarchar" or "nchar" or "sysname" => "nvarchar(4000)",
        "varchar" or "char" => "varchar(8000)",
        "varbinary" or "binary" => "varbinary(8000)",
        "decimal" or "numeric" => "decimal(38, 10)",
        _ => column.SqlTypeName
    };

    private static char ParseOp(object value)
    {
        var text = Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim();
        return text switch
        {
            "I" or "U" or "D" => text[0],
            _ => throw new InvalidOperationException($"Unexpected differential operation '{text}'.")
        };
    }

    private static async Task<long> ScalarLongAsync(
        SqlConnection connection,
        string sql,
        int timeout,
        CancellationToken cancellationToken) =>
        await connection.ExecuteScalarAsync<long>(new CommandDefinition(sql, commandTimeout: timeout, cancellationToken: cancellationToken))
            .ConfigureAwait(false);

    private static async Task ExecuteAsync(
        SqlConnection connection,
        string sql,
        int timeout,
        CancellationToken cancellationToken,
        SqlTransaction? transaction = null) =>
        await connection.ExecuteAsync(new CommandDefinition(
            sql,
            transaction: transaction,
            commandTimeout: timeout,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

    private static async Task DeleteChunkedAsync(
        SqlConnection connection,
        TableSchema table,
        int timeout,
        CancellationToken cancellationToken)
    {
        int removed;
        do
        {
            removed = await connection.ExecuteAsync(new CommandDefinition(
                $"DELETE TOP ({ApplyChunkRows}) FROM {table.FullName};",
                commandTimeout: timeout,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
        while (removed == ApplyChunkRows);
    }

    private static bool TryBuildWatermarkFilter(
        TableSchema table,
        ColumnSchema? changeColumn,
        ColumnSchema? rowVersion,
        WatermarkState watermark,
        out string? predicate,
        out List<(string Name, object Value)> parameters)
    {
        _ = table;
        var filters = new List<string>();
        parameters = [];
        if (changeColumn is not null && watermark.UpdatedAt is not null)
        {
            filters.Add($"{Q(changeColumn.Name)} > @wmUpdatedAt");
            parameters.Add(("@wmUpdatedAt", watermark.UpdatedAt));
        }

        if (rowVersion is not null && watermark.RowVersion is { Length: > 0 })
        {
            filters.Add($"{Q(rowVersion.Name)} > @wmRowVer");
            parameters.Add(("@wmRowVer", watermark.RowVersion));
        }

        if (filters.Count == 0)
        {
            predicate = null;
            return false;
        }

        predicate = string.Join(" OR ", filters);
        return true;
    }

    private async Task RememberWatermarkAsync(
        SqlConnection sourceConnection,
        string sourceConnectionId,
        string targetConnectionId,
        TableSchema table,
        ColumnSchema? changeColumn,
        ColumnSchema? rowVersion,
        SyncTableRequest request,
        bool success,
        bool fullHash,
        int timeout,
        CancellationToken cancellationToken)
    {
        if (!success || request.Trigger != SyncTriggerKind.Interval)
        {
            return;
        }

        await using var command = sourceConnection.CreateCommand();
        var updatedSql = changeColumn is null ? "CAST(NULL AS datetime2)" : $"MAX({Q(changeColumn.Name)})";
        var versionSql = rowVersion is null ? "CAST(NULL AS varbinary(8))" : $"MAX({Q(rowVersion.Name)})";
        command.CommandText = $"SELECT {updatedSql}, {versionSql} FROM {table.FullName};";
        command.CommandTimeout = timeout;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        object? updatedAt = null;
        byte[]? version = null;
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            updatedAt = reader.IsDBNull(0) ? null : reader.GetValue(0);
            version = reader.IsDBNull(1) ? null : (byte[])reader.GetValue(1);
        }

        var key = WatermarkKey(sourceConnectionId, targetConnectionId, table);
        lock (_watermarkGate)
        {
            if (!_watermarks.TryGetValue(key, out var state))
            {
                state = new WatermarkState();
                _watermarks[key] = state;
            }

            if (updatedAt is not null)
            {
                state.UpdatedAt = updatedAt;
            }

            if (version is { Length: > 0 })
            {
                state.RowVersion = version;
            }

            state.CyclesSinceFullHash = fullHash ? 0 : state.CyclesSinceFullHash + 1;
        }
    }

    private static string WatermarkKey(string sourceConnectionId, string targetConnectionId, TableSchema table) =>
        sourceConnectionId + "|" + targetConnectionId + "|" + table.FullName;

    private sealed class WatermarkState
    {
        public object? UpdatedAt { get; set; }

        public byte[]? RowVersion { get; set; }

        public int CyclesSinceFullHash { get; set; }
    }

    private void LogSummary(
        TableSchema table,
        SyncConnection target,
        SyncTableRequest request,
        bool dryRun,
        ColumnSchema? changeColumn,
        long sourceRows,
        long inserted,
        long updated,
        long deleted,
        long conflicted,
        long unchanged,
        long transferred,
        long bytes,
        string? backup,
        Stopwatch stopwatch)
    {
        _logger.LogInformation(
            "Differential sync {Table} => {Target}. Trigger={Trigger} DryRun={DryRun} Inserted={Inserted} Updated={Updated} Deleted={Deleted} Conflicted={Conflicted} Unchanged={Unchanged} RowsTransferred={RowsTransferred} EstimatedBytes={EstimatedBytes} SourceFingerprints={SourceFingerprints} ChangeColumn={ChangeColumn} Backup={Backup} ElapsedMs={ElapsedMs}",
            table.FullName,
            target.Name,
            request.Trigger,
            dryRun,
            inserted,
            updated,
            deleted,
            conflicted,
            unchanged,
            transferred,
            bytes,
            sourceRows,
            changeColumn?.Name ?? "(none)",
            backup ?? "(none)",
            stopwatch.ElapsedMilliseconds);
    }

    private static TableSyncResult Result(
        TableSchema table,
        SyncConnection target,
        Stopwatch stopwatch,
        bool dryRun,
        bool success,
        bool skipped,
        long rowsRead = 0,
        long rowsAffected = 0,
        long inserted = 0,
        long updated = 0,
        long deleted = 0,
        long conflicted = 0,
        long unchanged = 0,
        long transferred = 0,
        long bytes = 0,
        string? backup = null,
        string? changeColumn = null,
        string? error = null) =>
        new()
        {
            SchemaName = table.SchemaName,
            TableName = table.TableName,
            TargetConnectionId = target.Id,
            TargetConnectionName = target.Name,
            Success = success,
            Skipped = skipped,
            DryRun = dryRun,
            RowsReadFromSource = rowsRead,
            RowsAffectedOnTarget = rowsAffected,
            RowsInserted = inserted,
            RowsUpdated = updated,
            RowsDeleted = deleted,
            RowsConflicted = conflicted,
            RowsUnchanged = unchanged,
            RowsTransferred = transferred,
            EstimatedBytesTransferred = bytes,
            BackupLocation = backup,
            ChangeTimestampColumn = changeColumn,
            Elapsed = stopwatch.Elapsed,
            ErrorMessage = error
        };

    private readonly record struct DiffKey(char Op, object?[] Values)
    {
        public DiffKind Kind => Op switch
        {
            'I' => DiffKind.Insert,
            'U' => DiffKind.Update,
            'D' => DiffKind.Delete,
            _ => throw new InvalidOperationException($"Unexpected differential operation '{Op}'.")
        };

        public RowKey ToKey() => new(Values);
    }

    private sealed class ConflictLogTableWriter : IConflictLogTable
    {
        private readonly SqlConnection _connection;
        private readonly TableSchema _table;
        private readonly string _targetName;
        private readonly Guid _cycleId;
        private readonly int _timeout;
        private bool _ensured;

        public ConflictLogTableWriter(SqlConnection connection, TableSchema table, string targetName, Guid cycleId, int timeout)
        {
            _connection = connection;
            _table = table;
            _targetName = targetName;
            _cycleId = cycleId;
            _timeout = timeout;
        }

        public async Task InsertAsync(IReadOnlyList<ConflictRow> rows, string backupLocation, CancellationToken cancellationToken)
        {
            if (!_ensured)
            {
                await ExecuteAsync(_connection, """
                    IF SCHEMA_ID(N'sync') IS NULL EXEC(N'CREATE SCHEMA sync');
                    IF OBJECT_ID(N'sync.ConflictLog', N'U') IS NULL
                    CREATE TABLE sync.ConflictLog
                    (
                        ConflictId bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_sync_ConflictLog PRIMARY KEY,
                        CycleId uniqueidentifier NOT NULL,
                        LoggedAtUtc datetime2 NOT NULL,
                        SchemaName sysname NOT NULL,
                        TableName sysname NOT NULL,
                        TargetName nvarchar(256) NOT NULL,
                        Operation varchar(16) NOT NULL,
                        KeyJson nvarchar(max) NOT NULL,
                        TargetValuesJson nvarchar(max) NULL,
                        MainValuesJson nvarchar(max) NULL,
                        MainChangeTimestamp datetimeoffset NULL,
                        TargetChangeTimestamp datetimeoffset NULL,
                        BackupLocation nvarchar(1024) NULL,
                        ReviewStatus varchar(20) NOT NULL CONSTRAINT DF_sync_ConflictLog_Review DEFAULT ('Pending'),
                        DryRun bit NOT NULL CONSTRAINT DF_sync_ConflictLog_DryRun DEFAULT (0)
                    );
                    """, _timeout, cancellationToken).ConfigureAwait(false);
                _ensured = true;
            }

            foreach (var row in rows)
            {
                var key = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < row.KeyNames.Length; i++)
                {
                    key[row.KeyNames[i]] = ConflictBackupSession.Normalize(row.KeyValues[i]);
                }

                await _connection.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT INTO sync.ConflictLog
                    (
                        CycleId, LoggedAtUtc, SchemaName, TableName, TargetName, Operation,
                        KeyJson, TargetValuesJson, MainValuesJson, MainChangeTimestamp, TargetChangeTimestamp,
                        BackupLocation, ReviewStatus, DryRun
                    )
                    VALUES
                    (
                        @CycleId, SYSUTCDATETIME(), @SchemaName, @TableName, @TargetName, @Operation,
                        @KeyJson, @TargetValuesJson, @MainValuesJson, @MainChangeTimestamp, @TargetChangeTimestamp,
                        @BackupLocation, N'Pending', 0
                    );
                    """,
                    new
                    {
                        CycleId = _cycleId,
                        SchemaName = _table.SchemaName,
                        TableName = _table.TableName,
                        TargetName = _targetName,
                        row.Operation,
                        KeyJson = Json(key),
                        TargetValuesJson = Json(row.TargetValues),
                        MainValuesJson = Json(row.MainValues),
                        row.MainChangeTimestamp,
                        row.TargetChangeTimestamp,
                        BackupLocation = backupLocation
                    },
                    commandTimeout: _timeout,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);
            }
        }

        private static string? Json(object? value) =>
            value is null ? null : System.Text.Json.JsonSerializer.Serialize(value);
    }

    private sealed class BackupTableWriter : IConflictTableWriter
    {
        private readonly SqlConnection _connection;
        private readonly TableSchema _table;
        private readonly IReadOnlyList<ColumnSchema> _columns;
        private readonly int _timeout;
        private readonly Guid _cycleId;
        private string? _qualified;

        public BackupTableWriter(
            SqlConnection connection,
            TableSchema table,
            IReadOnlyList<ColumnSchema> columns,
            int timeout,
            Guid cycleId)
        {
            _connection = connection;
            _table = table;
            _columns = columns;
            _timeout = timeout;
            _cycleId = cycleId;
        }

        public async Task<string> CreateAsync(CancellationToken cancellationToken)
        {
            await ExecuteAsync(_connection, "IF SCHEMA_ID(N'sync') IS NULL EXEC(N'CREATE SCHEMA sync');", _timeout, cancellationToken)
                .ConfigureAwait(false);

            var leaf = $"ConflictBackup_{_table.SchemaName}_{_table.TableName}_{DateTime.UtcNow:yyyyMMddHHmmss}_{Guid.NewGuid():N}";
            var safe = new string(leaf.Select(ch => char.IsLetterOrDigit(ch) || ch == '_' ? ch : '_').ToArray());
            if (safe.Length > 110)
            {
                safe = safe[..110];
            }

            _qualified = $"sync.{Q(safe)}";
            var columnList = string.Join(", ", _columns.Select(column => Q(column.Name)));
            await ExecuteAsync(_connection, $"""
                SELECT TOP (0) {columnList}
                INTO {_qualified}
                FROM {_table.FullName};
                ALTER TABLE {_qualified} ADD
                    BackupOperation varchar(16) NULL,
                    BackupCycleId uniqueidentifier NULL,
                    BackedUpAtUtc datetime2 NULL;
                """, _timeout, cancellationToken).ConfigureAwait(false);
            return _qualified;
        }

        public async Task WriteAsync(IReadOnlyList<ConflictRow> rows, CancellationToken cancellationToken)
        {
            if (_qualified is null)
            {
                throw new InvalidOperationException("Conflict backup table has not been created.");
            }

            var data = new DataTable();
            foreach (var column in _columns)
            {
                data.Columns.Add(column.Name, Nullable.GetUnderlyingType(ClrType(column.SqlTypeName)) ?? ClrType(column.SqlTypeName));
            }

            data.Columns.Add("BackupOperation", typeof(string));
            data.Columns.Add("BackupCycleId", typeof(Guid));
            data.Columns.Add("BackedUpAtUtc", typeof(DateTime));

            foreach (var row in rows)
            {
                var values = new object[data.Columns.Count];
                for (var i = 0; i < _columns.Count; i++)
                {
                object? raw = null;
                row.TargetValues?.TryGetValue(_columns[i].Name, out raw);
                    values[i] = Cell(raw, data.Columns[i].DataType);
                }

                values[^3] = row.Operation;
                values[^2] = _cycleId;
                values[^1] = DateTime.UtcNow;
                data.Rows.Add(values);
            }

            using var bulk = new SqlBulkCopy(_connection, SqlBulkCopyOptions.KeepIdentity, null)
            {
                DestinationTableName = _qualified,
                BulkCopyTimeout = _timeout
            };
            foreach (DataColumn column in data.Columns)
            {
                bulk.ColumnMappings.Add(column.ColumnName, column.ColumnName);
            }

            await bulk.WriteToServerAsync(data, cancellationToken).ConfigureAwait(false);
        }

        private static Type ClrType(string sqlType) => sqlType.ToLowerInvariant() switch
        {
            "bit" => typeof(bool),
            "tinyint" => typeof(byte),
            "smallint" => typeof(short),
            "int" => typeof(int),
            "bigint" => typeof(long),
            "real" => typeof(float),
            "float" => typeof(double),
            "decimal" or "numeric" or "money" or "smallmoney" => typeof(decimal),
            "uniqueidentifier" => typeof(Guid),
            "datetimeoffset" => typeof(DateTimeOffset),
            "time" => typeof(TimeSpan),
            "binary" or "varbinary" or "image" or "rowversion" or "timestamp" => typeof(byte[]),
            "date" or "datetime" or "datetime2" or "smalldatetime" => typeof(DateTime),
            _ => typeof(string)
        };

        private static object Cell(object? value, Type clrType)
        {
            if (value is null or DBNull)
            {
                return DBNull.Value;
            }

            if (clrType == typeof(string))
            {
                return value as string ?? Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
            }

            if (value.GetType() == clrType)
            {
                return value;
            }

            if (clrType == typeof(DateTime) && value is DateTimeOffset offset)
            {
                return offset.DateTime;
            }

            if (clrType == typeof(byte[]) && value is byte[] bytes)
            {
                return bytes;
            }

            return value;
        }
    }
}
