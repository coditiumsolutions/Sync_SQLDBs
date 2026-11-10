using System.Diagnostics;
using Dapper;
using DatabaseSync.Core.DTOs;
using DatabaseSync.Core.Enums;
using DatabaseSync.Core.Interfaces;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace DatabaseSync.Infrastructure.Services;

public sealed class DatabaseConnectionService : IDatabaseConnectionService
{
    private readonly ISqlConnectionFactory _connectionFactory;
    private readonly ILogger<DatabaseConnectionService> _logger;

    public DatabaseConnectionService(
        ISqlConnectionFactory connectionFactory,
        ILogger<DatabaseConnectionService> logger)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<DatabaseConnectivityResult> TestConnectionAsync(
        string connectionId,
        CancellationToken cancellationToken = default)
    {
        var options = _connectionFactory.GetConnection(connectionId);
        var summary = _connectionFactory.BuildSafeConnectionSummary(connectionId);
        var stopwatch = Stopwatch.StartNew();

        _logger.LogInformation(
            "Testing connectivity for {ConnectionName}: {ConnectionSummary}",
            options.Name,
            summary);

        try
        {
            await using var connection = _connectionFactory.CreateConnection(connectionId);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            var commandDefinition = new CommandDefinition(
                commandText: "SELECT @@VERSION;",
                commandTimeout: Math.Max(options.ConnectionTimeoutSeconds, 5),
                cancellationToken: cancellationToken);

            var versionRaw = await connection
                .ExecuteScalarAsync<string>(commandDefinition)
                .ConfigureAwait(false);

            stopwatch.Stop();

            var version = versionRaw?
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault()?
                .Trim();

            _logger.LogInformation(
                "Connectivity OK for {ConnectionName} ({Server}/{Database}) in {ElapsedMs} ms. Version: {ServerVersion}",
                options.Name,
                options.Server,
                options.Database,
                stopwatch.ElapsedMilliseconds,
                version);

            return new DatabaseConnectivityResult
            {
                ConnectionId = options.Id,
                ConnectionName = options.Name,
                IsMain = options.IsMain,
                Server = options.Server,
                Database = options.Database,
                Status = ConnectionStatus.Connected,
                Elapsed = stopwatch.Elapsed,
                ServerVersion = version,
                CheckedAtUtc = DateTimeOffset.UtcNow
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            return new DatabaseConnectivityResult
            {
                ConnectionId = options.Id,
                ConnectionName = options.Name,
                IsMain = options.IsMain,
                Server = options.Server,
                Database = options.Database,
                Status = ConnectionStatus.Cancelled,
                Elapsed = stopwatch.Elapsed,
                ErrorMessage = "Connectivity test was cancelled.",
                CheckedAtUtc = DateTimeOffset.UtcNow
            };
        }
        catch (SqlException ex) when (IsTimeout(ex))
        {
            stopwatch.Stop();
            _logger.LogError(ex, "Connectivity timed out for {ConnectionName}.", options.Name);
            return new DatabaseConnectivityResult
            {
                ConnectionId = options.Id,
                ConnectionName = options.Name,
                IsMain = options.IsMain,
                Server = options.Server,
                Database = options.Database,
                Status = ConnectionStatus.TimedOut,
                Elapsed = stopwatch.Elapsed,
                ErrorMessage = SanitizeErrorMessage(ex.Message),
                CheckedAtUtc = DateTimeOffset.UtcNow
            };
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogError(ex, "Connectivity failed for {ConnectionName}.", options.Name);
            return new DatabaseConnectivityResult
            {
                ConnectionId = options.Id,
                ConnectionName = options.Name,
                IsMain = options.IsMain,
                Server = options.Server,
                Database = options.Database,
                Status = ConnectionStatus.Failed,
                Elapsed = stopwatch.Elapsed,
                ErrorMessage = SanitizeErrorMessage(ex.Message),
                CheckedAtUtc = DateTimeOffset.UtcNow
            };
        }
    }

    private static bool IsTimeout(SqlException ex) =>
        ex.Number is -2 or 258 or 1222 ||
        ex.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase);

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
}
