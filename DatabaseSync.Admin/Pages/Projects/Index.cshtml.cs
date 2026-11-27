using DatabaseSync.Core.Interfaces;
using DatabaseSync.Core.Models.Management;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DatabaseSync.Admin.Pages.Projects;

public class IndexModel : PageModel
{
    private readonly IProjectStore _store;

    public IndexModel(IProjectStore store)
    {
        _store = store;
    }

    public IReadOnlyList<ProjectRecord> Projects { get; private set; } = [];

    public string DatabasePath { get; private set; } = string.Empty;

    public async Task OnGetAsync()
    {
        DatabasePath = _store.DatabasePath;
        Projects = await _store.ListProjectsAsync();
    }

    public async Task<IActionResult> OnPostSetActiveAsync(long id, bool isActive)
    {
        await _store.SetProjectActiveAsync(id, isActive);
        TempData["StatusMessage"] = isActive ? "Project set to Active." : "Project set to Inactive.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(long id)
    {
        await _store.DeleteProjectAsync(id);
        TempData["StatusMessage"] = "Project deleted.";
        return RedirectToPage();
    }
}
