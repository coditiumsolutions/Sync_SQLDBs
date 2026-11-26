using System.Text.Json;
using DatabaseSync.Core.Interfaces;
using DatabaseSync.Core.Models;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DatabaseSync.Admin.Pages;

public class SummaryModel : PageModel
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly ISyncConfigStore _configStore;

    public SummaryModel(ISyncConfigStore configStore)
    {
        _configStore = configStore;
    }

    public IReadOnlyList<InstanceSummaryRow> Rows { get; private set; } = [];

    public void OnGet()
    {
        var root = GetInstancesRoot();
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            Rows = [];
            return;
        }

        var rows = new List<InstanceSummaryRow>();
        foreach (var dir in Directory.GetDirectories(root))
        {
            var metaPath = Path.Combine(dir, "instance.json");
            if (!System.IO.File.Exists(metaPath))
            {
                continue;
            }

            string instanceName;
            try
            {
                using var stream = System.IO.File.OpenRead(metaPath);
                var meta = JsonSerializer.Deserialize<InstanceMeta>(stream, JsonOpts);
                instanceName = string.IsNullOrWhiteSpace(meta?.Name)
                    ? new DirectoryInfo(dir).Name
                    : meta.Name!;
            }
            catch
            {
                instanceName = new DirectoryInfo(dir).Name;
            }

            var configPath = Path.Combine(dir, "sync-config.json");
            if (!System.IO.File.Exists(configPath))
            {
                rows.Add(new InstanceSummaryRow
                {
                    InstanceName = instanceName,
                    HostProviderName = "—",
                    IpAddress = "—",
                    Role = "—",
                    Database = "—",
                    IsEnabled = false
                });
                continue;
            }

            try
            {
                var json = System.IO.File.ReadAllText(configPath);
                var config = JsonSerializer.Deserialize<SyncConfiguration>(json, JsonOpts);
                var connections = config?.Connections ?? [];
                if (connections.Count == 0)
                {
                    rows.Add(new InstanceSummaryRow
                    {
                        InstanceName = instanceName,
                        HostProviderName = "—",
                        IpAddress = "—",
                        Role = "—",
                        Database = "—",
                        IsEnabled = false
                    });
                    continue;
                }

                foreach (var c in connections.OrderByDescending(x => x.IsMain).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
                {
                    rows.Add(new InstanceSummaryRow
                    {
                        InstanceName = instanceName,
                        HostProviderName = string.IsNullOrWhiteSpace(c.Name) ? c.Id : c.Name,
                        IpAddress = string.IsNullOrWhiteSpace(c.Server) ? "—" : c.Server,
                        Role = c.IsMain ? "Main" : "Target",
                        Database = c.Database ?? "",
                        IsEnabled = c.IsEnabled
                    });
                }
            }
            catch
            {
                rows.Add(new InstanceSummaryRow
                {
                    InstanceName = instanceName,
                    HostProviderName = "—",
                    IpAddress = "(config unreadable)",
                    Role = "—",
                    Database = "—",
                    IsEnabled = false
                });
            }
        }

        Rows = rows
            .OrderBy(r => r.InstanceName, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(r => r.Role == "Main")
            .ThenBy(r => r.HostProviderName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private string? GetInstancesRoot()
    {
        var configPath = _configStore.ConfigFilePath;
        var instanceDir = Path.GetDirectoryName(configPath);
        if (string.IsNullOrWhiteSpace(instanceDir))
        {
            return null;
        }

        return Directory.GetParent(instanceDir)?.FullName;
    }

    public sealed class InstanceSummaryRow
    {
        public string InstanceName { get; init; } = "";
        public string HostProviderName { get; init; } = "";
        public string IpAddress { get; init; } = "";
        public string Role { get; init; } = "";
        public string Database { get; init; } = "";
        public bool IsEnabled { get; init; }
    }

    private sealed class InstanceMeta
    {
        public string? Name { get; set; }
    }
}
