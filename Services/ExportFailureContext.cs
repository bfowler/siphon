using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Siphon.Models;

namespace Siphon.Services;

// Keep the original exception type and stack so failure handling remains unchanged.
internal sealed record ExportFailureContext(
    string SourceTable,
    string DestinationTable,
    string Operation,
    long? RowNumber = null,
    long RowsWritten = 0,
    long RowsCommitted = 0,
    string? SourceColumn = null,
    string? SourceType = null,
    string? DestinationColumn = null,
    string? DestinationType = null,
    int? ColumnOrdinal = null)
{
    private const string DataKey = "Siphon.ExportFailureContext";

    public static ExportFailureContext? From(Exception exception) => exception.Data[DataKey] as ExportFailureContext;

    public void Attach(Exception exception)
    {
        if (From(exception) is null) exception.Data[DataKey] = this;
    }

    public static string DescribeSourceType(ColumnMapping column) =>
        column.SourceType is "decimal" or "numeric" && column.NumericPrecision is not null && column.NumericScale is not null
            ? $"{column.SourceType}({column.NumericPrecision},{column.NumericScale})"
            : column.SourceType;

    public static void WriteTo(TextWriter output, Exception exception)
    {
        output.WriteLine($"  Exception type: {exception.GetType().FullName}");
        if (exception is SqlException sql)
            output.WriteLine($"  SQL Server error number: {sql.Number}; state: {sql.State}; severity: {sql.Class}");
        if (exception is SqliteException sqlite)
            output.WriteLine($"  SQLite error code: {sqlite.SqliteErrorCode}; extended code: {sqlite.SqliteExtendedErrorCode}");
        if (From(exception) is not { } context) return;

        output.WriteLine($"  Source table: {context.SourceTable}");
        output.WriteLine($"  Destination table: {context.DestinationTable}");
        output.WriteLine($"  Operation: {context.Operation}");
        if (context.RowNumber is not null)
            output.WriteLine($"  Source row position: {context.RowNumber}");
        if (context.SourceColumn is not null)
        {
            output.WriteLine($"  Source column: {context.SourceColumn} (ordinal {context.ColumnOrdinal}, type {context.SourceType})");
            output.WriteLine($"  Destination column: {context.DestinationColumn} (type {context.DestinationType})");
        }
        output.WriteLine($"  Rows written: {context.RowsWritten}; rows committed: {context.RowsCommitted}");
        output.WriteLine("  Output may be incomplete. Uncommitted rows are rolled back; previously committed batches remain.");
    }
}
