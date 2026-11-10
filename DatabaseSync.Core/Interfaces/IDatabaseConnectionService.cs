using DatabaseSync.Core.DTOs;

namespace DatabaseSync.Core.Interfaces;

public interface IDatabaseConnectionService
{
    Task<DatabaseConnectivityResult> TestConnectionAsync(
        string connectionId,
        CancellationToken cancellationToken = default);
}
