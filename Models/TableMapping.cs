namespace Siphon.Models;

/// <summary>
/// Represents a mapping between a source SQL Server table and destination SQLite table.
/// </summary>
public class TableMapping
{
    /// <summary>
    /// Full source table name including schema (e.g., "dbo.Customers").
    /// </summary>
    public string SourceTable { get; set; } = string.Empty;

    /// <summary>
    /// Schema name of the source table.
    /// </summary>
    public string SourceSchema { get; set; } = string.Empty;

    /// <summary>
    /// Destination table name in SQLite (no schema prefix).
    /// </summary>
    public string DestinationTable { get; set; } = string.Empty;

    /// <summary>
    /// List of column mappings for this table.
    /// </summary>
    public List<ColumnMapping> Columns { get; set; } = [];
}