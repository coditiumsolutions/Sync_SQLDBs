using System.Globalization;
using Dapper;
using DatabaseSync.Core.Interfaces;
using DatabaseSync.Core.Models.Management;
using DatabaseSync.Infrastructure.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DatabaseSync.Infrastructure.Management;

/// <summary>
/// Shared Synchronization projects DB — survives Active Database / product switches.
/// </summary>
public sealed class SqliteProjectStore : IProjectStore
{
    private readonly ILogger<SqliteProjectStore> _logger;
    private readonly string _filePath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _initialized;

    public SqliteProjectStore(
        IOptions<SynchronizationDbOptions> options,
        IOptions<SyncConfigStoreOptions> syncConfigOptions,
        ILogger<SqliteProjectStore> logger)
    {
        _logger = logger;
        var configured = options.Value.FilePath;
        if (Path.IsPathRooted(configured))
        {
            _filePath = configured;
        }
        else
        {
            var syncConfigPath = Path.IsPathRooted(syncConfigOptions.Value.FilePath)
                ? syncConfigOptions.Value.FilePath
                : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, syncConfigOptions.Value.FilePath));
            var instanceDir = Path.GetDirectoryName(syncConfigPath) ?? AppContext.BaseDirectory;
            var instancesRoot = Directory.GetParent(instanceDir)?.FullName ?? instanceDir;
            _filePath = Path.GetFullPath(Path.Combine(instancesRoot, configured));
        }
    }

    public string DatabasePath => _filePath;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return;
            }

            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await connection.ExecuteAsync(new CommandDefinition("""
                PRAGMA journal_mode=WAL;
                PRAGMA foreign_keys=ON;

                CREATE TABLE IF NOT EXISTS Projects (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Name TEXT NOT NULL COLLATE NOCASE UNIQUE,
                    ProjectHint TEXT NOT NULL,
                    IsActive INTEGER NOT NULL DEFAULT 1,
                    CreatedAtUtc TEXT NOT NULL,
                    UpdatedAtUtc TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS ProjectDatabases (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    ProjectId INTEGER NOT NULL,
                    DatabaseName TEXT NOT NULL,
                    DatabaseHint TEXT NOT NULL,
                    ServerIp TEXT NOT NULL,
                    UserId TEXT NULL,
                    Password TEXT NULL,
                    IsSource INTEGER NOT NULL DEFAULT 0,
                    IntegratedSecurity INTEGER NOT NULL DEFAULT 0,
                    Encrypt INTEGER NOT NULL DEFAULT 1,
                    TrustServerCertificate INTEGER NOT NULL DEFAULT 1,
                    ConnectionTimeoutSeconds INTEGER NOT NULL DEFAULT 15,
                    CreatedAtUtc TEXT NOT NULL,
                    UpdatedAtUtc TEXT NOT NULL,
                    FOREIGN KEY (ProjectId) REFERENCES Projects(Id) ON DELETE CASCADE
                );

                CREATE INDEX IF NOT EXISTS IX_Projects_Name ON Projects(Name);
                CREATE INDEX IF NOT EXISTS IX_ProjectDatabases_ProjectId ON ProjectDatabases(ProjectId);
                """, cancellationToken: cancellationToken)).ConfigureAwait(false);

            _initialized = true;
            _logger.LogInformation("Shared Synchronization SQLite ready at {Path}", _filePath);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<ProjectRecord>> ListProjectsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var projects = (await connection.QueryAsync<ProjectRow>(new CommandDefinition("""
            SELECT * FROM Projects ORDER BY Name COLLATE NOCASE;
            """, cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();

        var result = new List<ProjectRecord>(projects.Count);
        foreach (var project in projects)
        {
            var mapped = MapProject(project);
            mapped.Databases = await LoadProjectDatabasesAsync(connection, project.Id, cancellationToken).ConfigureAwait(false);
            result.Add(mapped);
        }

        return result;
    }

    public async Task<ProjectRecord?> GetProjectAsync(long id, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var row = await connection.QuerySingleOrDefaultAsync<ProjectRow>(new CommandDefinition("""
            SELECT * FROM Projects WHERE Id = @Id;
            """, new { Id = id }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        var project = MapProject(row);
        project.Databases = await LoadProjectDatabasesAsync(connection, id, cancellationToken).ConfigureAwait(false);
        return project;
    }

    public async Task<long> SaveProjectAsync(ProjectRecord project, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            var now = DateTimeOffset.UtcNow;
            long projectId;
            if (project.Id <= 0)
            {
                await connection.ExecuteAsync(new CommandDefinition("""
                    INSERT INTO Projects (Name, ProjectHint, IsActive, CreatedAtUtc, UpdatedAtUtc)
                    VALUES (@Name, @ProjectHint, @IsActive, @CreatedAtUtc, @UpdatedAtUtc);
                    """,
                    new
                    {
                        Name = project.Name.Trim(),
                        ProjectHint = project.ProjectHint?.Trim() ?? "",
                        IsActive = project.IsActive ? 1 : 0,
                        CreatedAtUtc = ToText(project.CreatedAtUtc == default ? now : project.CreatedAtUtc),
                        UpdatedAtUtc = ToText(now)
                    },
                    transaction: tx,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);

                projectId = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                    "SELECT last_insert_rowid();",
                    transaction: tx,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);
            }
            else
            {
                projectId = project.Id;
                await connection.ExecuteAsync(new CommandDefinition("""
                    UPDATE Projects
                    SET Name = @Name,
                        ProjectHint = @ProjectHint,
                        IsActive = @IsActive,
                        UpdatedAtUtc = @UpdatedAtUtc
                    WHERE Id = @Id;
                    """,
                    new
                    {
                        Id = projectId,
                        Name = project.Name.Trim(),
                        ProjectHint = project.ProjectHint?.Trim() ?? "",
                        IsActive = project.IsActive ? 1 : 0,
                        UpdatedAtUtc = ToText(now)
                    },
                    transaction: tx,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);

                await connection.ExecuteAsync(new CommandDefinition("""
                    DELETE FROM ProjectDatabases WHERE ProjectId = @ProjectId;
                    """,
                    new { ProjectId = projectId },
                    transaction: tx,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);
            }

            var sourceAssigned = false;
            foreach (var db in project.Databases ?? [])
            {
                var isSource = db.IsSource && !sourceAssigned;
                if (isSource)
                {
                    sourceAssigned = true;
                }

                await connection.ExecuteAsync(new CommandDefinition("""
                    INSERT INTO ProjectDatabases (
                        ProjectId, DatabaseName, DatabaseHint, ServerIp, UserId, Password,
                        IsSource, IntegratedSecurity, Encrypt, TrustServerCertificate,
                        ConnectionTimeoutSeconds, CreatedAtUtc, UpdatedAtUtc)
                    VALUES (
                        @ProjectId, @DatabaseName, @DatabaseHint, @ServerIp, @UserId, @Password,
                        @IsSource, @IntegratedSecurity, @Encrypt, @TrustServerCertificate,
                        @ConnectionTimeoutSeconds, @CreatedAtUtc, @UpdatedAtUtc);
                    """,
                    new
                    {
                        ProjectId = projectId,
                        DatabaseName = db.DatabaseName?.Trim() ?? "",
                        DatabaseHint = db.DatabaseHint?.Trim() ?? "",
                        ServerIp = db.ServerIp?.Trim() ?? "",
                        UserId = db.UserId?.Trim(),
                        Password = db.Password,
                        IsSource = isSource ? 1 : 0,
                        IntegratedSecurity = db.IntegratedSecurity ? 1 : 0,
                        Encrypt = db.Encrypt ? 1 : 0,
                        TrustServerCertificate = db.TrustServerCertificate ? 1 : 0,
                        ConnectionTimeoutSeconds = db.ConnectionTimeoutSeconds <= 0 ? 15 : db.ConnectionTimeoutSeconds,
                        CreatedAtUtc = ToText(db.CreatedAtUtc == default ? now : db.CreatedAtUtc),
                        UpdatedAtUtc = ToText(now)
                    },
                    transaction: tx,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);
            }

            if (!sourceAssigned && (project.Databases?.Count ?? 0) > 0)
            {
                await connection.ExecuteAsync(new CommandDefinition("""
                    UPDATE ProjectDatabases
                    SET IsSource = 1
                    WHERE Id = (
                        SELECT Id FROM ProjectDatabases WHERE ProjectId = @ProjectId ORDER BY Id LIMIT 1
                    );
                    """,
                    new { ProjectId = projectId },
                    transaction: tx,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);
            }

            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            return projectId;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteProjectAsync(long id, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition("""
            DELETE FROM Projects WHERE Id = @Id;
            """, new { Id = id }, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task SetProjectActiveAsync(long id, bool isActive, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE Projects SET IsActive = @IsActive, UpdatedAtUtc = @UpdatedAtUtc WHERE Id = @Id;
            """,
            new
            {
                Id = id,
                IsActive = isActive ? 1 : 0,
                UpdatedAtUtc = ToText(DateTimeOffset.UtcNow)
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task DeleteProjectDatabaseAsync(long projectDatabaseId, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition("""
            DELETE FROM ProjectDatabases WHERE Id = @Id;
            """, new { Id = projectDatabaseId }, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<long> UpsertProjectDatabaseAsync(ProjectDatabaseRecord database, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        if (database.ProjectId <= 0)
        {
            throw new ArgumentException("ProjectId is required.", nameof(database));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            var projectExists = await connection.ExecuteScalarAsync<long>(new CommandDefinition("""
                SELECT COUNT(1) FROM Projects WHERE Id = @Id;
                """, new { Id = database.ProjectId }, transaction: tx, cancellationToken: cancellationToken)).ConfigureAwait(false);
            if (projectExists <= 0)
            {
                throw new InvalidOperationException($"Project {database.ProjectId} was not found.");
            }

            var now = DateTimeOffset.UtcNow;
            if (database.IsSource)
            {
                await connection.ExecuteAsync(new CommandDefinition("""
                    UPDATE ProjectDatabases SET IsSource = 0 WHERE ProjectId = @ProjectId;
                    """, new { database.ProjectId }, transaction: tx, cancellationToken: cancellationToken)).ConfigureAwait(false);
            }

            long id;
            if (database.Id > 0)
            {
                var existingPassword = await connection.ExecuteScalarAsync<string?>(new CommandDefinition("""
                    SELECT Password FROM ProjectDatabases WHERE Id = @Id AND ProjectId = @ProjectId;
                    """, new { database.Id, database.ProjectId }, transaction: tx, cancellationToken: cancellationToken)).ConfigureAwait(false);

                var password = string.IsNullOrWhiteSpace(database.Password) ? existingPassword : database.Password;
                await connection.ExecuteAsync(new CommandDefinition("""
                    UPDATE ProjectDatabases
                    SET DatabaseName = @DatabaseName,
                        DatabaseHint = @DatabaseHint,
                        ServerIp = @ServerIp,
                        UserId = @UserId,
                        Password = @Password,
                        IsSource = @IsSource,
                        IntegratedSecurity = @IntegratedSecurity,
                        Encrypt = @Encrypt,
                        TrustServerCertificate = @TrustServerCertificate,
                        ConnectionTimeoutSeconds = @ConnectionTimeoutSeconds,
                        UpdatedAtUtc = @UpdatedAtUtc
                    WHERE Id = @Id AND ProjectId = @ProjectId;
                    """,
                    new
                    {
                        database.Id,
                        database.ProjectId,
                        DatabaseName = database.DatabaseName?.Trim() ?? "",
                        DatabaseHint = database.DatabaseHint?.Trim() ?? "",
                        ServerIp = database.ServerIp?.Trim() ?? "",
                        UserId = database.UserId?.Trim(),
                        Password = password,
                        IsSource = database.IsSource ? 1 : 0,
                        IntegratedSecurity = database.IntegratedSecurity ? 1 : 0,
                        Encrypt = database.Encrypt ? 1 : 0,
                        TrustServerCertificate = database.TrustServerCertificate ? 1 : 0,
                        ConnectionTimeoutSeconds = database.ConnectionTimeoutSeconds <= 0 ? 15 : database.ConnectionTimeoutSeconds,
                        UpdatedAtUtc = ToText(now)
                    },
                    transaction: tx,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);
                id = database.Id;
            }
            else
            {
                await connection.ExecuteAsync(new CommandDefinition("""
                    INSERT INTO ProjectDatabases (
                        ProjectId, DatabaseName, DatabaseHint, ServerIp, UserId, Password,
                        IsSource, IntegratedSecurity, Encrypt, TrustServerCertificate,
                        ConnectionTimeoutSeconds, CreatedAtUtc, UpdatedAtUtc)
                    VALUES (
                        @ProjectId, @DatabaseName, @DatabaseHint, @ServerIp, @UserId, @Password,
                        @IsSource, @IntegratedSecurity, @Encrypt, @TrustServerCertificate,
                        @ConnectionTimeoutSeconds, @CreatedAtUtc, @UpdatedAtUtc);
                    """,
                    new
                    {
                        database.ProjectId,
                        DatabaseName = database.DatabaseName?.Trim() ?? "",
                        DatabaseHint = database.DatabaseHint?.Trim() ?? "",
                        ServerIp = database.ServerIp?.Trim() ?? "",
                        UserId = database.UserId?.Trim(),
                        Password = database.Password,
                        IsSource = database.IsSource ? 1 : 0,
                        IntegratedSecurity = database.IntegratedSecurity ? 1 : 0,
                        Encrypt = database.Encrypt ? 1 : 0,
                        TrustServerCertificate = database.TrustServerCertificate ? 1 : 0,
                        ConnectionTimeoutSeconds = database.ConnectionTimeoutSeconds <= 0 ? 15 : database.ConnectionTimeoutSeconds,
                        CreatedAtUtc = ToText(now),
                        UpdatedAtUtc = ToText(now)
                    },
                    transaction: tx,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);

                id = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                    "SELECT last_insert_rowid();",
                    transaction: tx,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);
            }

            var sourceCount = await connection.ExecuteScalarAsync<long>(new CommandDefinition("""
                SELECT COUNT(1) FROM ProjectDatabases WHERE ProjectId = @ProjectId AND IsSource = 1;
                """, new { database.ProjectId }, transaction: tx, cancellationToken: cancellationToken)).ConfigureAwait(false);
            if (sourceCount == 0)
            {
                await connection.ExecuteAsync(new CommandDefinition("""
                    UPDATE ProjectDatabases
                    SET IsSource = 1
                    WHERE Id = (
                        SELECT Id FROM ProjectDatabases WHERE ProjectId = @ProjectId ORDER BY Id LIMIT 1
                    );
                    """, new { database.ProjectId }, transaction: tx, cancellationToken: cancellationToken)).ConfigureAwait(false);
            }

            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            return id;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        await InitializeAsync(cancellationToken).ConfigureAwait(false);
    }

    private SqliteConnection CreateConnection() =>
        new(new SqliteConnectionStringBuilder
        {
            DataSource = _filePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString());

    private static async Task<List<ProjectDatabaseRecord>> LoadProjectDatabasesAsync(
        SqliteConnection connection,
        long projectId,
        CancellationToken cancellationToken)
    {
        var rows = await connection.QueryAsync<ProjectDatabaseRow>(new CommandDefinition("""
            SELECT * FROM ProjectDatabases WHERE ProjectId = @ProjectId ORDER BY IsSource DESC, DatabaseName COLLATE NOCASE;
            """, new { ProjectId = projectId }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.Select(MapProjectDatabase).ToList();
    }

    private static ProjectRecord MapProject(ProjectRow row) => new()
    {
        Id = row.Id,
        Name = row.Name,
        ProjectHint = row.ProjectHint,
        IsActive = row.IsActive != 0,
        CreatedAtUtc = FromText(row.CreatedAtUtc),
        UpdatedAtUtc = FromText(row.UpdatedAtUtc)
    };

    private static ProjectDatabaseRecord MapProjectDatabase(ProjectDatabaseRow row) => new()
    {
        Id = row.Id,
        ProjectId = row.ProjectId,
        DatabaseName = row.DatabaseName,
        DatabaseHint = row.DatabaseHint,
        ServerIp = row.ServerIp,
        UserId = row.UserId,
        Password = row.Password,
        IsSource = row.IsSource != 0,
        IntegratedSecurity = row.IntegratedSecurity != 0,
        Encrypt = row.Encrypt != 0,
        TrustServerCertificate = row.TrustServerCertificate != 0,
        ConnectionTimeoutSeconds = row.ConnectionTimeoutSeconds,
        CreatedAtUtc = FromText(row.CreatedAtUtc),
        UpdatedAtUtc = FromText(row.UpdatedAtUtc)
    };

    private static string ToText(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset FromText(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private sealed class ProjectRow
    {
        public long Id { get; init; }
        public string Name { get; init; } = "";
        public string ProjectHint { get; init; } = "";
        public long IsActive { get; init; }
        public string CreatedAtUtc { get; init; } = "";
        public string UpdatedAtUtc { get; init; } = "";
    }

    private sealed class ProjectDatabaseRow
    {
        public long Id { get; init; }
        public long ProjectId { get; init; }
        public string DatabaseName { get; init; } = "";
        public string DatabaseHint { get; init; } = "";
        public string ServerIp { get; init; } = "";
        public string? UserId { get; init; }
        public string? Password { get; init; }
        public long IsSource { get; init; }
        public long IntegratedSecurity { get; init; }
        public long Encrypt { get; init; }
        public long TrustServerCertificate { get; init; }
        public int ConnectionTimeoutSeconds { get; init; }
        public string CreatedAtUtc { get; init; } = "";
        public string UpdatedAtUtc { get; init; } = "";
    }
}
