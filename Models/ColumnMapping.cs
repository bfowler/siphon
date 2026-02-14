namespace Siphon.Models;

/// <summary>
/// Represents a mapping between a source SQL Server column and destination SQLite column.
/// </summary>
public class ColumnMapping
{
    /// <summary>
    /// Column name in SQL Server.
    /// </summary>
    public string SourceColumn { get; set; } = string.Empty;

    /// <summary>
    /// SQL Server data type (e.g., "nvarchar", "int", "datetime").
    /// </summary>
    public string SourceType { get; set; } = string.Empty;

    /// <summary>
    /// Column name in SQLite.
    /// </summary>
    public string DestinationColumn { get; set; } = string.Empty;

    /// <summary>
    /// SQLite data type (e.g., "TEXT", "INTEGER", "REAL", "BLOB").
    /// </summary>
    public string DestinationType { get; set; } = string.Empty;

    /// <summary>
    /// Whether the column allows null values.
    /// </summary>
    public bool IsNullable { get; set; } = true;

    /// <summary>
    /// Whether this column is part of the primary key.
    /// </summary>
    public bool IsPrimaryKey { get; set; } = false;
}