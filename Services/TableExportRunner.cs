using Siphon.Models;

namespace Siphon.Services;

internal sealed record TableExportResult(long TotalRows, int TablesProcessed, int TablesFailed)
{
    public int ExitCode => TablesFailed == 0 ? 0 : 1;
    public string CompletionMessage => TablesFailed == 0
        ? "Extraction completed successfully."
        : "Extraction completed with errors.";
}

internal static class TableExportRunner
{
    public static async Task<TableExportResult> RunAsync(
        IReadOnlyList<TableMapping> mappings,
        Func<TableMapping, Task<long>> transfer,
        Action<TableMapping, InvalidOperationException> reportFailure)
    {
        if (mappings.Count == 0)
            throw new InvalidOperationException("No tables matched the requested selection.");

        long rows = 0;
        var processed = 0;
        var failed = 0;
        foreach (var mapping in mappings)
        {
            try
            {
                rows += await transfer(mapping);
                processed++;
            }
            catch (InvalidOperationException ex)
            {
                failed++;
                reportFailure(mapping, ex);
            }
        }
        return new TableExportResult(rows, processed, failed);
    }
}
