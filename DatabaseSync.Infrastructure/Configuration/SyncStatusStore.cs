using System.Text.Json;
using DatabaseSync.Core.Interfaces;
using DatabaseSync.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DatabaseSync.Infrastructure.Configuration;

public sealed class SyncStatusStore : ISyncStatusStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly ILogger<SyncStatusStore> _logger;
    private readonly string _filePath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SyncStatusStore(IOptions<SyncConfigStoreOptions> options, ILogger<SyncStatusStore> logger)
    {
        _logger = logger;
        // Keep status next to sync-config.json.
        var configPath = Path.IsPathRooted(options.Value.FilePath)
            ? options.Value.FilePath
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, options.Value.FilePath));

        var directory = Path.GetDirectoryName(configPath) ?? AppContext.BaseDirectory;
        _filePath = Path.Combine(directory, SyncRuntimeStatus.FileName);
    }

    public string StatusFilePath => _filePath;

    public SyncRuntimeStatus Get()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return new SyncRuntimeStatus
                {
                    State = "Idle",
                    Message = "No sync has run yet."
                };
            }

            var json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize<SyncRuntimeStatus>(json, JsonOptions)
                   ?? new SyncRuntimeStatus { State = "Idle", Message = "No sync has run yet." };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read sync status file.");
            return new SyncRuntimeStatus
            {
                State = "Idle",
                Message = "Could not read sync status."
            };
        }
    }

    public async Task WriteAsync(SyncRuntimeStatus status, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(status);
        status.UpdatedAtUtc = DateTimeOffset.UtcNow;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temp = _filePath + ".tmp";
            await using (var stream = File.Create(temp))
            {
                await JsonSerializer.SerializeAsync(stream, status, JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
            }

            File.Copy(temp, _filePath, overwrite: true);
            File.Delete(temp);
        }
        finally
        {
            _gate.Release();
        }
    }
}
