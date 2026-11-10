using DatabaseSync.Core.Interfaces;
using DatabaseSync.Core.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DatabaseSync.Admin.Pages.Connections;

public class IndexModel : PageModel
{
    private readonly ISyncConfigStore _configStore;
    private readonly IDatabaseConnectionService _connectionService;

    public IndexModel(ISyncConfigStore configStore, IDatabaseConnectionService connectionService)
    {
        _configStore = configStore;
        _connectionService = connectionService;
    }

    public List<SyncConnection> Connections { get; private set; } = [];

    public async Task OnGetAsync()
    {
        var config = await _configStore.GetAsync();
        Connections = config.Connections
            .OrderByDescending(c => c.IsMain)
            .ThenBy(c => c.Name)
            .ToList();
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
        TempData[result.IsSuccessful ? "StatusMessage" : "ErrorMessage"] =
            result.IsSuccessful
                ? $"Connected to {result.ConnectionName} ({result.Server}/{result.Database})."
                : $"Failed {result.ConnectionName}: {result.ErrorMessage}";
        return RedirectToPage();
    }
}
