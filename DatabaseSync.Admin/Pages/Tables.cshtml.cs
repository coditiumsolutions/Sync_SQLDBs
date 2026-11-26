using System.Text.Json;
using DatabaseSync.Admin.Services;
using DatabaseSync.Core.DTOs;
using DatabaseSync.Core.Interfaces;
using DatabaseSync.Core.Models;
using DatabaseSync.Core.Models.Management;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DatabaseSync.Admin.Pages;

public class TablesModel : PageModel
{
    private readonly ISyncConfigStore _configStore;
    private readonly ISchemaDiscoveryService _schemaDiscovery;
    private readonly IManagementStore _managementStore;
    private readonly AuditService _audit;

    public TablesModel(
        ISyncConfigStore configStore,
        ISchemaDiscoveryService schemaDiscovery,
        IManagementStore managementStore,
        AuditService audit)
    {
        _configStore = configStore;
        _schemaDiscovery = schemaDiscovery;
        _managementStore = managementStore;
        _audit = audit;
    }

    public string ConfigPath { get; private set; } = string.Empty;

    public SyncConnection? MainConnection { get; private set; }

    public IReadOnlyList<TableCatalogEntry> Tables { get; private set; } = [];

    public string? LoadError { get; private set; }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public async Task OnGetAsync()
    {
        await LoadAsync();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var config = await _configStore.GetAsync();
        config.SyncSettings.SyncAllTables = Input.SyncAllTables;
        config.SyncSettings.SelectedTables = Input.SyncAllTables
            ? []
            : (Input.SelectedTables ?? [])
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Select(t => t.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
                .ToList();

        await _configStore.SaveAsync(config);

        var selectedJson = JsonSerializer.Serialize(config.SyncSettings.SelectedTables ?? []);
        await _managementStore.SaveTableSelectionSnapshotAsync(new TableSelectionSnapshot
        {
            SyncAllTables = config.SyncSettings.SyncAllTables,
            SelectedTablesJson = selectedJson,
            ChangedByUserName = User.Identity?.Name,
            CreatedAtUtc = DateTimeOffset.UtcNow
        });

        await _audit.WriteAsync(
            User,
            "SaveTableSelection",
            "Tables",
            config.SyncSettings.SyncAllTables
                ? "Sync all tables enabled."
                : $"Selected {config.SyncSettings.SelectedTables.Count} table(s).");

        TempData["StatusMessage"] = Input.SyncAllTables
            ? "Table selection saved. All syncable tables on Main will be synchronised."
            : $"Table selection saved. {config.SyncSettings.SelectedTables.Count} table(s) will be synchronised.";

        return RedirectToPage();
    }

    private async Task LoadAsync()
    {
        var config = await _configStore.GetAsync();
        ConfigPath = _configStore.ConfigFilePath;
        MainConnection = config.Connections.FirstOrDefault(c => c.IsMain && c.IsEnabled)
                         ?? config.Connections.FirstOrDefault(c => c.IsMain);

        Input.SyncAllTables = config.SyncSettings.SyncAllTables;
        Input.SelectedTables = (config.SyncSettings.SelectedTables ?? []).ToList();

        if (MainConnection is null)
        {
            LoadError = "No Main connection is nominated. Set Main under Connections first.";
            return;
        }

        try
        {
            Tables = await _schemaDiscovery.ListTablesAsync(MainConnection.Id);
            if (Input.SyncAllTables)
            {
                // Show every syncable table as checked while "all tables" mode is on.
                Input.SelectedTables = Tables
                    .Where(t => t.HasPrimaryKey)
                    .Select(t => t.Key)
                    .ToList();
            }
        }
        catch (Exception ex)
        {
            LoadError = $"Could not load tables from Main ({MainConnection.Name}): {ex.Message}";
            Tables = [];
        }
    }

    public sealed class InputModel
    {
        public bool SyncAllTables { get; set; } = true;

        public List<string> SelectedTables { get; set; } = [];
    }
}
