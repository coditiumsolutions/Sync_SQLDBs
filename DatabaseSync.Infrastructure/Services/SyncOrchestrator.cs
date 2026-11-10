using Dapper;
using DatabaseSync.Core.DTOs;
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

    public async Task<SyncCycleResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var started = DateTimeOffset.UtcNow;
        var settings = _configStore.Get().SyncSettings;

        if (!settings.EnableSynchronization || !settings.SyncMainToOthers)
        {
            return new SyncCycleResult
            {
                StartedAtUtc = started,
                FinishedAtUtc = DateTimeOffset.UtcNow,
                Success = true,
                ErrorMessage = "Synchronisation is disabled."
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
                    MainConnectionName = main.Name,
                    ErrorMessage = $"Main database '{main.Name}' is unreachable: {mainConnectivity.ErrorMessage}"
                };
            }

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
                    "Starting {MainName} => {TargetName} sync for {TableCount} tables.",
                    main.Name,
                    target.Name,
                    tables.Count);

                await SetForeignKeysAsync(target.Id, enable: false, cancellationToken).ConfigureAwait(false);

                try
                {
                    foreach (var table in tables)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var result = await _tableSyncService
                            .SyncTableAsync(main.Id, target.Id, table, cancellationToken)
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

            var success = allResults.Count > 0 && allResults.All(r => r.Success);

            return new SyncCycleResult
            {
                StartedAtUtc = started,
                FinishedAtUtc = DateTimeOffset.UtcNow,
                Success = success,
                MainConnectionName = main.Name,
                TargetsSynced = syncedTargets,
                TableResults = allResults,
                ErrorMessage = success
                    ? null
                    : targets.Count == 0
                        ? "No target databases are enabled."
                        : "One or more tables failed to synchronise. See logs."
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
                ErrorMessage = ex.Message
            };
        }
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
