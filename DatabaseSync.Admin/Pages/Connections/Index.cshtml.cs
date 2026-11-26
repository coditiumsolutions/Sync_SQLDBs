using System.Collections.Concurrent;
using DatabaseSync.Core.Interfaces;
using DatabaseSync.Core.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DatabaseSync.Admin.Pages.Connections;

public class IndexModel : PageModel
{
    private static readonly ConcurrentDictionary<string, ConnectionTestStatus> LastTestResults = new(StringComparer.OrdinalIgnoreCase);

    private readonly ISyncConfigStore _configStore;
    private readonly IDatabaseConnectionService _connectionService;

    public IndexModel(ISyncConfigStore configStore, IDatabaseConnectionService connectionService)
    {
        _configStore = configStore;
        _connectionService = connectionService;
    }

    public List<SyncConnection> Connections { get; private set; } = [];

    public IReadOnlyDictionary<string, ConnectionTestStatus> TestStatuses { get; private set; } =
        new Dictionary<string, ConnectionTestStatus>(StringComparer.OrdinalIgnoreCase);

    public async Task OnGetAsync()
    {
        var config = await _configStore.GetAsync();
        Connections = config.Connections
            .OrderByDescending(c => c.IsMain)
            .ThenBy(c => c.Name)
            .ToList();

        var statuses = new Dictionary<string, ConnectionTestStatus>(StringComparer.OrdinalIgnoreCase);
        foreach (var connection in Connections)
        {
            if (LastTestResults.TryGetValue(connection.Id, out var status))
            {
                statuses[connection.Id] = status;
            }
        }

        TestStatuses = statuses;
    }

    public async Task<IActionResult> OnPostSetMainAsync(string id)
    {
        var config = await _configStore.GetAsync();
        var selected = config.Connections.FirstOrDefault(c => c.Id == id);
        if (selected is null)
        {
            TempData["ErrorMessage"] = "Connection not found.";
            return RedirectToPage();
        }

        foreach (var connection in config.Connections)
        {
            connection.IsMain = connection.Id == id;
        }

        await _configStore.SaveAsync(config);
        TempData["StatusMessage"] = $"'{selected.Name}' is now the Main (source) database.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(string id)
    {
        var config = await _configStore.GetAsync();
        var selected = config.Connections.FirstOrDefault(c => c.Id == id);
        if (selected is null)
        {
            TempData["ErrorMessage"] = "Connection not found.";
            return RedirectToPage();
        }

        config.Connections.RemoveAll(c => c.Id == id);
        if (selected.IsMain && config.Connections.Count > 0)
        {
            config.Connections[0].IsMain = true;
        }

        await _configStore.SaveAsync(config);
        TempData["StatusMessage"] = $"Deleted '{selected.Name}'.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostTestAsync(string id)
    {
        var result = await _connectionService.TestConnectionAsync(id);
        LastTestResults[id] = new ConnectionTestStatus
        {
            IsConnected = result.IsSuccessful,
            Detail = result.IsSuccessful
                ? $"{result.Server}/{result.Database}"
                : (result.ErrorMessage ?? "Connection failed."),
            CheckedAtUtc = result.CheckedAtUtc
        };

        TempData[result.IsSuccessful ? "StatusMessage" : "ErrorMessage"] =
            result.IsSuccessful
                ? $"Connected to {result.ConnectionName} ({result.Server}/{result.Database})."
                : $"Fail Connection — {result.ConnectionName}: {result.ErrorMessage}";
        return RedirectToPage();
    }

    public sealed class ConnectionTestStatus
    {
        public bool IsConnected { get; init; }
        public string Detail { get; init; } = "";
        public DateTimeOffset CheckedAtUtc { get; init; }
    }
}
