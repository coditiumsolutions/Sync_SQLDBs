using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DatabaseSync.Admin.Services;
using DatabaseSync.Core.Interfaces;
using DatabaseSync.Core.Models;
using DatabaseSync.Core.Models.Management;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DatabaseSync.Admin.Pages.Connections;

public class EditModel : PageModel
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly ISyncConfigStore _configStore;
    private readonly IManagementStore _managementStore;
    private readonly ActiveInstanceInfo _activeInstance;

    public EditModel(
        ISyncConfigStore configStore,
        IManagementStore managementStore,
        ActiveInstanceInfo activeInstance)
    {
        _configStore = configStore;
        _managementStore = managementStore;
        _activeInstance = activeInstance;
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public bool IsNew => string.IsNullOrWhiteSpace(Input.Id);

    public async Task<IActionResult> OnGetAsync(string? id)
    {
        _activeInstance.Refresh();
        if (string.IsNullOrWhiteSpace(id))
        {
            Input = new InputModel
            {
                DatabaseName = _activeInstance.Name ?? "",
                IsEnabled = true,
                Encrypt = true,
                TrustServerCertificate = true,
                ConnectionTimeoutSeconds = 15
            };
            return Page();
        }

        var config = await _configStore.GetAsync();
        var connection = config.Connections.FirstOrDefault(c => c.Id == id);
        if (connection is null)
        {
            TempData["ErrorMessage"] = "Server not found.";
            return RedirectToPage("./Index");
        }

        Input = InputModel.FromConnection(connection, _activeInstance.Name ?? "");
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        _activeInstance.Refresh();
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var databaseName = (Input.DatabaseName ?? "").Trim();
        var databaseHint = string.IsNullOrWhiteSpace(Input.Database)
            ? "YourDatabase"
            : Input.Database.Trim();

        if (!Regex.IsMatch(databaseName, @"^[A-Za-z0-9_\-]+$"))
        {
            ModelState.AddModelError(nameof(Input.DatabaseName), "Database may only contain letters, numbers, underscore, and hyphen.");
            return Page();
        }

        var currentName = _activeInstance.Name ?? "";
        try
        {
            if (!string.Equals(databaseName, currentName, StringComparison.OrdinalIgnoreCase))
            {
                var createdNewDatabase = await EnsureDatabaseInstanceAsync(databaseName, databaseHint);
                await SeedInstanceConnectionAsync(databaseName, databaseHint, replaceConnections: createdNewDatabase);
                await _managementStore.UpsertManagedDatabaseAsync(new ManagedDatabaseRecord
                {
                    Name = databaseName,
                    DatabaseHint = databaseHint,
                    CreatedAtUtc = DateTimeOffset.UtcNow,
                    UpdatedAtUtc = DateTimeOffset.UtcNow
                });

                TempData["StatusMessage"] = createdNewDatabase
                    ? $"Database '{databaseName}' created with server '{Input.Name}'. Start '{databaseName}' from Databases to use it."
                    : $"Server '{Input.Name}' saved under database '{databaseName}'. Start '{databaseName}' from Databases to use it.";
                return RedirectToPage("/Instances");
            }

            await UpdateCurrentInstanceHintAsync(databaseName, databaseHint);
            await _managementStore.UpsertManagedDatabaseAsync(new ManagedDatabaseRecord
            {
                Name = databaseName,
                DatabaseHint = databaseHint,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            });
        }
        catch (Exception ex)
        {
            ModelState.AddModelError(string.Empty, "Could not save database metadata: " + ex.Message);
            return Page();
        }

        var config = await _configStore.GetAsync();
        SyncConnection connection;

        if (string.IsNullOrWhiteSpace(Input.Id))
        {
            connection = new SyncConnection { Id = Guid.NewGuid().ToString("N") };
            config.Connections.Add(connection);
        }
        else
        {
            connection = config.Connections.FirstOrDefault(c => c.Id == Input.Id)
                ?? throw new InvalidOperationException("Server not found.");
        }

        var previousPassword = connection.Password;
        Input.ApplyTo(connection);

        if (string.IsNullOrWhiteSpace(Input.Password) && !string.IsNullOrWhiteSpace(previousPassword))
        {
            connection.Password = previousPassword;
        }

        if (Input.IsMain)
        {
            foreach (var item in config.Connections)
            {
                item.IsMain = item.Id == connection.Id;
            }
        }
        else if (config.Connections.All(c => !c.IsMain) && config.Connections.Count > 0)
        {
            config.Connections[0].IsMain = true;
        }

        await _configStore.SaveAsync(config);
        TempData["StatusMessage"] = $"Saved server '{connection.Name}' for database '{databaseName}'.";
        return RedirectToPage("./Index");
    }

    private async Task<bool> EnsureDatabaseInstanceAsync(string name, string databaseHint)
    {
        var root = GetInstancesRoot();
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new InvalidOperationException("Databases folder not found.");
        }

        var dir = Path.Combine(root, name);
        var metaPath = Path.Combine(dir, "instance.json");
        if (Directory.Exists(dir) && System.IO.File.Exists(metaPath))
        {
            await UpdateInstanceHintFileAsync(dir, name, databaseHint);
            return false;
        }

        Directory.CreateDirectory(dir);
        var configPath = Path.Combine(dir, "sync-config.json");
        WriteMeta(dir, name, databaseHint, configPath, createdAtUtc: null);
        EnsureSyncConfig(dir, name, databaseHint, overwrite: false);
        WriteLocalScripts(dir, name);
        return true;
    }

    private async Task UpdateCurrentInstanceHintAsync(string name, string databaseHint)
    {
        var root = GetInstancesRoot();
        if (string.IsNullOrWhiteSpace(root))
        {
            return;
        }

        var dir = Path.Combine(root, name);
        if (!Directory.Exists(dir))
        {
            return;
        }

        await UpdateInstanceHintFileAsync(dir, name, databaseHint);
    }

    private async Task UpdateInstanceHintFileAsync(string dir, string name, string databaseHint)
    {
        var metaPath = Path.Combine(dir, "instance.json");
        string? createdAt = null;
        if (System.IO.File.Exists(metaPath))
        {
            try
            {
                using var stream = System.IO.File.OpenRead(metaPath);
                var existing = await JsonSerializer.DeserializeAsync<InstanceJson>(stream, JsonOpts);
                createdAt = existing?.CreatedAtUtc;
            }
            catch
            {
                // ignore
            }
        }

        var configPath = Path.Combine(dir, "sync-config.json");
        WriteMeta(dir, name, databaseHint, configPath, createdAt);
    }

    private async Task SeedInstanceConnectionAsync(string name, string databaseHint, bool replaceConnections)
    {
        var root = GetInstancesRoot();
        if (string.IsNullOrWhiteSpace(root))
        {
            return;
        }

        var configPath = Path.Combine(root, name, "sync-config.json");
        if (!System.IO.File.Exists(configPath))
        {
            return;
        }

        var json = await System.IO.File.ReadAllTextAsync(configPath);
        var config = JsonSerializer.Deserialize<SyncConfiguration>(json, JsonOpts) ?? new SyncConfiguration();
        var connection = new SyncConnection { Id = Guid.NewGuid().ToString("N") };
        Input.ApplyTo(connection);
        connection.Database = databaseHint;
        connection.IsEnabled = Input.IsEnabled;
        if (string.IsNullOrWhiteSpace(connection.Password))
        {
            connection.Password = Input.Password;
        }

        if (replaceConnections || config.Connections.Count == 0)
        {
            connection.IsMain = true;
            config.Connections = [connection];
        }
        else
        {
            if (connection.IsMain)
            {
                foreach (var item in config.Connections)
                {
                    item.IsMain = false;
                }
            }
            else if (config.Connections.All(c => !c.IsMain))
            {
                connection.IsMain = true;
            }

            config.Connections.Add(connection);
        }

        await System.IO.File.WriteAllTextAsync(
            configPath,
            JsonSerializer.Serialize(config, JsonOpts));
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

    private void WriteMeta(string dir, string name, string databaseHint, string configPath, string? createdAtUtc)
    {
        var port = 5088;
        var adminUrl = "http://127.0.0.1:5088";
        var root = GetInstancesRoot();
        if (!string.IsNullOrWhiteSpace(root))
        {
            var sharedPath = Path.Combine(root, "shared-port.json");
            if (System.IO.File.Exists(sharedPath))
            {
                try
                {
                    using var stream = System.IO.File.OpenRead(sharedPath);
                    var shared = JsonSerializer.Deserialize<SharedPortJson>(stream, JsonOpts);
                    if (shared?.Port is > 0)
                    {
                        port = shared.Port;
                        adminUrl = string.IsNullOrWhiteSpace(shared.AdminUrl)
                            ? $"http://127.0.0.1:{port}"
                            : shared.AdminUrl!;
                    }
                }
                catch
                {
                    // keep defaults
                }
            }
        }

        var meta = new Dictionary<string, object?>
        {
            ["name"] = name,
            ["port"] = port,
            ["adminUrl"] = adminUrl,
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

    private static void WriteLocalScripts(string dir, string name)
    {
        System.IO.File.WriteAllText(
            Path.Combine(dir, "start.ps1"),
            $"# Start {name}\r\n& \"$PSScriptRoot\\..\\Start-SyncInstance.ps1\" -Name \"{name}\"\r\n");
        System.IO.File.WriteAllText(
            Path.Combine(dir, "stop.ps1"),
            $"# Stop {name}\r\n& \"$PSScriptRoot\\..\\Stop-SyncInstance.ps1\" -Name \"{name}\"\r\n");
    }

    public sealed class InputModel
    {
        public string? Id { get; set; }

        [Required, Display(Name = "Database")]
        [RegularExpression(@"^[A-Za-z0-9_\-]+$", ErrorMessage = "Database may only contain letters, numbers, underscore, and hyphen.")]
        public string DatabaseName { get; set; } = string.Empty;

        [Required, Display(Name = "Display name")]
        public string Name { get; set; } = string.Empty;

        [Display(Name = "Nominate as Source")]
        public bool IsMain { get; set; }

        [Display(Name = "Enabled")]
        public bool IsEnabled { get; set; } = true;

        [Required]
        public string Server { get; set; } = string.Empty;

        [Required, Display(Name = "Database Hint")]
        public string Database { get; set; } = string.Empty;

        [Display(Name = "Windows authentication")]
        public bool IntegratedSecurity { get; set; }

        [Display(Name = "User ID")]
        public string? UserId { get; set; }

        [DataType(DataType.Password)]
        public string? Password { get; set; }

        [Display(Name = "Timeout (seconds)")]
        [Range(1, 300)]
        public int ConnectionTimeoutSeconds { get; set; } = 15;

        public bool Encrypt { get; set; } = true;

        [Display(Name = "Trust server certificate")]
        public bool TrustServerCertificate { get; set; } = true;

        public static InputModel FromConnection(SyncConnection connection, string databaseName) => new()
        {
            Id = connection.Id,
            DatabaseName = databaseName,
            Name = connection.Name,
            IsMain = connection.IsMain,
            IsEnabled = connection.IsEnabled,
            Server = connection.Server,
            Database = connection.Database,
            IntegratedSecurity = connection.IntegratedSecurity,
            UserId = connection.UserId,
            Password = string.Empty,
            ConnectionTimeoutSeconds = connection.ConnectionTimeoutSeconds,
            Encrypt = connection.Encrypt,
            TrustServerCertificate = connection.TrustServerCertificate
        };

        public void ApplyTo(SyncConnection connection)
        {
            connection.Name = Name.Trim();
            connection.IsMain = IsMain;
            connection.IsEnabled = IsEnabled;
            connection.Server = Server.Trim();
            connection.Database = Database.Trim();
            connection.IntegratedSecurity = IntegratedSecurity;
            connection.UserId = UserId?.Trim();
            connection.Password = Password;
            connection.ConnectionTimeoutSeconds = ConnectionTimeoutSeconds;
            connection.Encrypt = Encrypt;
            connection.TrustServerCertificate = TrustServerCertificate;
            connection.ApplicationName = "Coditium.DatabaseSync";
        }
    }

    private sealed class InstanceJson
    {
        public string? CreatedAtUtc { get; set; }
    }

    private sealed class SharedPortJson
    {
        public int Port { get; set; }
        public string? AdminUrl { get; set; }
    }
}
