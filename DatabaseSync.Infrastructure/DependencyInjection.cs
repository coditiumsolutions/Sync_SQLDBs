using DatabaseSync.Core.Interfaces;
using DatabaseSync.Infrastructure.Configuration;
using DatabaseSync.Infrastructure.Data;
using DatabaseSync.Infrastructure.Management;
using DatabaseSync.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DatabaseSync.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddDatabaseSyncInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<SyncConfigStoreOptions>(
            configuration.GetSection(SyncConfigStoreOptions.SectionName));
        services.Configure<ManagementDbOptions>(
            configuration.GetSection(ManagementDbOptions.SectionName));
        services.Configure<SynchronizationDbOptions>(
            configuration.GetSection(SynchronizationDbOptions.SectionName));

        services.AddSingleton<ISyncConfigStore, SyncConfigStore>();
        services.AddSingleton<ISyncStatusStore, SyncStatusStore>();
        services.AddSingleton<IManagementStore, SqliteManagementStore>();
        services.AddSingleton<IProjectStore, SqliteProjectStore>();
        services.AddSingleton<ISqlConnectionFactory, SqlConnectionFactory>();
        services.AddSingleton<IDatabaseConnectionService, DatabaseConnectionService>();
        services.AddSingleton<IConnectivityCheckService, ConnectivityCheckService>();
        services.AddSingleton<ISchemaDiscoveryService, SchemaDiscoveryService>();
        services.AddSingleton<ITableSyncService, TableSyncService>();
        services.AddSingleton<ISyncOrchestrator, SyncOrchestrator>();
        services.AddSingleton<IManualSyncRequestStore, ManualSyncRequestStore>();
        services.AddSingleton<ISyncCycleRunner, SyncCycleRunner>();

        return services;
    }
}
