using DatabaseSync.Core.DTOs;
using DatabaseSync.Core.Enums;
using DatabaseSync.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace DatabaseSync.Infrastructure.Services;

public sealed class ConnectivityCheckService : IConnectivityCheckService
{
    private readonly IDatabaseConnectionService _databaseConnectionService;
    private readonly ISqlConnectionFactory _connectionFactory;
    private readonly ISyncConfigStore _configStore;
    private readonly ILogger<ConnectivityCheckService> _logger;

    public ConnectivityCheckService(
        IDatabaseConnectionService databaseConnectionService,
        ISqlConnectionFactory connectionFactory,
        ISyncConfigStore configStore,
        ILogger<ConnectivityCheckService> logger)
    {
        _databaseConnectionService = databaseConnectionService
            ?? throw new ArgumentNullException(nameof(databaseConnectionService));
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _configStore = configStore ?? throw new ArgumentNullException(nameof(configStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<IReadOnlyList<DatabaseConnectivityResult>> CheckAllDatabasesAsync(
        CancellationToken cancellationToken = default)
    {
        var connections = _connectionFactory.GetEnabledConnections();
        _logger.LogInformation("Starting connectivity check cycle for {DatabaseCount} databases.", connections.Count);

        var results = new List<DatabaseConnectivityResult>(connections.Count);
        foreach (var connection in connections)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await TestWithRetriesAsync(connection.Id, cancellationToken).ConfigureAwait(false));
        }

        _logger.LogInformation(
            "Connectivity check cycle completed. Successful={SuccessCount}/{TotalCount}.",
            results.Count(r => r.IsSuccessful),
            results.Count);

        return results;
    }

    private async Task<DatabaseConnectivityResult> TestWithRetriesAsync(
        string connectionId,
        CancellationToken cancellationToken)
    {
        var settings = _configStore.Get().SyncSettings;
        var maxAttempts = Math.Max(1, settings.MaximumRetryAttempts);
        DatabaseConnectivityResult? lastResult = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lastResult = await _databaseConnectionService
                .TestConnectionAsync(connectionId, cancellationToken)
                .ConfigureAwait(false);

            if (lastResult.IsSuccessful || lastResult.Status == ConnectionStatus.Cancelled)
            {
                return lastResult;
            }

            if (attempt < maxAttempts)
            {
                var delay = TimeSpan.FromSeconds(Math.Max(1, settings.RetryIntervalSeconds));
                _logger.LogWarning(
                    "Connectivity attempt {Attempt}/{MaxAttempts} failed for {ConnectionName}. Retrying in {RetrySeconds}s.",
                    attempt,
                    maxAttempts,
                    lastResult.ConnectionName,
                    delay.TotalSeconds);

                try
                {
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return lastResult with
                    {
                        Status = ConnectionStatus.Cancelled,
                        ErrorMessage = "Retry wait was cancelled."
                    };
                }
            }
        }

        return lastResult!;
    }
}
