using System.Data;
using Microsoft.Data.Sqlite;
using Siphon.Models;
using Siphon.Services;

namespace Siphon.Tests;

public sealed class SqliteWriterTests
{
    [Theory]
    [InlineData("select", "key]")]
    [InlineData("table] \"name\"", "key] \"id\"")]
    public async Task QuotedNamesPreserveRowsAndPrimaryKeyAfterRecreation(string table, string column)
    {
        using var workspace = new TestWorkspace();
        var path = workspace.FilePath();
        using var writer = new SqliteWriter(path);
        await writer.InitializeAsync();
        var mapping = Mapping(table, column);
        for (var run = 0; run < 2; run++)
        {
            await writer.CreateTableAsync(mapping);
            Assert.Equal(1, await writer.InsertDataAsync(mapping, Records(1)));
            Assert.Equal(1L, SqliteTestData.Scalar(path,
                $"SELECT {SqlIdentifier.Sqlite(column)} FROM {SqlIdentifier.Sqlite(table)}"));
            await Assert.ThrowsAsync<SqliteException>(() => writer.InsertDataAsync(mapping, Records(1)));
        }
    }

    [Fact]
    public async Task RecoverableTransferFailureReturnsFailureAndCountsOnlyCompletedTables()
    {
        using var workspace = new TestWorkspace();
        var path = workspace.FilePath();
        using var writer = new SqliteWriter(path);
        await writer.InitializeAsync();
        var broken = Mapping("Broken", "Id");
        var good = Mapping("Good", "Id");
        var failures = new List<(TableMapping Mapping, string Message)>();
        var result = await TableExportRunner.RunAsync([broken, good], async mapping =>
        {
            await writer.CreateTableAsync(mapping);
            return await writer.InsertDataAsync(mapping, mapping == broken
                ? Records(1001, new InvalidOperationException("Synthetic stream failure."))
                : Records(1));
        }, (mapping, ex) => failures.Add((mapping, ex.Message)));

        Assert.Equal(1, result.ExitCode);
        Assert.Equal("Extraction completed with errors.", result.CompletionMessage);
        Assert.Equal(1, result.TablesFailed);
        Assert.Equal(1, result.TablesProcessed);
        Assert.Equal(1, result.TotalRows);
        Assert.Equal((broken, "Synthetic stream failure."), Assert.Single(failures));
        Assert.Equal(1000L, SqliteTestData.Scalar(path, "SELECT COUNT(*) FROM Broken"));
        Assert.Equal(1L, SqliteTestData.Scalar(path, "SELECT COUNT(*) FROM Good"));
    }

    [Fact]
    public async Task SqliteFailureAbortsRunAndRollsBackUncommittedBatch()
    {
        using var workspace = new TestWorkspace();
        var path = workspace.FilePath();
        using var writer = new SqliteWriter(path);
        await writer.InitializeAsync();
        var broken = Mapping("Broken", "Id");
        var good = Mapping("Good", "Id");
        var attempted = new List<TableMapping>();
        await Assert.ThrowsAsync<SqliteException>(() => TableExportRunner.RunAsync([broken, good], async mapping =>
        {
            attempted.Add(mapping);
            await writer.CreateTableAsync(mapping);
            return await writer.InsertDataAsync(mapping, Records(1001, repeatLast: true));
        }, (_, _) => Assert.Fail("A fatal SQLite error must not be treated as a recoverable table failure.")));
        Assert.Equal(broken, Assert.Single(attempted));
        Assert.Equal(1000L, SqliteTestData.Scalar(path, "SELECT COUNT(*) FROM Broken"));
        Assert.Equal(0L, SqliteTestData.Scalar(path, "SELECT COUNT(*) FROM sqlite_master WHERE name='Good'"));
    }

    [Fact]
    public async Task CompleteTransferHasSuccessfulResultAndActualRowCount()
    {
        using var workspace = new TestWorkspace();
        var path = workspace.FilePath();
        using var writer = new SqliteWriter(path);
        await writer.InitializeAsync();
        var mapping = Mapping("Complete", "Id");
        var result = await TableExportRunner.RunAsync([mapping], async table =>
        {
            await writer.CreateTableAsync(table);
            return await writer.InsertDataAsync(table, Records(1005));
        }, (_, _) => Assert.Fail("The complete transfer should not fail."));
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("Extraction completed successfully.", result.CompletionMessage);
        Assert.Equal(0, result.TablesFailed);
        Assert.Equal(1, result.TablesProcessed);
        Assert.Equal(1005, result.TotalRows);
        Assert.Equal(1005L, SqliteTestData.Scalar(path, "SELECT COUNT(*) FROM Complete"));
    }

    private static TableMapping Mapping(string table, string column) => new()
    {
        SourceTable = "dbo." + table,
        DestinationTable = table,
        Columns = [new ColumnMapping
        {
            SourceColumn = column, DestinationColumn = column, SourceType = "int", DestinationType = "INTEGER",
            IsNullable = false, IsPrimaryKey = true
        }]
    };

    private static async IAsyncEnumerable<IDataRecord> Records(int count, Exception? failure = null, bool repeatLast = false)
    {
        using var table = new DataTable();
        table.Columns.Add("Id", typeof(int));
        for (var i = 1; i <= count; i++) table.Rows.Add(repeatLast && i == count ? 1 : i);
        using var reader = table.CreateDataReader();
        while (await reader.ReadAsync()) yield return reader;
        if (failure is not null) throw failure;
    }
}
