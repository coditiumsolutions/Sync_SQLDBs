using System.Diagnostics;
using DatabaseSync.Core.Interfaces;

namespace DatabaseSync.Admin.Services;

public sealed class ActiveInstanceInfo
{
    private readonly ISyncConfigStore _configStore;

    public ActiveInstanceInfo(ISyncConfigStore configStore)
    {
        _configStore = configStore;
    }

    public string? Name { get; private set; }
    public bool IsRunning { get; private set; }
    public int SharedPort { get; private set; } = 5088;

    public ActiveInstanceInfo Refresh()
    {
        Name = null;
        IsRunning = false;
        SharedPort = 5088;

        var configPath = _configStore.ConfigFilePath;
        var instanceDir = Path.GetDirectoryName(configPath);
        if (string.IsNullOrWhiteSpace(instanceDir))
        {
            return this;
        }

        var currentName = new DirectoryInfo(instanceDir).Name;
        var root = Directory.GetParent(instanceDir)?.FullName;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            Name = currentName;
            return this;
        }

        var sharedPortPath = Path.Combine(root, "shared-port.json");
        if (System.IO.File.Exists(sharedPortPath))
        {
            try
            {
                var json = System.IO.File.ReadAllText(sharedPortPath);
                var match = System.Text.RegularExpressions.Regex.Match(json, "\"port\"\\s*:\\s*(\\d+)");
                if (match.Success && int.TryParse(match.Groups[1].Value, out var port) && port > 0)
                {
                    SharedPort = port;
                }
            }
            catch
            {
                // keep default
            }
        }

        string? runningName = null;
        foreach (var dir in Directory.GetDirectories(root))
        {
            var metaPath = Path.Combine(dir, "instance.json");
            if (!System.IO.File.Exists(metaPath))
            {
                continue;
            }

            if (IsRunningDir(dir))
            {
                runningName = new DirectoryInfo(dir).Name;
                break;
            }
        }

        if (!string.IsNullOrWhiteSpace(runningName))
        {
            Name = runningName;
            IsRunning = true;
            return this;
        }

        var activePath = Path.Combine(root, "active-product.txt");
        if (System.IO.File.Exists(activePath))
        {
            var marked = System.IO.File.ReadAllText(activePath).Trim();
            if (!string.IsNullOrWhiteSpace(marked))
            {
                Name = marked;
                return this;
            }
        }

        Name = currentName;
        return this;
    }

    private static bool IsRunningDir(string instanceDir)
    {
        var run = Path.Combine(instanceDir, ".run");
        return PidAlive(Path.Combine(run, "admin.pid")) || PidAlive(Path.Combine(run, "worker.pid"));
    }

    private static bool PidAlive(string file)
    {
        if (!System.IO.File.Exists(file))
        {
            return false;
        }

        if (!int.TryParse(System.IO.File.ReadLines(file).FirstOrDefault(), out var pid) || pid <= 0)
        {
            return false;
        }

        try
        {
            Process.GetProcessById(pid);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
