using System.Globalization;
using Dapper;
using DatabaseSync.Core.Interfaces;
using DatabaseSync.Core.Models.Management;
using DatabaseSync.Infrastructure.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DatabaseSync.Infrastructure.Management;

public sealed class SqliteManagementStore : IManagementStore
{
    private readonly ILogger<SqliteManagementStore> _logger;
    private readonly string _filePath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _initialized;

    public SqliteManagementStore(
        IOptions<ManagementDbOptions> options,
        IOptions<SyncConfigStoreOptions> syncConfigOptions,
        ILogger<SqliteManagementStore> logger)
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
            var directory = Path.GetDirectoryName(syncConfigPath) ?? AppContext.BaseDirectory;
            _filePath = Path.GetFullPath(Path.Combine(directory, configured));
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

                CREATE TABLE IF NOT EXISTS Users (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    UserName TEXT NOT NULL COLLATE NOCASE UNIQUE,
                    DisplayName TEXT NOT NULL,
                    PasswordHash TEXT NOT NULL,
                    PasswordSalt TEXT NOT NULL,
                    Role TEXT NOT NULL,
                    IsActive INTEGER NOT NULL DEFAULT 1,
                    CreatedAtUtc TEXT NOT NULL,
                    UpdatedAtUtc TEXT NOT NULL,
                    LastLoginAtUtc TEXT NULL
                );

                CREATE TABLE IF NOT EXISTS LoginAttempts (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    UserName TEXT NOT NULL,
                    Succeeded INTEGER NOT NULL,
                    IpAddress TEXT NULL,
                    UserAgent TEXT NULL,
                    Detail TEXT NULL,
                    CreatedAtUtc TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS AuditEvents (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    UserId INTEGER NULL,
                    UserName TEXT NULL,
                    Action TEXT NOT NULL,
                    Category TEXT NOT NULL,
                    Detail TEXT NULL,
                    CreatedAtUtc TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS SyncRuns (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    StartedAtUtc TEXT NOT NULL,
                    FinishedAtUtc TEXT NULL,
                    Trigger TEXT NOT NULL,
                    State TEXT NOT NULL,
                    DryRun INTEGER NOT NULL,
                    MainConnectionName TEXT NULL,
                    Message TEXT NULL,
                    SuccessfulTables INTEGER NOT NULL DEFAULT 0,
                    FailedTables INTEGER NOT NULL DEFAULT 0,
                    RowsInserted INTEGER NOT NULL DEFAULT 0,
                    RowsUpdated INTEGER NOT NULL DEFAULT 0,
                    RowsDeleted INTEGER NOT NULL DEFAULT 0,
                    RowsConflicted INTEGER NOT NULL DEFAULT 0,
                    EstimatedBytesTransferred INTEGER NOT NULL DEFAULT 0,
                    RequestedByUserName TEXT NULL
                );

                CREATE TABLE IF NOT EXISTS SyncRunServers (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    SyncRunId INTEGER NOT NULL,
                    ConnectionId TEXT NULL,
                    Name TEXT NOT NULL,
                    Server TEXT NULL,
                    DatabaseName TEXT NULL,
                    IsMain INTEGER NOT NULL,
                    Outcome TEXT NOT NULL,
                    Reason TEXT NULL,
                    TablesOk INTEGER NOT NULL DEFAULT 0,
                    TablesFailed INTEGER NOT NULL DEFAULT 0,
                    FOREIGN KEY (SyncRunId) REFERENCES SyncRuns(Id) ON DELETE CASCADE
                );

                CREATE TABLE IF NOT EXISTS SyncRunTables (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    SyncRunId INTEGER NOT NULL,
                    SchemaName TEXT NOT NULL,
                    TableName TEXT NOT NULL,
                    TargetConnectionId TEXT NULL,
                    TargetConnectionName TEXT NULL,
                    Success INTEGER NOT NULL,
                    Skipped INTEGER NOT NULL,
                    RowsInserted INTEGER NOT NULL DEFAULT 0,
                    RowsUpdated INTEGER NOT NULL DEFAULT 0,
                    RowsDeleted INTEGER NOT NULL DEFAULT 0,
                    RowsConflicted INTEGER NOT NULL DEFAULT 0,
                    EstimatedBytesTransferred INTEGER NOT NULL DEFAULT 0,
                    ElapsedMs INTEGER NOT NULL DEFAULT 0,
                    ErrorMessage TEXT NULL,
                    FOREIGN KEY (SyncRunId) REFERENCES SyncRuns(Id) ON DELETE CASCADE
                );

                CREATE TABLE IF NOT EXISTS TableSelectionSnapshots (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    SyncAllTables INTEGER NOT NULL,
                    SelectedTablesJson TEXT NOT NULL,
                    ChangedByUserName TEXT NULL,
                    CreatedAtUtc TEXT NOT NULL
                );

                CREATE INDEX IF NOT EXISTS IX_LoginAttempts_CreatedAtUtc ON LoginAttempts(CreatedAtUtc);
                CREATE INDEX IF NOT EXISTS IX_AuditEvents_CreatedAtUtc ON AuditEvents(CreatedAtUtc);
                CREATE INDEX IF NOT EXISTS IX_SyncRuns_StartedAtUtc ON SyncRuns(StartedAtUtc);
                """, cancellationToken: cancellationToken)).ConfigureAwait(false);

            var userCount = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT COUNT(1) FROM Users;",
                cancellationToken: cancellationToken)).ConfigureAwait(false);

            if (userCount == 0)
            {
                var now = DateTimeOffset.UtcNow;
                var (hash, salt) = PasswordHasher.HashPassword("Admin@123");
                await connection.ExecuteAsync(new CommandDefinition("""
                    INSERT INTO Users (UserName, DisplayName, PasswordHash, PasswordSalt, Role, IsActive, CreatedAtUtc, UpdatedAtUtc)
                    VALUES (@UserName, @DisplayName, @PasswordHash, @PasswordSalt, @Role, 1, @CreatedAtUtc, @UpdatedAtUtc);
                    """,
                    new
                    {
                        UserName = "admin",
                        DisplayName = "Administrator",
                        PasswordHash = hash,
                        PasswordSalt = salt,
                        Role = "Admin",
                        CreatedAtUtc = ToText(now),
                        UpdatedAtUtc = ToText(now)
                    },
                    cancellationToken: cancellationToken)).ConfigureAwait(false);

                await connection.ExecuteAsync(new CommandDefinition("""
                    INSERT INTO AuditEvents (UserId, UserName, Action, Category, Detail, CreatedAtUtc)
                    VALUES (NULL, 'system', 'SeedDefaultAdmin', 'Users', 'Created default admin user (admin). Change the password after first login.', @CreatedAtUtc);
                    """,
                    new { CreatedAtUtc = ToText(now) },
                    cancellationToken: cancellationToken)).ConfigureAwait(false);

                _logger.LogInformation(
                    "Seeded default management admin user 'admin' in {DatabasePath}. Change the password after first login.",
                    _filePath);
            }

            _initialized = true;
            _logger.LogInformation("Management SQLite ready at {DatabasePath}", _filePath);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<AppUser?> GetUserByUserNameAsync(string userName, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var row = await connection.QuerySingleOrDefaultAsync<UserRow>(new CommandDefinition("""
            SELECT * FROM Users WHERE UserName = @UserName COLLATE NOCASE LIMIT 1;
            """, new { UserName = userName }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return row is null ? null : MapUser(row);
    }

    public async Task<AppUser?> GetUserByIdAsync(long id, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var row = await connection.QuerySingleOrDefaultAsync<UserRow>(new CommandDefinition("""
            SELECT * FROM Users WHERE Id = @Id LIMIT 1;
            """, new { Id = id }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return row is null ? null : MapUser(row);
    }

    public async Task<IReadOnlyList<AppUser>> ListUsersAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<UserRow>(new CommandDefinition("""
            SELECT * FROM Users ORDER BY UserName COLLATE NOCASE;
            """, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.Select(MapUser).ToList();
    }

    public async Task<long> CreateUserAsync(AppUser user, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var id = await connection.ExecuteScalarAsync<long>(new CommandDefinition("""
            INSERT INTO Users (UserName, DisplayName, PasswordHash, PasswordSalt, Role, IsActive, CreatedAtUtc, UpdatedAtUtc, LastLoginAtUtc)
            VALUES (@UserName, @DisplayName, @PasswordHash, @PasswordSalt, @Role, @IsActive, @CreatedAtUtc, @UpdatedAtUtc, @LastLoginAtUtc);
            SELECT last_insert_rowid();
            """,
            new
            {
                user.UserName,
                user.DisplayName,
                user.PasswordHash,
                user.PasswordSalt,
                user.Role,
                IsActive = user.IsActive ? 1 : 0,
                CreatedAtUtc = ToText(user.CreatedAtUtc),
                UpdatedAtUtc = ToText(user.UpdatedAtUtc),
                LastLoginAtUtc = ToText(user.LastLoginAtUtc)
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return id;
    }

    public async Task UpdateUserAsync(AppUser user, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE Users
            SET DisplayName = @DisplayName,
                PasswordHash = @PasswordHash,
                PasswordSalt = @PasswordSalt,
                Role = @Role,
                IsActive = @IsActive,
                UpdatedAtUtc = @UpdatedAtUtc,
                LastLoginAtUtc = @LastLoginAtUtc
            WHERE Id = @Id;
            """,
            new
            {
                user.Id,
                user.DisplayName,
                user.PasswordHash,
                user.PasswordSalt,
                user.Role,
                IsActive = user.IsActive ? 1 : 0,
                UpdatedAtUtc = ToText(user.UpdatedAtUtc),
                LastLoginAtUtc = ToText(user.LastLoginAtUtc)
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task SetUserActiveAsync(long id, bool isActive, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE Users SET IsActive = @IsActive, UpdatedAtUtc = @UpdatedAtUtc WHERE Id = @Id;
            """,
            new
            {
                Id = id,
                IsActive = isActive ? 1 : 0,
                UpdatedAtUtc = ToText(DateTimeOffset.UtcNow)
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task RecordLoginAttemptAsync(LoginAttemptRecord attempt, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO LoginAttempts (UserName, Succeeded, IpAddress, UserAgent, Detail, CreatedAtUtc)
            VALUES (@UserName, @Succeeded, @IpAddress, @UserAgent, @Detail, @CreatedAtUtc);
            """,
            new
            {
                attempt.UserName,
                Succeeded = attempt.Succeeded ? 1 : 0,
                attempt.IpAddress,
                attempt.UserAgent,
                attempt.Detail,
                CreatedAtUtc = ToText(attempt.CreatedAtUtc)
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<LoginAttemptRecord>> ListLoginAttemptsAsync(int take = 100, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<LoginRow>(new CommandDefinition("""
            SELECT * FROM LoginAttempts ORDER BY Id DESC LIMIT @Take;
            """, new { Take = take }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.Select(r => new LoginAttemptRecord
        {
            Id = r.Id,
            UserName = r.UserName,
            Succeeded = r.Succeeded != 0,
            IpAddress = r.IpAddress,
            UserAgent = r.UserAgent,
            Detail = r.Detail,
            CreatedAtUtc = FromText(r.CreatedAtUtc)
        }).ToList();
    }

    public async Task WriteAuditAsync(AuditEventRecord audit, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO AuditEvents (UserId, UserName, Action, Category, Detail, CreatedAtUtc)
            VALUES (@UserId, @UserName, @Action, @Category, @Detail, @CreatedAtUtc);
            """,
            new
            {
                audit.UserId,
                audit.UserName,
                audit.Action,
                audit.Category,
                audit.Detail,
                CreatedAtUtc = ToText(audit.CreatedAtUtc)
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AuditEventRecord>> ListAuditEventsAsync(int take = 100, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<AuditRow>(new CommandDefinition("""
            SELECT * FROM AuditEvents ORDER BY Id DESC LIMIT @Take;
            """, new { Take = take }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.Select(r => new AuditEventRecord
        {
            Id = r.Id,
            UserId = r.UserId,
            UserName = r.UserName,
            Action = r.Action,
            Category = r.Category,
            Detail = r.Detail,
            CreatedAtUtc = FromText(r.CreatedAtUtc)
        }).ToList();
    }

    public async Task<long> SaveSyncRunAsync(SyncRunRecord run, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var runId = await connection.ExecuteScalarAsync<long>(new CommandDefinition("""
            INSERT INTO SyncRuns (
                StartedAtUtc, FinishedAtUtc, Trigger, State, DryRun, MainConnectionName, Message,
                SuccessfulTables, FailedTables, RowsInserted, RowsUpdated, RowsDeleted, RowsConflicted,
                EstimatedBytesTransferred, RequestedByUserName)
            VALUES (
                @StartedAtUtc, @FinishedAtUtc, @Trigger, @State, @DryRun, @MainConnectionName, @Message,
                @SuccessfulTables, @FailedTables, @RowsInserted, @RowsUpdated, @RowsDeleted, @RowsConflicted,
                @EstimatedBytesTransferred, @RequestedByUserName);
            SELECT last_insert_rowid();
            """,
            new
            {
                StartedAtUtc = ToText(run.StartedAtUtc),
                FinishedAtUtc = ToText(run.FinishedAtUtc),
                run.Trigger,
                run.State,
                DryRun = run.DryRun ? 1 : 0,
                run.MainConnectionName,
                run.Message,
                run.SuccessfulTables,
                run.FailedTables,
                run.RowsInserted,
                run.RowsUpdated,
                run.RowsDeleted,
                run.RowsConflicted,
                run.EstimatedBytesTransferred,
                run.RequestedByUserName
            },
            tx,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        foreach (var server in run.Servers)
        {
            await connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO SyncRunServers (
                    SyncRunId, ConnectionId, Name, Server, DatabaseName, IsMain, Outcome, Reason, TablesOk, TablesFailed)
                VALUES (
                    @SyncRunId, @ConnectionId, @Name, @Server, @DatabaseName, @IsMain, @Outcome, @Reason, @TablesOk, @TablesFailed);
                """,
                new
                {
                    SyncRunId = runId,
                    server.ConnectionId,
                    server.Name,
                    server.Server,
                    server.DatabaseName,
                    IsMain = server.IsMain ? 1 : 0,
                    server.Outcome,
                    server.Reason,
                    server.TablesOk,
                    server.TablesFailed
                },
                tx,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }

        foreach (var table in run.Tables)
        {
            await connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO SyncRunTables (
                    SyncRunId, SchemaName, TableName, TargetConnectionId, TargetConnectionName,
                    Success, Skipped, RowsInserted, RowsUpdated, RowsDeleted, RowsConflicted,
                    EstimatedBytesTransferred, ElapsedMs, ErrorMessage)
                VALUES (
                    @SyncRunId, @SchemaName, @TableName, @TargetConnectionId, @TargetConnectionName,
                    @Success, @Skipped, @RowsInserted, @RowsUpdated, @RowsDeleted, @RowsConflicted,
                    @EstimatedBytesTransferred, @ElapsedMs, @ErrorMessage);
                """,
                new
                {
                    SyncRunId = runId,
                    table.SchemaName,
                    table.TableName,
                    table.TargetConnectionId,
                    table.TargetConnectionName,
                    Success = table.Success ? 1 : 0,
                    Skipped = table.Skipped ? 1 : 0,
                    table.RowsInserted,
                    table.RowsUpdated,
                    table.RowsDeleted,
                    table.RowsConflicted,
                    table.EstimatedBytesTransferred,
                    table.ElapsedMs,
                    table.ErrorMessage
                },
                tx,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return runId;
    }

    public async Task<IReadOnlyList<SyncRunRecord>> ListSyncRunsAsync(int take = 50, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<SyncRunRow>(new CommandDefinition("""
            SELECT * FROM SyncRuns ORDER BY Id DESC LIMIT @Take;
            """, new { Take = take }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.Select(MapSyncRun).ToList();
    }

    public async Task<SyncRunRecord?> GetSyncRunAsync(long id, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var row = await connection.QuerySingleOrDefaultAsync<SyncRunRow>(new CommandDefinition("""
            SELECT * FROM SyncRuns WHERE Id = @Id;
            """, new { Id = id }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        var run = MapSyncRun(row);
        var servers = await connection.QueryAsync<SyncRunServerRow>(new CommandDefinition("""
            SELECT * FROM SyncRunServers WHERE SyncRunId = @Id ORDER BY Id;
            """, new { Id = id }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        var tables = await connection.QueryAsync<SyncRunTableRow>(new CommandDefinition("""
            SELECT * FROM SyncRunTables WHERE SyncRunId = @Id ORDER BY Id;
            """, new { Id = id }, cancellationToken: cancellationToken)).ConfigureAwait(false);

        run.Servers = servers.Select(s => new SyncRunServerRecord
        {
            Id = s.Id,
            SyncRunId = s.SyncRunId,
            ConnectionId = s.ConnectionId,
            Name = s.Name,
            Server = s.Server,
            DatabaseName = s.DatabaseName,
            IsMain = s.IsMain != 0,
            Outcome = s.Outcome,
            Reason = s.Reason,
            TablesOk = s.TablesOk,
            TablesFailed = s.TablesFailed
        }).ToList();

        run.Tables = tables.Select(t => new SyncRunTableRecord
        {
            Id = t.Id,
            SyncRunId = t.SyncRunId,
            SchemaName = t.SchemaName,
            TableName = t.TableName,
            TargetConnectionId = t.TargetConnectionId,
            TargetConnectionName = t.TargetConnectionName,
            Success = t.Success != 0,
            Skipped = t.Skipped != 0,
            RowsInserted = t.RowsInserted,
            RowsUpdated = t.RowsUpdated,
            RowsDeleted = t.RowsDeleted,
            RowsConflicted = t.RowsConflicted,
            EstimatedBytesTransferred = t.EstimatedBytesTransferred,
            ElapsedMs = t.ElapsedMs,
            ErrorMessage = t.ErrorMessage
        }).ToList();

        return run;
    }

    public async Task SaveTableSelectionSnapshotAsync(TableSelectionSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO TableSelectionSnapshots (SyncAllTables, SelectedTablesJson, ChangedByUserName, CreatedAtUtc)
            VALUES (@SyncAllTables, @SelectedTablesJson, @ChangedByUserName, @CreatedAtUtc);
            """,
            new
            {
                SyncAllTables = snapshot.SyncAllTables ? 1 : 0,
                snapshot.SelectedTablesJson,
                snapshot.ChangedByUserName,
                CreatedAtUtc = ToText(snapshot.CreatedAtUtc)
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<TableSelectionSnapshot>> ListTableSelectionSnapshotsAsync(int take = 50, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<TableSelectionRow>(new CommandDefinition("""
            SELECT * FROM TableSelectionSnapshots ORDER BY Id DESC LIMIT @Take;
            """, new { Take = take }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.Select(r => new TableSelectionSnapshot
        {
            Id = r.Id,
            SyncAllTables = r.SyncAllTables != 0,
            SelectedTablesJson = r.SelectedTablesJson,
            ChangedByUserName = r.ChangedByUserName,
            CreatedAtUtc = FromText(r.CreatedAtUtc)
        }).ToList();
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

    private static string ToText(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static string? ToText(DateTimeOffset? value) =>
        value is null ? null : ToText(value.Value);

    private static DateTimeOffset FromText(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static DateTimeOffset? FromTextNullable(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : FromText(value);

    private static AppUser MapUser(UserRow row) => new()
    {
        Id = row.Id,
        UserName = row.UserName,
        DisplayName = row.DisplayName,
        PasswordHash = row.PasswordHash,
        PasswordSalt = row.PasswordSalt,
        Role = row.Role,
        IsActive = row.IsActive != 0,
        CreatedAtUtc = FromText(row.CreatedAtUtc),
        UpdatedAtUtc = FromText(row.UpdatedAtUtc),
        LastLoginAtUtc = FromTextNullable(row.LastLoginAtUtc)
    };

    private static SyncRunRecord MapSyncRun(SyncRunRow row) => new()
    {
        Id = row.Id,
        StartedAtUtc = FromText(row.StartedAtUtc),
        FinishedAtUtc = FromTextNullable(row.FinishedAtUtc),
        Trigger = row.Trigger,
        State = row.State,
        DryRun = row.DryRun != 0,
        MainConnectionName = row.MainConnectionName,
        Message = row.Message,
        SuccessfulTables = row.SuccessfulTables,
        FailedTables = row.FailedTables,
        RowsInserted = row.RowsInserted,
        RowsUpdated = row.RowsUpdated,
        RowsDeleted = row.RowsDeleted,
        RowsConflicted = row.RowsConflicted,
        EstimatedBytesTransferred = row.EstimatedBytesTransferred,
        RequestedByUserName = row.RequestedByUserName
    };

    private sealed class UserRow
    {
        public long Id { get; init; }
        public string UserName { get; init; } = string.Empty;
        public string DisplayName { get; init; } = string.Empty;
        public string PasswordHash { get; init; } = string.Empty;
        public string PasswordSalt { get; init; } = string.Empty;
        public string Role { get; init; } = string.Empty;
        public long IsActive { get; init; }
        public string CreatedAtUtc { get; init; } = string.Empty;
        public string UpdatedAtUtc { get; init; } = string.Empty;
        public string? LastLoginAtUtc { get; init; }
    }

    private sealed class LoginRow
    {
        public long Id { get; init; }
        public string UserName { get; init; } = string.Empty;
        public long Succeeded { get; init; }
        public string? IpAddress { get; init; }
        public string? UserAgent { get; init; }
        public string? Detail { get; init; }
        public string CreatedAtUtc { get; init; } = string.Empty;
    }

    private sealed class AuditRow
    {
        public long Id { get; init; }
        public long? UserId { get; init; }
        public string? UserName { get; init; }
        public string Action { get; init; } = string.Empty;
        public string Category { get; init; } = string.Empty;
        public string? Detail { get; init; }
        public string CreatedAtUtc { get; init; } = string.Empty;
    }

    private sealed class SyncRunRow
    {
        public long Id { get; init; }
        public string StartedAtUtc { get; init; } = string.Empty;
        public string? FinishedAtUtc { get; init; }
        public string Trigger { get; init; } = string.Empty;
        public string State { get; init; } = string.Empty;
        public long DryRun { get; init; }
        public string? MainConnectionName { get; init; }
        public string? Message { get; init; }
        public int SuccessfulTables { get; init; }
        public int FailedTables { get; init; }
        public long RowsInserted { get; init; }
        public long RowsUpdated { get; init; }
        public long RowsDeleted { get; init; }
        public long RowsConflicted { get; init; }
        public long EstimatedBytesTransferred { get; init; }
        public string? RequestedByUserName { get; init; }
    }

    private sealed class SyncRunServerRow
    {
        public long Id { get; init; }
        public long SyncRunId { get; init; }
        public string? ConnectionId { get; init; }
        public string Name { get; init; } = string.Empty;
        public string? Server { get; init; }
        public string? DatabaseName { get; init; }
        public long IsMain { get; init; }
        public string Outcome { get; init; } = string.Empty;
        public string? Reason { get; init; }
        public int TablesOk { get; init; }
        public int TablesFailed { get; init; }
    }

    private sealed class SyncRunTableRow
    {
        public long Id { get; init; }
        public long SyncRunId { get; init; }
        public string SchemaName { get; init; } = string.Empty;
        public string TableName { get; init; } = string.Empty;
        public string? TargetConnectionId { get; init; }
        public string? TargetConnectionName { get; init; }
        public long Success { get; init; }
        public long Skipped { get; init; }
        public long RowsInserted { get; init; }
        public long RowsUpdated { get; init; }
        public long RowsDeleted { get; init; }
        public long RowsConflicted { get; init; }
        public long EstimatedBytesTransferred { get; init; }
        public long ElapsedMs { get; init; }
        public string? ErrorMessage { get; init; }
    }

    private sealed class TableSelectionRow
    {
        public long Id { get; init; }
        public long SyncAllTables { get; init; }
        public string SelectedTablesJson { get; init; } = "[]";
        public string? ChangedByUserName { get; init; }
        public string CreatedAtUtc { get; init; } = string.Empty;
    }
}
