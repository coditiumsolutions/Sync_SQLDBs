namespace DatabaseSync.Core.Models;

/// <summary>
/// Root configuration section containing all three SQL Server connection profiles.
/// Bound from the "DatabaseConnections" section in appsettings.json.
/// </summary>
public sealed class DatabaseConnectionsOptions
{
    public const string SectionName = "DatabaseConnections";

    public DatabaseConnectionOptions MainDatabase { get; set; } = new();

    public DatabaseConnectionOptions CloudDatabase { get; set; } = new();

    public DatabaseConnectionOptions LocalDatabase { get; set; } = new();
}
