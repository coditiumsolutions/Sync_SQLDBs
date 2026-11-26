using DatabaseSync.Admin.Services;
using DatabaseSync.Core.Interfaces;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DatabaseSync.Admin.Pages;

public class ConflictsModel : PageModel
{
    private readonly ISyncConfigStore _configStore;

    public ConflictsModel(ISyncConfigStore configStore)
    {
        _configStore = configStore;
    }

    public ConflictReview Review { get; private set; } = new();

    public async Task OnGetAsync()
    {
        var config = await _configStore.GetAsync();
        var folder = ConflictLogReview.ResolveFolder(
            _configStore.ConfigFilePath,
            config.SyncSettings.ConflictBackupFolder);
        Review = ConflictLogReview.Read(folder);
    }
}
