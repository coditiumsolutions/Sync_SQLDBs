using DatabaseSync.Core.DTOs;

namespace DatabaseSync.Core.Interfaces;

/// <summary>
/// Orchestrates connectivity checks across all configured databases, including retries.
/// </summary>
public interface IConnectivityCheckService
{
    /// <summary>
    /// Tests connectivity to Main, Cloud, and Local databases.
    /// Individual failures are captured in the results and do not throw.
    /// </summary>
    Task<IReadOnlyList<DatabaseConnectivityResult>> CheckAllDatabasesAsync(
        CancellationToken cancellationToken = default);
}
