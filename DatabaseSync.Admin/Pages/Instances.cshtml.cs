using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DatabaseSync.Core.Interfaces;
using DatabaseSync.Core.Models.Management;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DatabaseSync.Admin.Pages;

public class InstancesModel : PageModel
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly ISyncConfigStore _configStore;
    private readonly IManagementStore _managementStore;

    public InstancesModel(ISyncConfigStore configStore, IManagementStore managementStore)
    {
        _configStore = configStore;
        _managementStore = managementStore;
    }

    public string? CurrentInstanceName { get; private set; }
    public int SharedPort { get; private set; } = 5088;
    public string SharedAdminUrl { get; private set; } = "http://127.0.0.1:5088";
    public string? ActiveInstanceName { get; private set; }
    public IReadOnlyList<InstanceInfo> Instances { get; private set; } = [];
    public InstanceInfo? Editing { get; private set; }

    [BindProperty]
    public string Name { get; set; } = "";

    [BindProperty]
    public string OriginalName { get; set; } = "";

    [BindProperty]
    public string DatabaseHint { get; set; } = "";

    public void OnGet(string? edit = null)
    {
        Load();
        if (!string.IsNullOrWhiteSpace(edit))
        {
            Editing = Instances.FirstOrDefault(i =>
                string.Equals(i.Name, edit.Trim(), StringComparison.OrdinalIgnoreCase));
            if (Editing is null)
            {
                TempData["ErrorMessage"] = $"Instance '{edit}' not found.";
            }
            else
            {
                Name = Editing.Name;
                OriginalName = Editing.Name;
                DatabaseHint = Editing.DatabaseHint;
            }
        }
    }

    public async Task<IActionResult> OnPostAddAsync()
    {
        Load();
        var name = (Name ?? "").Trim();
        var db = string.IsNullOrWhiteSpace(DatabaseHint) ? "YourDatabase" : DatabaseHint.Trim();

        if (!Regex.IsMatch(name, @"^[A-Za-z0-9_\-]+$"))
        {
            TempData["ErrorMessage"] = "Database name may only contain letters, numbers, underscore, and hyphen.";
            return RedirectToPage();
        }

        var root = GetInstancesRoot();
        if (string.IsNullOrWhiteSpace(root))
        {
            TempData["ErrorMessage"] = "Databases folder not found.";
            return RedirectToPage();
        }

        var dir = Path.Combine(root, name);
        if (Directory.Exists(dir) && System.IO.File.Exists(Path.Combine(dir, "instance.json")))
        {
            TempData["ErrorMessage"] = $"Database '{name}' already exists.";
            return RedirectToPage();
        }

        try
        {
            Directory.CreateDirectory(dir);
            var configPath = Path.Combine(dir, "sync-config.json");
            WriteMeta(dir, name, db, configPath, createdAtUtc: null);
            EnsureSyncConfig(dir, name, db, overwrite: false);
            WriteLocalScripts(dir, name);
            await _managementStore.UpsertManagedDatabaseAsync(new ManagedDatabaseRecord
            {
                Name = name,
                DatabaseHint = db,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            });
            TempData["StatusMessage"] = $"Database '{name}' added. Edit Servers after you Start it.";
        }
        catch (Exception ex)
        {
            TempData["ErrorMessage"] = "Could not add database: " + ex.Message;
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostEditAsync()
    {
        Load();
        var original = (OriginalName ?? Name ?? "").Trim();
        var name = (Name ?? original).Trim();
        var db = string.IsNullOrWhiteSpace(DatabaseHint) ? "YourDatabase" : DatabaseHint.Trim();

        if (string.IsNullOrWhiteSpace(original))
        {
            TempData["ErrorMessage"] = "Missing instance name to edit.";
            return RedirectToPage();
        }

        if (!Regex.IsMatch(name, @"^[A-Za-z0-9_\-]+$"))
        {
            TempData["ErrorMessage"] = "Instance name may only contain letters, numbers, underscore, and hyphen.";
            return RedirectToPage(null, new { edit = original });
        }

        var root = GetInstancesRoot();
        if (string.IsNullOrWhiteSpace(root))
        {
            TempData["ErrorMessage"] = "Instances folder not found.";
            return RedirectToPage();
        }

        var oldDir = Path.Combine(root, original);
        var metaPath = Path.Combine(oldDir, "instance.json");
        if (!System.IO.File.Exists(metaPath))
        {
            TempData["ErrorMessage"] = $"Instance '{original}' not found.";
            return RedirectToPage();
        }

        if (IsRunning(oldDir) || string.Equals(original, CurrentInstanceName, StringComparison.OrdinalIgnoreCase))
        {
            // Allow metadata/db hint edit while running, but block rename of the live instance.
            if (!string.Equals(original, name, StringComparison.OrdinalIgnoreCase))
            {
                TempData["ErrorMessage"] = $"Stop '{original}' (or start another instance) before renaming it.";
                return RedirectToPage(null, new { edit = original });
            }
        }

        try
        {
            string? createdAt = null;
            try
            {
                using var stream = System.IO.File.OpenRead(metaPath);
                var existing = JsonSerializer.Deserialize<InstanceJson>(stream, JsonOpts);
                createdAt = existing?.CreatedAtUtc;
            }
            catch
            {
                // ignore — WriteMeta will set a new timestamp
            }

            var targetDir = oldDir;
            if (!string.Equals(original, name, StringComparison.OrdinalIgnoreCase))
            {
                var newDir = Path.Combine(root, name);
                if (Directory.Exists(newDir))
                {
                    TempData["ErrorMessage"] = $"Cannot rename to '{name}' — that instance already exists.";
                    return RedirectToPage(null, new { edit = original });
                }

                Directory.Move(oldDir, newDir);
                targetDir = newDir;
                WriteLocalScripts(targetDir, name);

                var activePath = Path.Combine(root, "active-product.txt");
                if (System.IO.File.Exists(activePath) &&
                    string.Equals(System.IO.File.ReadAllText(activePath).Trim(), original, StringComparison.OrdinalIgnoreCase))
                {
                    System.IO.File.WriteAllText(activePath, name);
                }
            }

            var configPath = Path.Combine(targetDir, "sync-config.json");
            WriteMeta(targetDir, name, db, configPath, createdAt);

            var configNote = "";
            try
            {
                UpdateConfigDatabases(configPath, db);
            }
            catch (Exception cfgEx)
            {
                configNote = " (database hint saved; sync-config update skipped: " + cfgEx.Message + ")";
            }

            await _managementStore.UpsertManagedDatabaseAsync(new ManagedDatabaseRecord
            {
                Name = name,
                DatabaseHint = db,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            });
            TempData["StatusMessage"] = $"Database '{name}' updated." + configNote;
            return RedirectToPage();
        }
        catch (Exception ex)
        {
            TempData["ErrorMessage"] = "Could not update database: " + ex.Message + " You can Delete this database and Add it again.";
            return RedirectToPage(null, new { edit = original });
        }
    }

    public IActionResult OnPostDelete()
    {
        Load();
        var name = (Name ?? "").Trim();
        var root = GetInstancesRoot();
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(name))
        {
            TempData["ErrorMessage"] = "Instance not specified.";
            return RedirectToPage();
        }

        var dir = Path.Combine(root, name);
        if (!Directory.Exists(dir) || !System.IO.File.Exists(Path.Combine(dir, "instance.json")))
        {
            TempData["ErrorMessage"] = $"Instance '{name}' not found.";
            return RedirectToPage();
        }

        if (string.Equals(name, CurrentInstanceName, StringComparison.OrdinalIgnoreCase))
        {
            TempData["ErrorMessage"] =
                $"Cannot delete '{name}' while it is the instance this Admin is using. Start another instance first, then delete '{name}'.";
            return RedirectToPage();
        }

        try
        {
            if (IsRunning(dir))
            {
                StopInstanceProcesses(root, name);
                Thread.Sleep(800);
            }

            // Best-effort remove; retry briefly for locked files under .run
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    Directory.Delete(dir, recursive: true);
                    break;
                }
                catch when (attempt < 3)
                {
                    Thread.Sleep(500);
                }
            }

            if (Directory.Exists(dir))
            {
                TempData["ErrorMessage"] = $"Could not fully delete '{name}'. Close any open files in that folder and try again.";
                return RedirectToPage();
            }

            var activePath = Path.Combine(root, "active-product.txt");
            if (System.IO.File.Exists(activePath) &&
                string.Equals(System.IO.File.ReadAllText(activePath).Trim(), name, StringComparison.OrdinalIgnoreCase))
            {
                System.IO.File.Delete(activePath);
            }

            TempData["StatusMessage"] = $"Instance '{name}' deleted.";
        }
        catch (Exception ex)
        {
            TempData["ErrorMessage"] = "Could not delete instance: " + ex.Message;
        }

        return RedirectToPage();
    }

    public IActionResult OnPostStart()
    {
        Load();
        var name = (Name ?? "").Trim();
        var root = GetInstancesRoot();
        if (string.IsNullOrWhiteSpace(root))
        {
            TempData["ErrorMessage"] = "Instances folder not found.";
            return RedirectToPage();
        }

        var metaPath = Path.Combine(root, name, "instance.json");
        if (!System.IO.File.Exists(metaPath))
        {
            TempData["ErrorMessage"] = $"Instance '{name}' not found.";
            return RedirectToPage();
        }

        var script = Path.Combine(root, "Start-SyncInstance.ps1");
        if (!System.IO.File.Exists(script))
        {
            TempData["ErrorMessage"] = "Start-SyncInstance.ps1 missing.";
            return RedirectToPage();
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\" -Name \"{name}\"",
                WorkingDirectory = root,
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                CreateNoWindow = true
            };
            Process.Start(psi);
            TempData["StatusMessage"] =
                $"Starting '{name}' on port {SharedPort}. Only one instance runs at a time — wait a few seconds, then open {SharedAdminUrl}.";
            TempData["SwitchingProduct"] = "1";
            TempData["SwitchUrl"] = SharedAdminUrl;
        }
        catch (Exception ex)
        {
            TempData["ErrorMessage"] = "Could not start instance: " + ex.Message;
        }

        return RedirectToPage();
    }

    public IActionResult OnPostStop()
    {
        Load();
        var name = (Name ?? "").Trim();
        var root = GetInstancesRoot();
        if (string.IsNullOrWhiteSpace(root))
        {
            TempData["ErrorMessage"] = "Instances folder not found.";
            return RedirectToPage();
        }

        try
        {
            StopInstanceProcesses(root, name);
            TempData["StatusMessage"] = $"Stopped '{name}'.";
        }
        catch (Exception ex)
        {
            TempData["ErrorMessage"] = "Could not stop instance: " + ex.Message;
        }

        return RedirectToPage();
    }

    private static void StopInstanceProcesses(string root, string name)
    {
        var script = Path.Combine(root, "Stop-SyncInstance.ps1");
        if (System.IO.File.Exists(script))
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\" -Name \"{name}\"",
                WorkingDirectory = root,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit(15000);
            return;
        }

        var run = Path.Combine(root, name, ".run");
        KillPidFile(Path.Combine(run, "admin.pid"));
        KillPidFile(Path.Combine(run, "worker.pid"));
    }

    private static void KillPidFile(string file)
    {
        if (!System.IO.File.Exists(file))
        {
            return;
        }

        if (int.TryParse(System.IO.File.ReadLines(file).FirstOrDefault(), out var pid) && pid > 0)
        {
            try
            {
                Process.GetProcessById(pid).Kill(entireProcessTree: true);
            }
            catch
            {
                // already gone
            }
        }

        try { System.IO.File.Delete(file); } catch { /* ignore */ }
    }

    private void Load()
    {
        var configPath = _configStore.ConfigFilePath;
        var instanceDir = Path.GetDirectoryName(configPath);
        CurrentInstanceName = string.IsNullOrWhiteSpace(instanceDir)
            ? null
            : new DirectoryInfo(instanceDir).Name;

        var root = GetInstancesRoot();
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            Instances = [];
            return;
        }

        ReadSharedPort(root);
        var activePath = Path.Combine(root, "active-product.txt");
        if (System.IO.File.Exists(activePath))
        {
            ActiveInstanceName = System.IO.File.ReadAllText(activePath).Trim();
        }

        var list = new List<InstanceInfo>();
        foreach (var dir in Directory.GetDirectories(root))
        {
            var metaPath = Path.Combine(dir, "instance.json");
            if (!System.IO.File.Exists(metaPath))
            {
                continue;
            }

            try
            {
                using var stream = System.IO.File.OpenRead(metaPath);
                var meta = JsonSerializer.Deserialize<InstanceJson>(stream, JsonOpts);
                if (meta is null || string.IsNullOrWhiteSpace(meta.Name))
                {
                    continue;
                }

                var name = meta.Name;
                var running = IsRunning(dir);
                list.Add(new InstanceInfo
                {
                    Name = name,
                    Port = SharedPort,
                    AdminUrl = SharedAdminUrl,
                    DatabaseHint = meta.DatabaseHint ?? "",
                    IsCurrent = string.Equals(name, CurrentInstanceName, StringComparison.OrdinalIgnoreCase),
                    IsRunning = running,
                    ConfigExists = System.IO.File.Exists(Path.Combine(dir, "sync-config.json"))
                });
            }
            catch
            {
                // Skip invalid metadata files.
            }
        }

        Instances = list
            .OrderByDescending(i => i.IsRunning)
            .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var activeRunning = Instances.FirstOrDefault(i => i.IsRunning);
        if (activeRunning is not null)
        {
            ActiveInstanceName = activeRunning.Name;
        }
        else if (string.IsNullOrWhiteSpace(ActiveInstanceName) && !string.IsNullOrWhiteSpace(CurrentInstanceName))
        {
            ActiveInstanceName = CurrentInstanceName;
        }
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

    private void ReadSharedPort(string root)
    {
        var path = Path.Combine(root, "shared-port.json");
        SharedPort = 5088;
        SharedAdminUrl = "http://127.0.0.1:5088";
        if (!System.IO.File.Exists(path))
        {
            return;
        }

        try
        {
            using var stream = System.IO.File.OpenRead(path);
            var shared = JsonSerializer.Deserialize<SharedPortJson>(stream, JsonOpts);
            if (shared?.Port is > 0)
            {
                SharedPort = shared.Port;
                SharedAdminUrl = string.IsNullOrWhiteSpace(shared.AdminUrl)
                    ? $"http://127.0.0.1:{SharedPort}"
                    : shared.AdminUrl!;
            }
        }
        catch
        {
            // keep defaults
        }
    }

    private static bool IsRunning(string instanceDir)
    {
        var pidDir = Path.Combine(instanceDir, ".run");
        return PidAlive(Path.Combine(pidDir, "admin.pid")) || PidAlive(Path.Combine(pidDir, "worker.pid"));
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

    private void WriteMeta(string dir, string name, string databaseHint, string configPath, string? createdAtUtc)
    {
        var meta = new Dictionary<string, object?>
        {
            ["name"] = name,
            ["port"] = SharedPort,
            ["adminUrl"] = SharedAdminUrl,
            ["databaseHint"] = databaseHint,
            ["configPath"] = configPath,
            ["createdAtUtc"] = string.IsNullOrWhiteSpace(createdAtUtc) ? DateTime.UtcNow.ToString("o") : createdAtUtc
        };
        System.IO.File.WriteAllText(Path.Combine(dir, "instance.json"), JsonSerializer.Serialize(meta, JsonOpts));
    }

    private void EnsureSyncConfig(string dir, string name, string database, bool overwrite)
    {
        var configPath = Path.Combine(dir, "sync-config.json");
        if (System.IO.File.Exists(configPath) && !overwrite)
        {
            return;
        }

        var root = GetInstancesRoot();
        var repoRoot = Directory.GetParent(root!)?.FullName;
        var example = Path.Combine(repoRoot ?? "", "DatabaseSync.Worker", "sync-config.example.json");
        if (!System.IO.File.Exists(example))
        {
            throw new FileNotFoundException("Missing sync-config.example.json", example);
        }

        System.IO.File.Copy(example, Path.Combine(dir, "sync-config.example.json"), overwrite: true);
        var node = JsonNode.Parse(System.IO.File.ReadAllText(example))!.AsObject();
        if (node["Connections"] is JsonArray connections)
        {
            var i = 0;
            foreach (var c in connections.OfType<JsonObject>())
            {
                c["Database"] = database;
                c["ApplicationName"] = $"Coditium.DatabaseSync.{name}";
                var isMain = c["IsMain"]?.GetValue<bool>() == true;
                c["Name"] = isMain ? $"{name} Main" : $"{name} Target";
                var id = c["Id"]?.GetValue<string>() ?? "";
                if (id == "main-server") c["Id"] = $"{name.ToLowerInvariant()}-main";
                else if (id == "cloud-server-1") c["Id"] = $"{name.ToLowerInvariant()}-cloud-1";
                else if (id == "cloud-server-2") c["Id"] = $"{name.ToLowerInvariant()}-cloud-2";
                else if (string.IsNullOrWhiteSpace(id)) c["Id"] = $"{name.ToLowerInvariant()}-{++i}";
            }
        }

        System.IO.File.WriteAllText(configPath, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void UpdateConfigDatabases(string configPath, string database)
    {
        if (!System.IO.File.Exists(configPath))
        {
            return;
        }

        var node = JsonNode.Parse(System.IO.File.ReadAllText(configPath))?.AsObject();
        if (node?["Connections"] is not JsonArray connections)
        {
            return;
        }

        foreach (var c in connections.OfType<JsonObject>())
        {
            c["Database"] = database;
        }

        System.IO.File.WriteAllText(configPath, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void WriteLocalScripts(string dir, string name)
    {
        System.IO.File.WriteAllText(
            Path.Combine(dir, "start.ps1"),
            $"# Start {name}\r\n& \"$PSScriptRoot\\..\\Start-SyncInstance.ps1\" -Name \"{name}\"\r\n");
        System.IO.File.WriteAllText(
            Path.Combine(dir, "stop.ps1"),
            $"# Stop {name}\r\n& \"$PSScriptRoot\\..\\Stop-SyncInstance.ps1\" -Name \"{name}\"\r\n");
    }

    public sealed class InstanceInfo
    {
        public string Name { get; init; } = "";
        public int Port { get; init; }
        public string AdminUrl { get; init; } = "";
        public string DatabaseHint { get; init; } = "";
        public bool IsCurrent { get; init; }
        public bool IsRunning { get; init; }
        public bool ConfigExists { get; init; }
    }

    private sealed class InstanceJson
    {
        public string? Name { get; set; }
        public int Port { get; set; }
        public string? AdminUrl { get; set; }
        public string? DatabaseHint { get; set; }
        public string? CreatedAtUtc { get; set; }
    }

    private sealed class SharedPortJson
    {
        public int Port { get; set; }
        public string? AdminUrl { get; set; }
    }
}
