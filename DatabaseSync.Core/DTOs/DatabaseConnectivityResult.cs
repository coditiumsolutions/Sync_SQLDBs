using DatabaseSync.Core.Enums;

namespace DatabaseSync.Core.DTOs;

public sealed record DatabaseConnectivityResult
{
    public required string ConnectionId { get; init; }

    public required string ConnectionName { get; init; }

    public bool IsMain { get; init; }

    public required string Server { get; init; }

    public required string Database { get; init; }

    public ConnectionStatus Status { get; init; }

    public TimeSpan Elapsed { get; init; }

    public string? ServerVersion { get; init; }

    public string? ErrorMessage { get; init; }

    public DateTimeOffset CheckedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public bool IsSuccessful => Status == ConnectionStatus.Connected;
}
