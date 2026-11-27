using DatabaseSync.Core.Models.Management;

namespace DatabaseSync.Core.Interfaces;

/// <summary>
/// Shared Synchronization projects store (not tied to the active product instance).
/// </summary>
public interface IProjectStore
{
    string DatabasePath { get; }

    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ProjectRecord>> ListProjectsAsync(CancellationToken cancellationToken = default);

    Task<ProjectRecord?> GetProjectAsync(long id, CancellationToken cancellationToken = default);

    Task<long> SaveProjectAsync(ProjectRecord project, CancellationToken cancellationToken = default);

    Task DeleteProjectAsync(long id, CancellationToken cancellationToken = default);

    Task SetProjectActiveAsync(long id, bool isActive, CancellationToken cancellationToken = default);

    Task DeleteProjectDatabaseAsync(long projectDatabaseId, CancellationToken cancellationToken = default);

    Task<long> UpsertProjectDatabaseAsync(ProjectDatabaseRecord database, CancellationToken cancellationToken = default);
}
