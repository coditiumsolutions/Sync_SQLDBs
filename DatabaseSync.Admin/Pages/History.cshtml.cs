using DatabaseSync.Core.Interfaces;
using DatabaseSync.Core.Models.Management;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DatabaseSync.Admin.Pages;

public class HistoryModel : PageModel
{
    private readonly IManagementStore _store;

    public HistoryModel(IManagementStore store)
    {
        _store = store;
    }

    public string DatabasePath { get; private set; } = string.Empty;

    public IReadOnlyList<SyncRunRecord> SyncRuns { get; private set; } = [];

    public IReadOnlyList<AuditEventRecord> AuditEvents { get; private set; } = [];

    public IReadOnlyList<LoginAttemptRecord> LoginAttempts { get; private set; } = [];

    public SyncRunRecord? SelectedRun { get; private set; }

    public async Task OnGetAsync(long? runId)
    {
        DatabasePath = _store.DatabasePath;
        SyncRuns = await _store.ListSyncRunsAsync(40);
        AuditEvents = await _store.ListAuditEventsAsync(40);
        LoginAttempts = await _store.ListLoginAttemptsAsync(40);
        if (runId is long id)
        {
            SelectedRun = await _store.GetSyncRunAsync(id);
        }
    }
}
