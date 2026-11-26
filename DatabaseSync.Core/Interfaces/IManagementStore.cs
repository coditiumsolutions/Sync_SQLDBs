using DatabaseSync.Core.Models.Management;

namespace DatabaseSync.Core.Interfaces;

public interface IManagementStore
{
    string DatabasePath { get; }

    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task<AppUser?> GetUserByUserNameAsync(string userName, CancellationToken cancellationToken = default);

    Task<AppUser?> GetUserByIdAsync(long id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AppUser>> ListUsersAsync(CancellationToken cancellationToken = default);

    Task<long> CreateUserAsync(AppUser user, CancellationToken cancellationToken = default);

    Task UpdateUserAsync(AppUser user, CancellationToken cancellationToken = default);

    Task SetUserActiveAsync(long id, bool isActive, CancellationToken cancellationToken = default);

    Task RecordLoginAttemptAsync(LoginAttemptRecord attempt, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LoginAttemptRecord>> ListLoginAttemptsAsync(int take = 100, CancellationToken cancellationToken = default);

    Task WriteAuditAsync(AuditEventRecord audit, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AuditEventRecord>> ListAuditEventsAsync(int take = 100, CancellationToken cancellationToken = default);

    Task<long> SaveSyncRunAsync(SyncRunRecord run, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SyncRunRecord>> ListSyncRunsAsync(int take = 50, CancellationToken cancellationToken = default);

    Task<SyncRunRecord?> GetSyncRunAsync(long id, CancellationToken cancellationToken = default);

    Task SaveTableSelectionSnapshotAsync(TableSelectionSnapshot snapshot, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TableSelectionSnapshot>> ListTableSelectionSnapshotsAsync(int take = 50, CancellationToken cancellationToken = default);
}
