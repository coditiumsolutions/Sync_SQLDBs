using Dapper;
using DatabaseSync.Core.DTOs;
using DatabaseSync.Core.Enums;
using DatabaseSync.Core.Interfaces;
using DatabaseSync.Core.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace DatabaseSync.Infrastructure.Services;

/// <summary>
/// Synchronises all tables from the nominated Main connection to every other enabled connection.
/// </summary>
public sealed class SyncOrchestrator : ISyncOrchestrator
{
    private readonly ISchemaDiscoveryService _schemaDiscoveryService;
    private readonly ITableSyncService _tableSyncService;
    private readonly IDatabaseConnectionService _databaseConnectionService;
    private readonly ISqlConnectionFactory _connectionFactory;
    private readonly ISyncConfigStore _configStore;
    private readonly ILogger<SyncOrchestrator> _logger;

    public SyncOrchestrator(
        ISchemaDiscoveryService schemaDiscoveryService,
        ITableSyncService tableSyncService,
        IDatabaseConnectionService databaseConnectionService,
        ISqlConnectionFactory connectionFactory,
        ISyncConfigStore configStore,
        ILogger<SyncOrchestrator> logger)
    {
        _schemaDiscoveryService = schemaDiscoveryService ?? throw new ArgumentNullException(nameof(schemaDiscoveryService));
        _tableSyncService = tableSyncService ?? throw new ArgumentNullException(nameof(tableSyncService));
        _databaseConnectionService = databaseConnectionService ?? throw new ArgumentNullException(nameof(databaseConnectionService));
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _configStore = configStore ?? throw new ArgumentNullException(nameof(configStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<SyncCycleResult> RunAsync(SyncTriggerKind trigger, CancellationToken cancellationToken = default)
    {
        var started = DateTimeOffset.UtcNow;
        var settings = _configStore.Get().SyncSettings;
        var dryRun = settings.DryRun;

        if (!settings.SyncMainToOthers)
        {
            return new SyncCycleResult
            {
                StartedAtUtc = started,
                FinishedAtUtc = DateTimeOffset.UtcNow,
                Success = false,
                Trigger = trigger,
                DryRun = dryRun,
                ErrorMessage = "SyncMainToOthers is disabled."
            };
        }

        try
        {
            var main = _connectionFactory.GetMainConnection();
            if (main is null)
            {
                return new SyncCycleResult
                {
                    StartedAtUtc = started,
                    FinishedAtUtc = DateTimeOffset.UtcNow,
                    Success = false,
                    Trigger = trigger,
                    DryRun = dryRun,
                    ErrorMessage = "No Main database is nominated. Open the Admin UI and mark one connection as Main."
                };
            }

            var mainConnectivity = await _databaseConnectionService
                .TestConnectionAsync(main.Id, cancellationToken)
                .ConfigureAwait(false);

            if (!mainConnectivity.IsSuccessful)
            {
                return new SyncCycleResult
                {
                    StartedAtUtc = started,
                    FinishedAtUtc = DateTimeOffset.UtcNow,
                    Success = false,
                    Trigger = trigger,
                    DryRun = dryRun,
                    MainConnectionName = main.Name,
                    ErrorMessage = $"Main database '{main.Name}' is unreachable: {mainConnectivity.ErrorMessage}"
                };
            }

            var cycleId = Guid.NewGuid();
            var backupFolder = ResolveBackupFolder(settings);
            Directory.CreateDirectory(backupFolder);
            var tableRequest = new SyncTableRequest
            {
                CycleId = cycleId,
                Trigger = trigger,
                DryRun = dryRun,
                BackupFolder = backupFolder
            };

            var tables = await _schemaDiscoveryService
                .GetTablesForSyncAsync(main.Id, cancellationToken)
                .ConfigureAwait(false);

            var targets = _connectionFactory.GetTargetConnections();
            var allResults = new List<TableSyncResult>();
            var syncedTargets = new List<string>();

            foreach (var target in targets)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var targetConnectivity = await _databaseConnectionService
                    .TestConnectionAsync(target.Id, cancellationToken)
                    .ConfigureAwait(false);

                if (!targetConnectivity.IsSuccessful)
                {
                    _logger.LogWarning(
                        "Skipping sync to {TargetName}: {Error}",
                        target.Name,
                        targetConnectivity.ErrorMessage);
                    continue;
                }

                _logger.LogInformation(
                    "Starting {Trigger} differential sync {MainName} => {TargetName} for {TableCount} tables. DryRun={DryRun} CycleId={CycleId}",
                    trigger,
                    main.Name,
                    target.Name,
                    tables.Count,
                    dryRun,
                    cycleId);

                await SetForeignKeysAsync(target.Id, enable: false, cancellationToken).ConfigureAwait(false);

                try
                {
                    foreach (var table in tables)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var result = await _tableSyncService
                            .SyncTableAsync(main.Id, target.Id, table, tableRequest, cancellationToken)
                            .ConfigureAwait(false);
                        allResults.Add(result);
                    }
                }
                finally
                {
                    await SetForeignKeysAsync(target.Id, enable: true, cancellationToken).ConfigureAwait(false);
                }

                syncedTargets.Add(target.Name);

                var failed = allResults.Count(r => r.TargetConnectionId == target.Id && !r.Success);
                _logger.LogInformation(
                    "Finished {MainName} => {TargetName}. FailedTables={FailedTables}/{TotalTables}.",
                    main.Name,
                    target.Name,
                    failed,
                    tables.Count);
            }

            var anyFailed = allResults.Any(result => !result.Success);
            var success = !anyFailed && syncedTargets.Count > 0;
            string? error = null;
            if (targets.Count == 0)
            {
                error = "No target databases are enabled.";
            }
            else if (syncedTargets.Count == 0)
            {
                error = "No target databases were reachable.";
            }
            else if (anyFailed)
            {
                error = "One or more tables failed to synchronise. See logs.";
            }
            else if (allResults.Count == 0)
            {
                error = "No syncable tables. Tables without a primary key are skipped.";
                success = true;
            }

            _logger.LogInformation(
                "Differential cycle finished. Trigger={Trigger} DryRun={DryRun} Success={Success} Inserted={Inserted} Updated={Updated} Deleted={Deleted} Conflicted={Conflicted} EstimatedBytes={EstimatedBytes} BackupFolder={BackupFolder}",
                trigger,
                dryRun,
                success,
                allResults.Sum(result => result.RowsInserted),
                allResults.Sum(result => result.RowsUpdated),
                allResults.Sum(result => result.RowsDeleted),
                allResults.Sum(result => result.RowsConflicted),
                allResults.Sum(result => result.EstimatedBytesTransferred),
                backupFolder);

            return new SyncCycleResult
            {
                StartedAtUtc = started,
                FinishedAtUtc = DateTimeOffset.UtcNow,
                Success = success,
                Trigger = trigger,
                DryRun = dryRun,
                MainConnectionName = main.Name,
                TargetsSynced = syncedTargets,
                TableResults = allResults,
                ErrorMessage = error
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Synchronisation cycle failed.");
            return new SyncCycleResult
            {
                StartedAtUtc = started,
                FinishedAtUtc = DateTimeOffset.UtcNow,
                Success = false,
                Trigger = trigger,
                DryRun = dryRun,
                ErrorMessage = ex.Message
            };
        }
    }

    private string ResolveBackupFolder(SyncSettings settings)
    {
        if (Path.IsPathRooted(settings.ConflictBackupFolder))
        {
            return settings.ConflictBackupFolder;
        }

        var configDirectory = Path.GetDirectoryName(_configStore.ConfigFilePath) ?? AppContext.BaseDirectory;
        return Path.GetFullPath(Path.Combine(configDirectory, settings.ConflictBackupFolder));
    }

    private async Task SetForeignKeysAsync(string connectionId, bool enable, CancellationToken cancellationToken)
    {
        var timeout = _configStore.Get().SyncSettings.CommandTimeoutSeconds;
        await using var connection = (SqlConnection)_connectionFactory.CreateConnection(connectionId);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var action = enable
            ? "WITH NOCHECK CHECK CONSTRAINT ALL"
            : "NOCHECK CONSTRAINT ALL";

        const string sql = """
            SELECT
                QUOTENAME(OBJECT_SCHEMA_NAME(parent_object_id)) + '.' + QUOTENAME(OBJECT_NAME(parent_object_id)) AS TableName
            FROM sys.foreign_keys
            GROUP BY parent_object_id;
            """;

        var tables = (await connection.QueryAsync<string>(new CommandDefinition(
            sql,
            commandTimeout: timeout,
            cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();

        foreach (var table in tables)
        {
            try
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    $"ALTER TABLE {table} {action};",
                    commandTimeout: timeout,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not alter constraints on {Table} ({Action}).", table, action);
            }
        }
    }
}
