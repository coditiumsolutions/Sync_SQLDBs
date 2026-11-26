namespace DatabaseSync.Core.DTOs;

/// <summary>
/// Lightweight table listing for the Admin UI table-selection page.
/// </summary>
public sealed class TableCatalogEntry
{
    public string SchemaName { get; set; } = string.Empty;

    public string TableName { get; set; } = string.Empty;

    public bool HasPrimaryKey { get; set; }

    public long ApproximateRowCount { get; set; }

    public string Key => $"{SchemaName}.{TableName}";

    public string DisplayName => $"{SchemaName}.{TableName}";
}
