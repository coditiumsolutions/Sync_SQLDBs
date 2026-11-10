using DatabaseSync.Infrastructure;
using DatabaseSync.Worker.Logging;
using DatabaseSync.Worker.Workers;
using Serilog;

Directory.SetCurrentDirectory(AppContext.BaseDirectory);

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console()
    .WriteTo.File(
        path: Path.Combine(AppContext.BaseDirectory, "Logs", "DatabaseSync-startup-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 7)
    .CreateBootstrapLogger();

try
{
    Log.Information("Starting Coditium.DatabaseSync host from {BaseDirectory}", AppContext.BaseDirectory);

    var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
    {
        Args = args,
        ContentRootPath = AppContext.BaseDirectory
    });

    builder.Services.AddWindowsService(options =>
    {
        options.ServiceName = "Coditium.DatabaseSync";
    });

    builder.Services.AddSerilog((services, loggerConfiguration) =>
        SerilogConfigurator.Configure(
            builder.Configuration,
            builder.Environment,
            services,
            loggerConfiguration));

    builder.Services.AddDatabaseSyncInfrastructure(builder.Configuration);
    builder.Services.AddHostedService<DatabaseSyncWorker>();

    var host = builder.Build();

    Log.Information(
        "Coditium.DatabaseSync configured. Environment={EnvironmentName}",
        builder.Environment.EnvironmentName);

    await host.RunAsync().ConfigureAwait(false);
}
catch (Exception ex)
{
    Log.Fatal(ex, "Coditium.DatabaseSync terminated unexpectedly");
    throw;
}
finally
{
    Log.Information("Coditium.DatabaseSync host shut down");
    await Log.CloseAndFlushAsync().ConfigureAwait(false);
}
