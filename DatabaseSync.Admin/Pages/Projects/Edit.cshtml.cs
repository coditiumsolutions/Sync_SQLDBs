using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using DatabaseSync.Admin.Services;
using DatabaseSync.Core.DTOs;
using DatabaseSync.Core.Interfaces;
using DatabaseSync.Core.Models;
using DatabaseSync.Core.Models.Management;
using DatabaseSync.Core.Scheduling;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.SqlClient;

namespace DatabaseSync.Admin.Pages.Projects;

public class EditModel : PageModel
{
    private static readonly ConcurrentDictionary<long, DatabaseTestStatus> LastTestResults = new();

    private readonly IProjectStore _projects;
    private readonly IManagementStore _management;
    private readonly ISyncConfigStore _configStore;
    private readonly IWorkerServiceControl _workerServiceControl;
    private readonly ISyncStatusStore _statusStore;
    private readonly ManualSyncHost _manualSyncHost;
    private readonly AuditService _audit;
    private readonly ISchemaDiscoveryService _schemaDiscovery;

    public EditModel(
        IProjectStore projects,
        IManagementStore management,
        ISyncConfigStore configStore,
        IWorkerServiceControl workerServiceControl,
        ISyncStatusStore statusStore,
        ManualSyncHost manualSyncHost,
        AuditService audit,
        ISchemaDiscoveryService schemaDiscovery)
    {
        _projects = projects;
        _management = management;
        _configStore = configStore;
        _workerServiceControl = workerServiceControl;
        _statusStore = statusStore;
        _manualSyncHost = manualSyncHost;
        _audit = audit;
        _schemaDiscovery = schemaDiscovery;
    }

    [BindProperty]
    public ProjectInput Input { get; set; } = new();

    [BindProperty]
    public DatabaseInput DbInput { get; set; } = new();

    public IReadOnlyList<ProjectDatabaseRecord> Databases { get; private set; } = [];

    public WorkerServiceStatusInfo ServiceStatus { get; private set; } = new();

    public SyncRuntimeStatus SyncStatus { get; private set; } = new();

    public IReadOnlyList<ServerSyncReport> ProjectSyncResults { get; private set; } = [];

    public IReadOnlyDictionary<long, DatabaseTestStatus> TestStatuses { get; private set; } =
        new Dictionary<long, DatabaseTestStatus>();

    public ProjectDatabaseRecord? SourceDatabase { get; private set; }

    public IReadOnlyList<TableCatalogEntry> ProjectTables { get; private set; } = [];

    public string? TablesLoadError { get; private set; }

    [BindProperty]
    public TableSelectionInput TableSelection { get; set; } = new();

    public bool IsNew => Input.Id <= 0;

    public bool ShowDbEditor { get; private set; }

    public IReadOnlyList<ProjectFailureRow> ProjectFailures { get; private set; } = [];

    public IReadOnlyList<SyncRunRecord> ProjectSyncRuns { get; private set; } = [];

    public IReadOnlyList<AuditEventRecord> ProjectAuditEvents { get; private set; } = [];

    public SyncRunRecord? SelectedHistoryRun { get; private set; }

    /// <summary>overview | sync-databases | sync-results | tables | history</summary>
    public string ActiveTab { get; private set; } = "overview";

    public async Task<IActionResult> OnGetAsync(long? id, long? editDb = null, bool addDb = false, string? tab = null, long? runId = null)
    {
        if (id is null or <= 0)
        {
            Input = new ProjectInput { IsActive = true };
            Databases = [];
            return Page();
        }

        if (!await LoadProjectAsync(id.Value))
        {
            TempData["ErrorMessage"] = "Project not found.";
            return RedirectToPage("./Index");
        }

        LoadSyncState();
        LoadTestStatuses();

        if (addDb)
        {
            ShowDbEditor = true;
            ActiveTab = "sync-databases";
            DbInput = new DatabaseInput
            {
                ProjectId = Input.Id,
                Encrypt = true,
                TrustServerCertificate = true,
                IsSource = Databases.Count == 0,
                ConnectionTimeoutSeconds = 15
            };
        }
        else if (editDb is > 0)
        {
            var row = Databases.FirstOrDefault(d => d.Id == editDb.Value);
            if (row is not null)
            {
                ShowDbEditor = true;
                ActiveTab = "sync-databases";
                DbInput = DatabaseInput.FromRecord(row);
            }
        }
        else if (string.Equals(tab, "sync-databases", StringComparison.OrdinalIgnoreCase))
        {
            ActiveTab = "sync-databases";
        }
        else if (string.Equals(tab, "sync-results", StringComparison.OrdinalIgnoreCase))
        {
            ActiveTab = "sync-results";
        }
        else if (string.Equals(tab, "tables", StringComparison.OrdinalIgnoreCase))
        {
            ActiveTab = "tables";
            await LoadProjectTablesAsync();
        }
        else if (string.Equals(tab, "history", StringComparison.OrdinalIgnoreCase)
                 || string.Equals(tab, "history-and-logs", StringComparison.OrdinalIgnoreCase))
        {
            ActiveTab = "history";
            await LoadProjectHistoryAsync(runId);
        }
        else if (string.Equals(tab, "overview", StringComparison.OrdinalIgnoreCase))
        {
            ActiveTab = "overview";
        }

        return Page();
    }

    public async Task<IActionResult> OnPostSaveTableSelectionAsync(long id)
    {
        if (id <= 0 || !await LoadProjectAsync(id))
        {
            TempData["ErrorMessage"] = "Project not found.";
            return RedirectToPage("./Index");
        }

        var config = await _configStore.GetAsync();
        config.SyncSettings.SyncAllTables = TableSelection.SyncAllTables;
        config.SyncSettings.SelectedTables = TableSelection.SyncAllTables
            ? []
            : (TableSelection.SelectedTables ?? [])
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Select(t => t.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
                .ToList();

        await _configStore.SaveAsync(config);

        await _management.SaveTableSelectionSnapshotAsync(new TableSelectionSnapshot
        {
            SyncAllTables = config.SyncSettings.SyncAllTables,
            SelectedTablesJson = JsonSerializer.Serialize(config.SyncSettings.SelectedTables ?? []),
            ChangedByUserName = User.Identity?.Name,
            CreatedAtUtc = DateTimeOffset.UtcNow
        });

        await _audit.WriteAsync(
            User,
            "SaveTableSelection",
            "Project",
            config.SyncSettings.SyncAllTables
                ? $"Sync all tables enabled from project '{Input.Name}'."
                : $"Selected {config.SyncSettings.SelectedTables.Count} table(s) from project '{Input.Name}'.");

        TempData["StatusMessage"] = TableSelection.SyncAllTables
            ? "Table selection saved. All syncable tables on Source will be synchronised."
            : $"Table selection saved. {config.SyncSettings.SelectedTables.Count} table(s) will be synchronised.";

        return RedirectToPage(new { id, tab = "tables" });
    }

    public async Task<IActionResult> OnPostTestDatabaseAsync(long projectId, long databaseId)
    {
        if (projectId <= 0 || databaseId <= 0 || !await LoadProjectAsync(projectId))
        {
            TempData["ErrorMessage"] = "Project not found.";
            return RedirectToPage("./Index");
        }

        var db = Databases.FirstOrDefault(d => d.Id == databaseId);
        if (db is null)
        {
            TempData["ErrorMessage"] = "Database not found on this project.";
            return RedirectToPage(new { id = projectId, tab = "sync-databases" });
        }

        var label = string.IsNullOrWhiteSpace(db.DatabaseHint) ? db.DatabaseName : db.DatabaseHint;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var cs = BuildProjectDatabaseConnectionString(db);
            await using var connection = new SqlConnection(cs);
            await connection.OpenAsync();
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT 1;";
            cmd.CommandTimeout = Math.Max(db.ConnectionTimeoutSeconds, 5);
            _ = await cmd.ExecuteScalarAsync();
            stopwatch.Stop();

            LastTestResults[databaseId] = new DatabaseTestStatus
            {
                IsConnected = true,
                Detail = $"{db.ServerIp}/{db.DatabaseName} ({stopwatch.ElapsedMilliseconds} ms)",
                CheckedAtUtc = DateTimeOffset.UtcNow
            };

            TempData["StatusMessage"] =
                $"Connected to {label} ({db.ServerIp}/{db.DatabaseName}).";
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            var detail = SanitizeErrorMessage(ex.Message);
            LastTestResults[databaseId] = new DatabaseTestStatus
            {
                IsConnected = false,
                Detail = detail,
                CheckedAtUtc = DateTimeOffset.UtcNow
            };

            TempData["ErrorMessage"] = $"Fail Connection — {label}: {detail}";
        }

        return RedirectToPage(new { id = projectId, tab = "sync-databases" });
    }

    public async Task<IActionResult> OnGetSyncStatusAsync(long id)
    {
        if (id <= 0 || !await LoadProjectAsync(id))
        {
            return new JsonResult(new { state = "Idle", servers = Array.Empty<object>() });
        }

        var status = _statusStore.Get();
        var settings = _configStore.Get().SyncSettings;
        var now = DateTimeOffset.UtcNow;
        var zone = TimeZoneInfo.Local;
        var grace = TimeSpan.FromMinutes(settings.ScheduleGraceMinutes);
        var servers = BuildProjectResults(Databases, status.Servers);

        return new JsonResult(new
        {
            status.State,
            status.Message,
            status.Trigger,
            status.ProgressTotal,
            status.ProgressCompleted,
            status.ProgressPercent,
            status.CurrentTargetName,
            status.CurrentTable,
            status.StartedAtUtc,
            status.FinishedAtUtc,
            NextScheduledRunUtc = settings.ScheduledSyncEnabled
                ? SyncSchedulePlanner.GetNextRunUtc(settings.Schedules, now, zone, grace, status.FiredScheduleOccurrences)
                : null,
            NextIntervalRunUtc = settings.IntervalSyncEnabled
                ? status.NextIntervalRunUtc ?? now
                : (DateTimeOffset?)null,
            Servers = servers,
            ScheduledSyncEnabled = settings.ScheduledSyncEnabled,
            IntervalSyncEnabled = settings.IntervalSyncEnabled
        });
    }

    public async Task<IActionResult> OnPostSyncNowAsync(long id)
    {
        if (id <= 0 || !await LoadProjectAsync(id))
        {
            TempData["ErrorMessage"] = "Project not found.";
            return RedirectToPage("./Index");
        }

        var userName = User.Identity?.Name;
        _manualSyncHost.Queue(userName);
        await _audit.WriteAsync(User, "SyncNow", "Project", $"Manual sync requested from project '{Input.Name}'.");
        TempData["StatusMessage"] =
            "Manual sync requested. It starts immediately unless another sync is running, in which case it runs once when that cycle finishes.";
        return RedirectToPage(new { id, tab = "sync-results" });
    }

    public async Task<IActionResult> OnPostStartServiceAsync(long id)
    {
        if (id <= 0 || !await LoadProjectAsync(id))
        {
            TempData["ErrorMessage"] = "Project not found.";
            return RedirectToPage("./Index");
        }

        await _statusStore.WriteAsync(new SyncRuntimeStatus
        {
            State = "Running",
            Message = "Sync service starting. Waiting for database synchronisation to complete...",
            StartedAtUtc = DateTimeOffset.UtcNow
        });

        var result = _workerServiceControl.Start();
        TempData[result.StatusText == "Error" ? "ErrorMessage" : "StatusMessage"] =
            result.StatusText != "Error"
                ? (result.Message ?? "Sync started.") + " Waiting for completion message..."
                : result.Message ?? $"Service status: {result.StatusText}";

        return RedirectToPage(new { id, tab = "sync-results" });
    }

    public async Task<IActionResult> OnPostStopServiceAsync(long id)
    {
        if (id <= 0 || !await LoadProjectAsync(id))
        {
            TempData["ErrorMessage"] = "Project not found.";
            return RedirectToPage("./Index");
        }

        var result = _workerServiceControl.Stop();
        await _statusStore.WriteAsync(new SyncRuntimeStatus
        {
            State = "Idle",
            Message = "Sync service stopped.",
            FinishedAtUtc = DateTimeOffset.UtcNow
        });

        TempData[result.StatusText == "Error" ? "ErrorMessage" : "StatusMessage"] =
            result.Message ?? $"Service status: {result.StatusText}";
        return RedirectToPage(new { id, tab = "sync-results" });
    }

    public async Task<IActionResult> OnPostSaveProjectAsync()
    {
        var name = (Input.Name ?? "").Trim();
        if (!Regex.IsMatch(name, @"^[A-Za-z0-9_\- ]+$") || string.IsNullOrWhiteSpace(name))
        {
            if (Input.Id > 0)
            {
                await LoadProjectAsync(Input.Id);
                LoadSyncState();
            }

            ModelState.AddModelError("Input.Name", "Project Name is required.");
            return Page();
        }

        try
        {
            var project = new ProjectRecord
            {
                Id = Input.Id,
                Name = name,
                ProjectHint = Input.ProjectHint?.Trim() ?? "",
                IsActive = Input.IsActive,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                Databases = []
            };

            if (Input.Id > 0)
            {
                var existing = await _projects.GetProjectAsync(Input.Id);
                project.Databases = existing?.Databases?.ToList() ?? [];
            }

            var id = await _projects.SaveProjectAsync(project);
            TempData["StatusMessage"] = Input.Id <= 0
                ? $"Project '{name}' created. Now add databases."
                : $"Project '{name}' updated.";
            return RedirectToPage(new { id, addDb = Input.Id <= 0 ? true : (bool?)null });
        }
        catch (Exception ex)
        {
            if (Input.Id > 0)
            {
                await LoadProjectAsync(Input.Id);
                LoadSyncState();
            }

            ModelState.AddModelError(string.Empty, "Could not save project: " + ex.Message);
            return Page();
        }
    }

    public async Task<IActionResult> OnPostSaveDatabaseAsync()
    {
        if (DbInput.ProjectId <= 0)
        {
            TempData["ErrorMessage"] = "Save the project first, then add databases.";
            return RedirectToPage("./Edit");
        }

        if (!await LoadProjectAsync(DbInput.ProjectId))
        {
            TempData["ErrorMessage"] = "Project not found.";
            return RedirectToPage("./Index");
        }

        if (string.IsNullOrWhiteSpace(DbInput.DatabaseName) || string.IsNullOrWhiteSpace(DbInput.ServerIp))
        {
            ShowDbEditor = true;
            ActiveTab = "sync-databases";
            LoadSyncState();
            LoadTestStatuses();
            ModelState.AddModelError(string.Empty, "Database Name and Server IP are required.");
            return Page();
        }

        try
        {
            var record = DbInput.ToRecord();
            record.ProjectId = DbInput.ProjectId;
            await _projects.UpsertProjectDatabaseAsync(record);
            TempData["StatusMessage"] = DbInput.Id > 0
                ? $"Database '{record.DatabaseName}' updated on project '{Input.Name}'."
                : $"Database '{record.DatabaseName}' attached to project '{Input.Name}'.";
            return RedirectToPage(new { id = DbInput.ProjectId, tab = "sync-databases" });
        }
        catch (Exception ex)
        {
            ShowDbEditor = true;
            ActiveTab = "sync-databases";
            LoadSyncState();
            LoadTestStatuses();
            ModelState.AddModelError(string.Empty, "Could not save database: " + ex.Message);
            return Page();
        }
    }

    public async Task<IActionResult> OnPostRemoveDatabaseAsync(long projectId, long databaseId)
    {
        if (projectId <= 0 || databaseId <= 0)
        {
            return RedirectToPage("./Index");
        }

        await _projects.DeleteProjectDatabaseAsync(databaseId);
        TempData["StatusMessage"] = "Database removed from project.";
        return RedirectToPage(new { id = projectId, tab = "sync-databases" });
    }

    private async Task<bool> LoadProjectAsync(long id)
    {
        var project = await _projects.GetProjectAsync(id);
        if (project is null)
        {
            return false;
        }

        Input = new ProjectInput
        {
            Id = project.Id,
            Name = project.Name,
            ProjectHint = project.ProjectHint,
            IsActive = project.IsActive
        };
        Databases = project.Databases;
        return true;
    }

    private void LoadSyncState()
    {
        ServiceStatus = _workerServiceControl.GetStatus();
        SyncStatus = _statusStore.Get();
        ProjectSyncResults = BuildProjectResults(Databases, SyncStatus.Servers);
    }

    private void LoadTestStatuses()
    {
        var statuses = new Dictionary<long, DatabaseTestStatus>();
        foreach (var db in Databases)
        {
            if (LastTestResults.TryGetValue(db.Id, out var status))
            {
                statuses[db.Id] = status;
            }
        }

        TestStatuses = statuses;
    }

    private async Task LoadProjectHistoryAsync(long? runId)
    {
        LoadSyncState();
        ProjectFailures = BuildProjectFailures(Databases, SyncStatus);

        var recent = await _management.ListSyncRunsAsync(40);
        var matched = new List<SyncRunRecord>();
        foreach (var summary in recent)
        {
            var full = await _management.GetSyncRunAsync(summary.Id);
            if (full is null)
            {
                continue;
            }

            if (IsRunRelatedToProject(full, Databases))
            {
                matched.Add(full);
            }
        }

        ProjectSyncRuns = matched;

        var projectName = Input.Name?.Trim() ?? "";
        var audits = await _management.ListAuditEventsAsync(80);
        ProjectAuditEvents = audits
            .Where(a =>
                string.Equals(a.Category, "Project", StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrWhiteSpace(projectName) &&
                 (a.Detail?.Contains(projectName, StringComparison.OrdinalIgnoreCase) ?? false)))
            .Take(40)
            .ToList();

        if (runId is > 0)
        {
            SelectedHistoryRun = matched.FirstOrDefault(r => r.Id == runId.Value)
                                 ?? await _management.GetSyncRunAsync(runId.Value);
            if (SelectedHistoryRun is not null && !IsRunRelatedToProject(SelectedHistoryRun, Databases))
            {
                SelectedHistoryRun = null;
            }
        }
    }

    private static IReadOnlyList<ProjectFailureRow> BuildProjectFailures(
        IReadOnlyList<ProjectDatabaseRecord> databases,
        SyncRuntimeStatus status)
    {
        var rows = new List<ProjectFailureRow>();
        if (status.State is "Failed" or "Skipped" && !string.IsNullOrWhiteSpace(status.Message))
        {
            rows.Add(new ProjectFailureRow
            {
                Scope = "Sync cycle",
                Name = status.Trigger ?? "Sync",
                Outcome = status.State,
                Reason = status.Message ?? "Sync was not successful."
            });
        }

        foreach (var report in BuildProjectResults(databases, status.Servers))
        {
            if (report.Outcome is "Synced" or "Source" or "Connected" or "Pending")
            {
                continue;
            }

            rows.Add(new ProjectFailureRow
            {
                Scope = report.IsMain ? "Source" : "Target",
                Name = report.Name,
                Outcome = report.Outcome,
                Reason = string.IsNullOrWhiteSpace(report.Reason)
                    ? "Sync was not successful."
                    : report.Reason
            });
        }

        return rows;
    }

    private static bool IsRunRelatedToProject(SyncRunRecord run, IReadOnlyList<ProjectDatabaseRecord> databases)
    {
        if (databases.Count == 0)
        {
            return false;
        }

        if (run.Servers.Count == 0)
        {
            // Fallback: keep failed runs when project has DBs on this product instance.
            return !string.Equals(run.State, "Succeeded", StringComparison.OrdinalIgnoreCase);
        }

        return run.Servers.Any(s => databases.Any(d =>
            string.Equals(s.Server?.Trim(), d.ServerIp?.Trim(), StringComparison.OrdinalIgnoreCase) &&
            string.Equals(s.DatabaseName?.Trim(), d.DatabaseName?.Trim(), StringComparison.OrdinalIgnoreCase)));
    }

    private async Task LoadProjectTablesAsync()
    {
        SourceDatabase = Databases.FirstOrDefault(d => d.IsSource) ?? Databases.FirstOrDefault();
        var config = await _configStore.GetAsync();
        TableSelection.SyncAllTables = config.SyncSettings.SyncAllTables;
        TableSelection.SelectedTables = (config.SyncSettings.SelectedTables ?? []).ToList();

        if (SourceDatabase is null)
        {
            TablesLoadError = "No Source database on this project. Add a DB under Sync Databases and nominate it as Source.";
            ProjectTables = [];
            return;
        }

        try
        {
            var matched = FindMatchingSyncConnection(config, SourceDatabase);
            if (matched is not null)
            {
                ProjectTables = await _schemaDiscovery.ListTablesAsync(matched.Id);
            }
            else
            {
                ProjectTables = await ListTablesFromProjectDatabaseAsync(SourceDatabase);
            }

            if (TableSelection.SyncAllTables)
            {
                TableSelection.SelectedTables = ProjectTables
                    .Where(t => t.HasPrimaryKey)
                    .Select(t => t.Key)
                    .ToList();
            }
        }
        catch (Exception ex)
        {
            TablesLoadError =
                $"Could not load tables from Source ({SourceDatabase.ServerIp} / {SourceDatabase.DatabaseName}): {ex.Message}";
            ProjectTables = [];
        }
    }

    private static SyncConnection? FindMatchingSyncConnection(SyncConfiguration config, ProjectDatabaseRecord db) =>
        config.Connections.FirstOrDefault(c =>
            string.Equals(c.Server?.Trim(), db.ServerIp?.Trim(), StringComparison.OrdinalIgnoreCase) &&
            string.Equals(c.Database?.Trim(), db.DatabaseName?.Trim(), StringComparison.OrdinalIgnoreCase));

    private static async Task<IReadOnlyList<TableCatalogEntry>> ListTablesFromProjectDatabaseAsync(
        ProjectDatabaseRecord db)
    {
        var cs = BuildProjectDatabaseConnectionString(db);
        await using var connection = new SqlConnection(cs);
        await connection.OpenAsync();

        const string sql = """
            SELECT
                s.name AS SchemaName,
                t.name AS TableName,
                CASE WHEN pk.object_id IS NULL THEN CAST(0 AS bit) ELSE CAST(1 AS bit) END AS HasPrimaryKey,
                CAST(ISNULL(SUM(p.rows), 0) AS bigint) AS ApproximateRowCount
            FROM sys.tables t
            INNER JOIN sys.schemas s ON s.schema_id = t.schema_id
            LEFT JOIN sys.indexes pk
                ON pk.object_id = t.object_id AND pk.is_primary_key = 1
            LEFT JOIN sys.partitions p
                ON p.object_id = t.object_id AND p.index_id IN (0, 1)
            WHERE t.is_ms_shipped = 0
              AND s.name NOT IN (N'sync')
              AND t.name NOT LIKE '\_\_Sync%' ESCAPE '\'
              AND t.name NOT LIKE 'ConflictBackup[_]%'
            GROUP BY s.name, t.name, pk.object_id
            ORDER BY s.name, t.name;
            """;

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = Math.Max(db.ConnectionTimeoutSeconds, 30);

        var rows = new List<TableCatalogEntry>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new TableCatalogEntry
            {
                SchemaName = reader.GetString(0),
                TableName = reader.GetString(1),
                HasPrimaryKey = reader.GetBoolean(2),
                ApproximateRowCount = reader.GetInt64(3)
            });
        }

        return rows;
    }

    private static string BuildProjectDatabaseConnectionString(ProjectDatabaseRecord db)
    {
        if (string.IsNullOrWhiteSpace(db.ServerIp))
        {
            throw new InvalidOperationException("Server IP is not configured.");
        }

        if (string.IsNullOrWhiteSpace(db.DatabaseName))
        {
            throw new InvalidOperationException("Database name is not configured.");
        }

        var builder = new SqlConnectionStringBuilder
        {
            DataSource = db.ServerIp.Trim(),
            InitialCatalog = db.DatabaseName.Trim(),
            ConnectTimeout = db.ConnectionTimeoutSeconds <= 0 ? 15 : db.ConnectionTimeoutSeconds,
            Encrypt = db.Encrypt,
            TrustServerCertificate = db.TrustServerCertificate,
            ApplicationName = "CoditiumSync-ProjectTest",
            PersistSecurityInfo = false
        };

        if (db.IntegratedSecurity)
        {
            builder.IntegratedSecurity = true;
        }
        else
        {
            builder.IntegratedSecurity = false;
            builder.UserID = db.UserId ?? string.Empty;
            builder.Password = db.Password ?? string.Empty;
        }

        return builder.ConnectionString;
    }

    private static string SanitizeErrorMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return "An unknown database error occurred.";
        }

        return message
            .Replace("Password=", "Password=***;", StringComparison.OrdinalIgnoreCase)
            .Replace("Pwd=", "Pwd=***;", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<ServerSyncReport> BuildProjectResults(
        IReadOnlyList<ProjectDatabaseRecord> databases,
        IReadOnlyList<ServerSyncReport> syncServers)
    {
        if (databases.Count == 0)
        {
            return [];
        }

        var results = new List<ServerSyncReport>(databases.Count);
        foreach (var db in databases)
        {
            var match = syncServers.FirstOrDefault(s =>
                string.Equals(s.Server?.Trim(), db.ServerIp?.Trim(), StringComparison.OrdinalIgnoreCase) &&
                string.Equals(s.Database?.Trim(), db.DatabaseName?.Trim(), StringComparison.OrdinalIgnoreCase));

            if (match is not null)
            {
                results.Add(new ServerSyncReport
                {
                    ConnectionId = match.ConnectionId,
                    Name = string.IsNullOrWhiteSpace(db.DatabaseHint) ? db.DatabaseName : db.DatabaseHint,
                    Server = db.ServerIp,
                    Database = db.DatabaseName,
                    IsMain = db.IsSource,
                    Outcome = match.Outcome,
                    Reason = match.Reason,
                    TablesOk = match.TablesOk,
                    TablesFailed = match.TablesFailed
                });
            }
            else
            {
                results.Add(new ServerSyncReport
                {
                    ConnectionId = $"project-db-{db.Id}",
                    Name = string.IsNullOrWhiteSpace(db.DatabaseHint) ? db.DatabaseName : db.DatabaseHint,
                    Server = db.ServerIp,
                    Database = db.DatabaseName,
                    IsMain = db.IsSource,
                    Outcome = "Pending",
                    Reason = "No sync result yet for this project database."
                });
            }
        }

        return results;
    }

    public sealed class DatabaseTestStatus
    {
        public bool IsConnected { get; init; }

        public string Detail { get; init; } = "";

        public DateTimeOffset CheckedAtUtc { get; init; }
    }

    public sealed class TableSelectionInput
    {
        [Display(Name = "Synchronise all syncable tables")]
        public bool SyncAllTables { get; set; } = true;

        public List<string> SelectedTables { get; set; } = [];
    }

    public sealed class ProjectFailureRow
    {
        public string Scope { get; init; } = "";

        public string Name { get; init; } = "";

        public string Outcome { get; init; } = "";

        public string Reason { get; init; } = "";
    }

    public sealed class ProjectInput
    {
        public long Id { get; set; }

        [Required, Display(Name = "Project Name")]
        public string Name { get; set; } = string.Empty;

        [Display(Name = "Project Hint")]
        public string ProjectHint { get; set; } = string.Empty;

        [Display(Name = "ActiveStatus")]
        public bool IsActive { get; set; } = true;
    }

    public sealed class DatabaseInput
    {
        public long Id { get; set; }

        public long ProjectId { get; set; }

        [Display(Name = "Database Name")]
        public string DatabaseName { get; set; } = string.Empty;

        [Display(Name = "Database Hint")]
        public string DatabaseHint { get; set; } = string.Empty;

        [Display(Name = "Server IP")]
        public string ServerIp { get; set; } = string.Empty;

        [Display(Name = "User ID")]
        public string? UserId { get; set; }

        [DataType(DataType.Password)]
        public string? Password { get; set; }

        [Display(Name = "Nominate as Source")]
        public bool IsSource { get; set; }

        [Display(Name = "Windows authentication")]
        public bool IntegratedSecurity { get; set; }

        public bool Encrypt { get; set; } = true;

        [Display(Name = "Trust server certificate")]
        public bool TrustServerCertificate { get; set; } = true;

        [Display(Name = "Timeout (seconds)")]
        public int ConnectionTimeoutSeconds { get; set; } = 15;

        public static DatabaseInput FromRecord(ProjectDatabaseRecord record) => new()
        {
            Id = record.Id,
            ProjectId = record.ProjectId,
            DatabaseName = record.DatabaseName,
            DatabaseHint = record.DatabaseHint,
            ServerIp = record.ServerIp,
            UserId = record.UserId,
            Password = string.Empty,
            IsSource = record.IsSource,
            IntegratedSecurity = record.IntegratedSecurity,
            Encrypt = record.Encrypt,
            TrustServerCertificate = record.TrustServerCertificate,
            ConnectionTimeoutSeconds = record.ConnectionTimeoutSeconds
        };

        public ProjectDatabaseRecord ToRecord() => new()
        {
            Id = Id,
            ProjectId = ProjectId,
            DatabaseName = DatabaseName.Trim(),
            DatabaseHint = DatabaseHint?.Trim() ?? "",
            ServerIp = ServerIp.Trim(),
            UserId = UserId?.Trim(),
            Password = Password,
            IsSource = IsSource,
            IntegratedSecurity = IntegratedSecurity,
            Encrypt = Encrypt,
            TrustServerCertificate = TrustServerCertificate,
            ConnectionTimeoutSeconds = ConnectionTimeoutSeconds <= 0 ? 15 : ConnectionTimeoutSeconds,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
    }
}
