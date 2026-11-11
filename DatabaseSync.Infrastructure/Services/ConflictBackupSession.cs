using System.Globalization;
using System.Text.Json;
using DatabaseSync.Core.Diff;
using DatabaseSync.Core.Enums;
using Microsoft.Extensions.Logging;

namespace DatabaseSync.Infrastructure.Services;

internal sealed class ConflictRow
{
    public required string Operation { get; init; }

    public required string[] KeyNames { get; init; }

    public required object?[] KeyValues { get; init; }

    public Dictionary<string, object?>? TargetValues { get; init; }

    public Dictionary<string, object?>? MainValues { get; init; }

    public DateTimeOffset? MainChangeTimestamp { get; init; }

    public DateTimeOffset? TargetChangeTimestamp { get; init; }

    public long EstimateBytes()
    {
        return 64
               + Estimate(KeyNames, KeyValues)
               + Estimate(TargetValues)
               + Estimate(MainValues);
    }

    private static long Estimate(Dictionary<string, object?>? values)
    {
        if (values is null)
        {
            return 0;
        }

        long total = 2;
        foreach (var (name, value) in values)
        {
            total += name.Length + EstimateValue(value);
        }

        return total;
    }

    private static long Estimate(string[] names, object?[] values)
    {
        long total = 2;
        for (var i = 0; i < names.Length; i++)
        {
            total += names[i].Length + EstimateValue(values[i]);
        }

        return total;
    }

    private static long EstimateValue(object? value) => value switch
    {
        null or DBNull => 4,
        string text => text.Length + 2,
        byte[] bytes => bytes.Length,
        _ => 24
    };
}

internal interface IConflictTableWriter
{
    Task<string> CreateAsync(CancellationToken cancellationToken);

    Task WriteAsync(IReadOnlyList<ConflictRow> rows, CancellationToken cancellationToken);
}

internal interface IConflictLogTable
{
    Task InsertAsync(IReadOnlyList<ConflictRow> rows, string backupLocation, CancellationToken cancellationToken);
}

/// <summary>
/// Buffers a small conflict set into one JSON review file. Once the configured row or byte
/// ceiling is crossed, spills to a JSONL bulk file or an optional backup table.
/// Every conflict is also appended to conflict-log.jsonl for later review.
/// </summary>
internal sealed class ConflictBackupSession : IAsyncDisposable
{
    private static readonly byte[] NewLine = "\n"u8.ToArray();

    private static readonly JsonSerializerOptions ReviewOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly JsonSerializerOptions LineOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly ConflictBackupSettings _settings;
    private readonly ILogger _logger;
    private readonly string _folder;
    private readonly string _tableLabel;
    private readonly string _targetName;
    private readonly Guid _cycleId;
    private readonly string _trigger;
    private readonly bool _dryRun;
    private readonly IConflictTableWriter? _tableWriter;
    private readonly IConflictLogTable? _logTable;
    private readonly List<ConflictRow> _buffer = [];
    private readonly List<string> _locations = [];

    private ConflictBackupForm? _form;
    private long _bufferedBytes;
    private string? _bulkPath;
    private string? _tableLocation;
    private bool _tableFailed;
    private bool _logTableFailed;
    private bool _completed;
    private int _logged;

    private ConflictBackupSession(
        ConflictBackupSettings settings,
        ILogger logger,
        string folder,
        string tableLabel,
        string targetName,
        Guid cycleId,
        string trigger,
        bool dryRun,
        IConflictTableWriter? tableWriter,
        IConflictLogTable? logTable)
    {
        _settings = settings;
        _logger = logger;
        _folder = folder;
        _tableLabel = tableLabel;
        _targetName = targetName;
        _cycleId = cycleId;
        _trigger = trigger;
        _dryRun = dryRun;
        _tableWriter = tableWriter;
        _logTable = logTable;
    }

    public int Count { get; private set; }

    public string? Location => _locations.Count == 0 ? null : string.Join("; ", _locations);

    public static ConflictBackupSession Create(
        ConflictBackupSettings settings,
        ILogger logger,
        string folder,
        string tableLabel,
        string targetName,
        Guid cycleId,
        string trigger,
        bool dryRun,
        IConflictTableWriter? tableWriter,
        IConflictLogTable? logTable = null) =>
        new(settings, logger, folder, tableLabel, targetName, cycleId, trigger, dryRun, tableWriter, logTable);

    public async Task AddAsync(ConflictRow row, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_completed, this);
        var size = row.EstimateBytes();
        if (_form is null)
        {
            var projected = ConflictBackupPlanner.Choose(_buffer.Count + 1, _bufferedBytes + size, _settings);
            if (projected == ConflictBackupForm.JsonReviewFile)
            {
                _buffer.Add(row);
                _bufferedBytes += size;
                Count++;
                return;
            }

            _form = projected;
            var pending = _buffer.Skip(_logged).ToList();
            await WriteExternalAsync(_buffer, writeLog: false, cancellationToken).ConfigureAwait(false);
            if (pending.Count > 0)
            {
                await AppendLogAsync(pending, Location ?? _tableLocation ?? _bulkPath ?? "conflict-backup", cancellationToken)
                    .ConfigureAwait(false);
            }

            _buffer.Clear();
            _bufferedBytes = 0;
            _logged = 0;
        }

        await WriteExternalAsync([row], writeLog: true, cancellationToken).ConfigureAwait(false);
        Count++;
    }

    /// <summary>
    /// Writes any buffered conflicts to the review file before target rows are changed.
    /// </summary>
    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        if (_completed || _form is not null || _buffer.Count == _logged)
        {
            return;
        }

        var path = ReviewPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? _folder);
        var document = new
        {
            cycleId = _cycleId,
            table = _tableLabel,
            target = _targetName,
            trigger = _trigger,
            dryRun = _dryRun,
            generatedAtUtc = DateTimeOffset.UtcNow,
            conflicts = _buffer.Select(ToDto).ToList()
        };
        await File.WriteAllBytesAsync(path, JsonSerializer.SerializeToUtf8Bytes(document, ReviewOptions), cancellationToken)
            .ConfigureAwait(false);
        Remember(path);
        var pending = _buffer.Skip(_logged).ToList();
        await AppendLogAsync(pending, path, cancellationToken).ConfigureAwait(false);
        _logged = _buffer.Count;
    }

    public async Task CompleteAsync(CancellationToken cancellationToken)
    {
        if (_completed)
        {
            return;
        }

        if (_form is null)
        {
            await FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        _completed = true;
    }

    public ValueTask DisposeAsync()
    {
        _completed = true;
        return ValueTask.CompletedTask;
    }

    private async Task WriteExternalAsync(IReadOnlyList<ConflictRow> rows, bool writeLog, CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return;
        }

        if (_form == ConflictBackupForm.BackupTable && _tableWriter is not null && !_tableFailed)
        {
            try
            {
                if (_tableLocation is null)
                {
                    _tableLocation = await _tableWriter.CreateAsync(cancellationToken).ConfigureAwait(false);
                    Remember(_tableLocation);
                }

                await _tableWriter.WriteAsync(rows, cancellationToken).ConfigureAwait(false);
                if (writeLog)
                {
                    await AppendLogAsync(rows, _tableLocation, cancellationToken).ConfigureAwait(false);
                }

                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Could not write conflict backup table for {Table}. Using a JSONL export instead.", _tableLabel);
                _tableFailed = true;
                _form = ConflictBackupForm.BulkExportFile;
            }
        }

        var path = await EnsureBulkFileAsync(cancellationToken).ConfigureAwait(false);
        await AppendBulkAsync(rows, path, cancellationToken).ConfigureAwait(false);
        if (writeLog)
        {
            await AppendLogAsync(rows, path, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<string> EnsureBulkFileAsync(CancellationToken cancellationToken)
    {
        if (_bulkPath is not null)
        {
            return _bulkPath;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(BulkPath()) ?? _folder);
        _bulkPath = BulkPath();
        Remember(_bulkPath);
        var header = new
        {
            cycleId = _cycleId,
            table = _tableLabel,
            target = _targetName,
            trigger = _trigger,
            dryRun = _dryRun,
            format = "jsonl",
            generatedAtUtc = DateTimeOffset.UtcNow
        };
        await File.WriteAllBytesAsync(
                _bulkPath,
                JsonSerializer.SerializeToUtf8Bytes(header, LineOptions).Concat(NewLine).ToArray(),
                cancellationToken)
            .ConfigureAwait(false);
        return _bulkPath;
    }

    private async Task AppendBulkAsync(IReadOnlyList<ConflictRow> rows, string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        foreach (var row in rows)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(ToDto(row), LineOptions);
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(NewLine, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task AppendLogAsync(IReadOnlyList<ConflictRow> rows, string backupLocation, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_folder);
        var path = Path.Combine(_folder, "conflict-log.jsonl");
        await using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        foreach (var row in rows)
        {
            var line = ToDto(row);
            line["backupLocation"] = backupLocation;
            line["reviewStatus"] = "Pending";
            var bytes = JsonSerializer.SerializeToUtf8Bytes(line, LineOptions);
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(NewLine, cancellationToken).ConfigureAwait(false);
        }

        if (_logTable is not null && !_logTableFailed)
        {
            try
            {
                await _logTable.InsertAsync(rows, backupLocation, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logTableFailed = true;
                _logger.LogWarning(ex, "Could not write sync.ConflictLog for {Table}. The JSONL conflict log was still written.", _tableLabel);
            }
        }
    }

    private Dictionary<string, object?> ToDto(ConflictRow row)
    {
        var key = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < row.KeyNames.Length; i++)
        {
            key[row.KeyNames[i]] = Normalize(row.KeyValues[i]);
        }

        return new Dictionary<string, object?>
        {
            ["cycleId"] = _cycleId,
            ["loggedAtUtc"] = DateTimeOffset.UtcNow,
            ["table"] = _tableLabel,
            ["target"] = _targetName,
            ["trigger"] = _trigger,
            ["operation"] = row.Operation,
            ["key"] = key,
            ["targetValues"] = NormalizeMap(row.TargetValues),
            ["mainValues"] = NormalizeMap(row.MainValues),
            ["mainChangeTimestamp"] = row.MainChangeTimestamp,
            ["targetChangeTimestamp"] = row.TargetChangeTimestamp,
            ["reviewStatus"] = "Pending",
            ["dryRun"] = _dryRun
        };
    }

    private void Remember(string location)
    {
        if (!_locations.Contains(location, StringComparer.OrdinalIgnoreCase))
        {
            _locations.Add(location);
        }
    }

    private string ReviewPath() => Path.Combine(CycleFolder(), Sanitize(_tableLabel) + ".json");

    private string BulkPath() => Path.Combine(CycleFolder(), Sanitize(_tableLabel) + ".jsonl");

    private string CycleFolder() => Path.Combine(_folder, _cycleId.ToString("N"), Sanitize(_targetName));

    private static Dictionary<string, object?>? NormalizeMap(Dictionary<string, object?>? values)
    {
        if (values is null)
        {
            return null;
        }

        var copy = new Dictionary<string, object?>(values.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in values)
        {
            copy[name] = Normalize(value);
        }

        return copy;
    }

    internal static object? Normalize(object? value) => value switch
    {
        null or DBNull => null,
        byte[] bytes => Convert.ToBase64String(bytes),
        DateTime dateTime => dateTime.ToString("o", CultureInfo.InvariantCulture),
        DateTimeOffset dateTimeOffset => dateTimeOffset.ToString("o", CultureInfo.InvariantCulture),
        TimeSpan timeSpan => timeSpan.ToString("c", CultureInfo.InvariantCulture),
        bool or string or Guid => value,
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString()
    };

    private static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray();
        var text = new string(chars);
        return string.IsNullOrWhiteSpace(text) ? "table" : text;
    }
}
