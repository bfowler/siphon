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
        var error = await Assert.ThrowsAsync<SqliteException>(() => TableExportRunner.RunAsync([broken, good], async mapping =>
        {
            attempted.Add(mapping);
            await writer.CreateTableAsync(mapping);
            return await writer.InsertDataAsync(mapping, Records(1001, repeatLast: true));
        }, (_, _) => Assert.Fail("A fatal SQLite error must not be treated as a recoverable table failure.")));
        var context = Assert.IsType<ExportFailureContext>(ExportFailureContext.From(error));
        Assert.Equal("Insert SQLite row", context.Operation);
        Assert.Equal(1001, context.RowNumber);
        Assert.Equal(1000, context.RowsWritten);
        Assert.Equal(1000, context.RowsCommitted);
        Assert.Null(context.SourceColumn); // Constraint errors cannot reliably identify one column.
        Assert.Equal(broken, Assert.Single(attempted));
        Assert.Equal(1000L, SqliteTestData.Scalar(path, "SELECT COUNT(*) FROM Broken"));
        Assert.Equal(0L, SqliteTestData.Scalar(path, "SELECT COUNT(*) FROM sqlite_master WHERE name='Good'"));
    }

    [Fact]
    public async Task ColumnReadFailurePreservesExceptionAndReportsRolledBackBatch()
    {
        using var workspace = new TestWorkspace();
        var path = workspace.FilePath();
        using var writer = new SqliteWriter(path);
        await writer.InitializeAsync();
        var mapping = Mapping("ColumnFailure", "Id");
        mapping.Columns.Add(new ColumnMapping
        {
            SourceColumn = "Fraction", SourceType = "decimal", NumericPrecision = 38, NumericScale = 35,
            DestinationColumn = "FractionCopy", DestinationType = "REAL"
        });
        await writer.CreateTableAsync(mapping);
        var failure = new OverflowException("Synthetic conversion overflow.");
        var error = await Assert.ThrowsAsync<OverflowException>(() => writer.InsertDataAsync(mapping, FailingRecords(failure)));
        Assert.Same(failure, error);
        Assert.Equal(new ExportFailureContext("dbo.ColumnFailure", "ColumnFailure", "Read column",
            1002, 1001, 1000, "Fraction", "decimal(38,35)", "FractionCopy", "REAL", 2),
            ExportFailureContext.From(error));
        Assert.Equal(1000L, SqliteTestData.Scalar(path, "SELECT COUNT(*) FROM ColumnFailure"));
        using var output = new StringWriter();
        ExportFailureContext.WriteTo(output, error);
        Assert.Contains("  Source column: Fraction (ordinal 2, type decimal(38,35))", output.ToString().Split(Environment.NewLine));
    }

    private static async IAsyncEnumerable<IDataRecord> FailingRecords(Exception failure)
    {
        using var table = new DataTable();
        table.Columns.Add("Id", typeof(int));
        table.Columns.Add("Fraction", typeof(decimal));
        for (var i = 1; i <= 1001; i++) table.Rows.Add(i, 0.125m);
        using var reader = table.CreateDataReader();
        while (await reader.ReadAsync()) yield return reader;
        yield return new FailingRecord(failure);
    }

    private sealed class FailingRecord(Exception failure) : IDataRecord
    {
        public object GetValue(int i) => i == 0 ? 1002 : throw failure;
        public int FieldCount => 2;
        public object this[int i] => GetValue(i);
        public object this[string name] => throw new NotSupportedException();
        public string GetName(int i) => throw new NotSupportedException();
        public string GetDataTypeName(int i) => throw new NotSupportedException();
        public Type GetFieldType(int i) => throw new NotSupportedException();
        public int GetValues(object[] values) => throw new NotSupportedException();
        public int GetOrdinal(string name) => throw new NotSupportedException();
        public bool GetBoolean(int i) => throw new NotSupportedException();
        public byte GetByte(int i) => throw new NotSupportedException();
        public long GetBytes(int i, long offset, byte[]? buffer, int bufferOffset, int length) => throw new NotSupportedException();
        public char GetChar(int i) => throw new NotSupportedException();
        public long GetChars(int i, long offset, char[]? buffer, int bufferOffset, int length) => throw new NotSupportedException();
        public Guid GetGuid(int i) => throw new NotSupportedException();
        public short GetInt16(int i) => throw new NotSupportedException();
        public int GetInt32(int i) => throw new NotSupportedException();
        public long GetInt64(int i) => throw new NotSupportedException();
        public float GetFloat(int i) => throw new NotSupportedException();
        public double GetDouble(int i) => throw new NotSupportedException();
        public string GetString(int i) => throw new NotSupportedException();
        public decimal GetDecimal(int i) => throw new NotSupportedException();
        public DateTime GetDateTime(int i) => throw new NotSupportedException();
        public IDataReader GetData(int i) => throw new NotSupportedException();
        public bool IsDBNull(int i) => throw new NotSupportedException();
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
