using Serilog;
using Serilog.Events;

namespace DatabaseSync.Worker.Logging;

/// <summary>
/// Centralised Serilog bootstrap for console and rolling file sinks.
/// </summary>
public static class SerilogConfigurator
{
    public const string LogDirectory = "Logs";
    public const string LogFileNamePattern = "DatabaseSync-.log";

    public static void Configure(
        IConfiguration configuration,
        IHostEnvironment environment,
        IServiceProvider services,
        LoggerConfiguration loggerConfiguration)
    {
        var logPath = Path.Combine(environment.ContentRootPath, LogDirectory, LogFileNamePattern);

        loggerConfiguration
            .ReadFrom.Configuration(configuration)
            .ReadFrom.Services(services)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Application", "Coditium.DatabaseSync")
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
            .MinimumLevel.Override("System", LogEventLevel.Warning)
            .WriteTo.Console(
                outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
            .WriteTo.File(
                path: logPath,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 30,
                shared: true,
                flushToDiskInterval: TimeSpan.FromSeconds(1),
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] ({SourceContext}) {Message:lj}{NewLine}{Exception}");
    }
}
