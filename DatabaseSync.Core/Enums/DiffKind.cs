namespace DatabaseSync.Core.Enums;

/// <summary>How a target row differs from the main database.</summary>
public enum DiffKind
{
    Insert = 0,
    Update = 1,
    Delete = 2
}
