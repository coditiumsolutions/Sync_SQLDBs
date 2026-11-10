using System.Text.Json;
using DatabaseSync.Core.Interfaces;
using DatabaseSync.Core.Models;
using Microsoft.Extensions.Options;

namespace DatabaseSync.Infrastructure.Configuration;

public sealed class ManualSyncRequestStore : IManualSyncRequestStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ManualSyncRequestStore(IOptions<SyncConfigStoreOptions> options)
    {
        var configPath = Path.IsPathRooted(options.Value.FilePath)
            ? options.Value.FilePath
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, options.Value.FilePath));
        var directory = Path.GetDirectoryName(configPath) ?? AppContext.BaseDirectory;
        _path = Path.Combine(directory, "sync-manual-request.json");
    }

    public string RequestFilePath => _path;

    public ManualSyncRequest Write(DateTimeOffset? requestedAtUtc = null)
    {
        var request = new ManualSyncRequest
        {
            RequestId = Guid.NewGuid(),
            RequestedAtUtc = requestedAtUtc ?? DateTimeOffset.UtcNow
        };

        _gate.Wait();
        try
        {
            WriteFile(request);
            return request;
        }
        finally
        {
            _gate.Release();
        }
    }

    public ManualSyncRequest? Read()
    {
        _gate.Wait();
        try
        {
            if (!File.Exists(_path))
            {
                return null;
            }

            var json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<ManualSyncRequest>(json, JsonOptions);
        }
        catch (IOException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void CompleteIfCurrent(Guid requestId)
    {
        _gate.Wait();
        try
        {
            var current = ReadUnlocked();
            if (current is null || current.RequestId != requestId)
            {
                return;
            }

            if (File.Exists(_path))
            {
                File.Delete(_path);
            }
        }
        catch (IOException)
        {
            // The next poll will try again.
        }
        finally
        {
            _gate.Release();
        }
    }

    private ManualSyncRequest? ReadUnlocked()
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        var json = File.ReadAllText(_path);
        return JsonSerializer.Deserialize<ManualSyncRequest>(json, JsonOptions);
    }

    private void WriteFile(ManualSyncRequest request)
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(request, JsonOptions));
        File.Copy(temp, _path, overwrite: true);
        File.Delete(temp);
    }
}
