using System.Text.Json;
using System.Text.Json.Serialization;
using DatabaseSync.Core.Interfaces;
using DatabaseSync.Core.Models;
using DatabaseSync.Core.Scheduling;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DatabaseSync.Infrastructure.Configuration;

public sealed class SyncConfigStoreOptions
{
    public const string SectionName = "SyncConfigStore";

    /// <summary>
    /// Absolute or content-root-relative path to sync-config.json.
    /// </summary>
    public string FilePath { get; set; } = SyncConfiguration.FileName;
}

public sealed class SyncConfigStore : ISyncConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    private readonly ILogger<SyncConfigStore> _logger;
    private readonly string _filePath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SyncConfiguration? _cache;
    private DateTime _cacheWriteTimeUtc = DateTime.MinValue;

    public SyncConfigStore(IOptions<SyncConfigStoreOptions> options, ILogger<SyncConfigStore> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        var configured = options.Value.FilePath;
        _filePath = Path.IsPathRooted(configured)
            ? configured
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, configured));
    }

    public string ConfigFilePath => _filePath;

    public SyncConfiguration Get() =>
        GetAsync().GetAwaiter().GetResult();

    public async Task<SyncConfiguration> GetAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_filePath))
            {
                var empty = CreateDefault();
                Normalize(empty);
                await WriteFileAsync(empty, cancellationToken).ConfigureAwait(false);
                _cache = Clone(empty);
                _cacheWriteTimeUtc = File.GetLastWriteTimeUtc(_filePath);
                return Clone(_cache);
            }

            var writeTime = File.GetLastWriteTimeUtc(_filePath);
            if (_cache is not null && writeTime == _cacheWriteTimeUtc)
            {
                return Clone(_cache);
            }

            SyncConfiguration config;
            await using (var stream = File.OpenRead(_filePath))
            {
                config = await JsonSerializer
                    .DeserializeAsync<SyncConfiguration>(stream, JsonOptions, cancellationToken)
                    .ConfigureAwait(false) ?? CreateDefault();
            }

            if (Normalize(config))
            {
                await WriteFileAsync(config, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation(
                    "Migrated sync trigger settings in {ConfigPath} to version {Version}. EnableSynchronization and SyncIntervalSeconds are legacy mirrors and are not used to start cycles.",
                    _filePath,
                    config.SyncSettings.TriggerConfigVersion);
            }

            _cache = Clone(config);
            _cacheWriteTimeUtc = File.GetLastWriteTimeUtc(_filePath);
            return Clone(_cache);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(SyncConfiguration configuration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        Normalize(configuration);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WriteFileAsync(configuration, cancellationToken).ConfigureAwait(false);
            _cache = Clone(configuration);
            _cacheWriteTimeUtc = File.GetLastWriteTimeUtc(_filePath);
            _logger.LogInformation("Saved sync configuration to {ConfigPath}", _filePath);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task WriteFileAsync(SyncConfiguration configuration, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = _filePath + ".tmp";
        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, configuration, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
        }

        File.Copy(tempPath, _filePath, overwrite: true);
        File.Delete(tempPath);
    }

    /// <returns>True when trigger settings were migrated and the file should be saved.</returns>
    private static bool Normalize(SyncConfiguration configuration)
    {
        configuration.Connections ??= [];
        configuration.SyncSettings ??= new SyncSettings();
        var migrated = SyncSettingsMigrator.Apply(configuration.SyncSettings);

        foreach (var connection in configuration.Connections)
        {
            if (string.IsNullOrWhiteSpace(connection.Id))
            {
                connection.Id = Guid.NewGuid().ToString("N");
            }
        }

        var mains = configuration.Connections.Where(c => c.IsMain).ToList();
        if (mains.Count == 0 && configuration.Connections.Count > 0)
        {
            configuration.Connections[0].IsMain = true;
        }
        else if (mains.Count > 1)
        {
            for (var i = 1; i < mains.Count; i++)
            {
                mains[i].IsMain = false;
            }
        }

        return migrated;
    }

    private static SyncConfiguration CreateDefault() => new()
    {
        SyncSettings = new SyncSettings
        {
            TriggerConfigVersion = SyncSettingsMigrator.CurrentVersion,
            EnableSynchronization = false,
            IntervalSyncEnabled = false,
            ScheduledSyncEnabled = false,
            SyncMainToOthers = true,
            AutoSyncEveryMinutes = 2,
            AutoSyncStartTime = "00:00",
            AutoSyncEndTime = "23:59",
            SyncIntervalSeconds = 120
        },
        Connections = []
    };

    private static SyncConfiguration Clone(SyncConfiguration source)
    {
        var json = JsonSerializer.Serialize(source, JsonOptions);
        return JsonSerializer.Deserialize<SyncConfiguration>(json, JsonOptions) ?? CreateDefault();
    }
}
