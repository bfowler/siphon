using System.Globalization;
using Microsoft.Data.Sqlite;
using Siphon.Models;
using Siphon.Services;

namespace Siphon.Tests;

[Trait("Category", "SqlServerIntegration")]
public sealed class ExportCliTests(SqlServerFixture source) : IClassFixture<SqlServerFixture>
{
    private Task<ProcessResult> Export(string output, bool overwrite, string database, params string[] tables)
    {
        var args = new List<string> { "-s", source.Server, "-d", database, "-o", output, "-w" };
        foreach (var table in tables) args.AddRange(["-t", table]);
        if (overwrite) args.Add("--overwrite");
        return TestProcess.Cli([.. args]);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CrossSchemaCollisionPreservesOutput(bool wildcard, bool overwrite)
    {
        using var workspace = new TestWorkspace();
        var path = workspace.FilePath();
        await SqliteTestData.SeedSentinel(path);
        var before = await File.ReadAllBytesAsync(path);
        var result = await Export(path, overwrite, "FixturesCI", wildcard ? ["*.Customer"] : ["dbo.Customer", "sales.Customer"]);
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Error: Destination table 'Customer' would receive multiple source tables: dbo.Customer, sales.Customer. Export these tables to separate output files.", result.Lines);
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
        Assert.Equal("preserve me", SqliteTestData.Scalar(path, "SELECT Value FROM Sentinel"));
        Assert.DoesNotContain("Extraction completed successfully.", result.Lines);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnmatchedWildcardPreservesExistingOutput(bool overwrite)
    {
        using var workspace = new TestWorkspace();
        var path = workspace.FilePath();
        await SqliteTestData.SeedSentinel(path);
        var before = await File.ReadAllBytesAsync(path);
        var result = await Export(path, overwrite, "FixturesCI", "missing_schema.*");
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Error: No tables matched the requested selection.", result.Lines);
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
        Assert.Equal("preserve me", SqliteTestData.Scalar(path, "SELECT Value FROM Sentinel"));
    }

    [Theory]
    [InlineData("missing_schema.*")]
    [InlineData("dbo.Missing")]
    [InlineData("*.Customer")]
    public async Task InvalidSelectionDoesNotCreateOutputDirectory(string selection)
    {
        using var workspace = new TestWorkspace();
        var directory = Path.Combine(workspace.Root, "absent");
        var result = await Export(Path.Combine(directory, "output.db"), true, "FixturesCI", selection);
        Assert.Equal(1, result.ExitCode);
        Assert.False(Directory.Exists(directory));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task MissingExactTableFailsBeforeAnyOutputChanges(bool mixed, bool overwrite)
    {
        using var workspace = new TestWorkspace();
        var path = workspace.FilePath();
        await SqliteTestData.SeedSentinel(path);
        var before = await File.ReadAllBytesAsync(path);
        var result = await Export(path, overwrite, "FixturesCI", mixed ? ["dbo.Customer", "dbo.Missing"] : ["dbo.Missing"]);
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Error: Table 'dbo.Missing' does not exist or has no columns.", result.Lines);
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
        Assert.Equal("preserve me", SqliteTestData.Scalar(path, "SELECT Value FROM Sentinel"));
        Assert.DoesNotContain("Extraction completed successfully.", result.Lines);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReferencesToSameObjectAreTransferredOnce(bool wildcard)
    {
        using var workspace = new TestWorkspace();
        var path = workspace.FilePath();
        var selections = wildcard ? new[] { "Customer", "dbo.Cust*", "dbo.customer" } : ["Customer", "dbo.Customer", "dbo.customer"];
        var result = await Export(path, false, "FixturesCI", selections);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(1, result.Metric("Tables processed"));
        Assert.Equal(1, result.Metric("Total rows"));
        Assert.Equal(1L, SqliteTestData.Scalar(path, "SELECT COUNT(*) FROM Customer"));
    }

    [Fact]
    public async Task TemporalValuesIncludeHiddenColumnsInSourceOrder()
    {
        using var workspace = new TestWorkspace();
        var path = workspace.FilePath();
        var result = await Export(path, false, "FixturesCI", "dbo.TemporalSample");
        Assert.Equal(0, result.ExitCode);
        await using var connection = source.Connect();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, ValidFrom, ValidTo, Marker FROM dbo.TemporalSample";
        await using var expected = await command.ExecuteReaderAsync();
        Assert.True(await expected.ReadAsync());
        using var destination = SqliteTestData.Open(path);
        using var query = destination.CreateCommand();
        query.CommandText = "SELECT Id, ValidFrom, ValidTo, Marker FROM TemporalSample";
        using var actual = query.ExecuteReader();
        Assert.True(actual.Read());
        Assert.Equal(expected.GetInt32(0), actual.GetInt64(0));
        Assert.Equal(expected.GetDateTime(1).ToString("o", CultureInfo.InvariantCulture), actual.GetString(1));
        Assert.Equal(expected.GetDateTime(2).ToString("o", CultureInfo.InvariantCulture), actual.GetString(2));
        Assert.Equal(expected.GetString(3), actual.GetString(3));
        Assert.False(actual.Read());
    }

    [Fact]
    public async Task QuotedSchemaTableAndColumnsRoundTripAndCanBeRecreated()
    {
        using var workspace = new TestWorkspace();
        var path = workspace.FilePath();
        for (var run = 0; run < 2; run++)
        {
            var result = await Export(path, false, "FixturesCI", "odd] schema.select] \"table\"");
            Assert.Equal(0, result.ExitCode);
            Assert.Equal("quoted row", SqliteTestData.Scalar(path, "SELECT \"value \"\"label]\" FROM \"select] \"\"table\"\"\" WHERE \"key] \"\"id\"\"\"=1"));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ColumnCollisionRejectsWholeSelection(bool overwrite)
    {
        using var workspace = new TestWorkspace();
        var path = workspace.FilePath();
        await SqliteTestData.SeedSentinel(path);
        var before = await File.ReadAllBytesAsync(path);
        var result = await Export(path, overwrite, "FixturesCS", "dbo.Customer", "dbo.ColumnCollision");
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Error: Source table 'dbo.ColumnCollision' has conflicting SQLite column names: Id, id.", result.Lines);
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
        Assert.Equal("preserve me", SqliteTestData.Scalar(path, "SELECT Value FROM Sentinel"));
    }

    [Fact]
    public async Task CaseSensitiveObjectsAreNotDeduplicated()
    {
        using var workspace = new TestWorkspace();
        var path = workspace.FilePath();
        var result = await Export(path, true, "FixturesCS", "dbo.Customer", "dbo.customer");
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Error: Destination table 'Customer' would receive multiple source tables: dbo.Customer, dbo.customer. Export these tables to separate output files.", result.Lines);
        Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData(false, "dbo.sqlite_Reserved")]
    [InlineData(true, "dbo.SQLITE_Reserved")]
    public async Task ReservedDestinationNamePreservesOutput(bool overwrite, string selection)
    {
        using var workspace = new TestWorkspace();
        var path = workspace.FilePath();
        await SqliteTestData.SeedSentinel(path);
        var before = await File.ReadAllBytesAsync(path);
        var result = await Export(path, overwrite, "FixturesCI", selection);
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Error: Source table 'dbo.sqlite_Reserved' has reserved SQLite destination name 'sqlite_Reserved'.", result.Lines);
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
        Assert.Equal("preserve me", SqliteTestData.Scalar(path, "SELECT Value FROM Sentinel"));
    }

    [Fact]
    public async Task UnicodeIdentifiersRemainDistinctInBundledSqlite()
    {
        using var workspace = new TestWorkspace();
        var path = workspace.FilePath();
        var result = await Export(path, false, "FixturesCS", "dbo.É", "dbo.é", "dbo.UnicodeColumns");
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(1L, SqliteTestData.Scalar(path, "SELECT \"É\" FROM \"É\""));
        Assert.Equal(2L, SqliteTestData.Scalar(path, "SELECT \"é\" FROM \"é\""));
        Assert.Equal(3L, SqliteTestData.Scalar(path, "SELECT \"É\" + \"é\" FROM UnicodeColumns"));
    }

    [Fact]
    public async Task WildcardKeepsRawNamesAndWarningsForOtherUnmatchedPatterns()
    {
        using var workspace = new TestWorkspace();
        var path = workspace.FilePath();
        var result = await Export(path, false, "FixturesCI", "dbo.Name*", "missing_schema.*");
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("  Pattern 'missing_schema.*' matched no tables", result.Lines);
        Assert.Equal(1L, SqliteTestData.Scalar(path, "SELECT Id FROM \"Name.With.Dot\""));
    }

    [Fact]
    public async Task EmptyNullableAndCompositeKeyTablesTransferAcrossBatches()
    {
        using var workspace = new TestWorkspace();
        var path = workspace.FilePath();
        var result = await Export(path, false, "FixturesCI", "dbo.EmptyTable", "dbo.BulkTable");
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(2, result.Metric("Tables processed"));
        Assert.Equal(1005, result.Metric("Total rows"));
        Assert.Equal(0L, SqliteTestData.Scalar(path, "SELECT COUNT(*) FROM EmptyTable"));
        Assert.Equal(1005L, SqliteTestData.Scalar(path, "SELECT COUNT(*) FROM BulkTable WHERE Marker IS NULL AND Part=7"));
        using var connection = SqliteTestData.Open(path);
        using var duplicate = connection.CreateCommand();
        duplicate.CommandText = "INSERT INTO BulkTable VALUES (1,7,NULL)";
        Assert.Throws<SqliteException>(() => duplicate.ExecuteNonQuery());
    }

    [Fact]
    public async Task ViewIsRejectedAsAnExactTableSelection()
    {
        using var workspace = new TestWorkspace();
        var path = workspace.FilePath();
        var result = await Export(path, false, "FixturesCI", "dbo.CustomerView");
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Error: Table 'dbo.CustomerView' does not exist or has no columns.", result.Lines);
        Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData("dbo.MarkedBaseTable")]
    [InlineData("dbo.MarkedBase*")]
    public async Task MicrosoftMarkedBaseTablesKeepLegacySelectionScope(string selection)
    {
        // Mark only a synthetic fixture table; no CDC/replication instance is required.
        await using var connection = source.Connect();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT is_ms_shipped,
                (SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES
                 WHERE TABLE_SCHEMA='dbo' AND TABLE_NAME='MarkedBaseTable' AND TABLE_TYPE='BASE TABLE')
            FROM sys.tables WHERE object_id=OBJECT_ID(N'dbo.MarkedBaseTable')
            """;
        await using var metadata = await command.ExecuteReaderAsync();
        Assert.True(await metadata.ReadAsync());
        Assert.True(metadata.GetBoolean(0));
        Assert.Equal(1, metadata.GetInt32(1));

        using var workspace = new TestWorkspace();
        var path = workspace.FilePath();
        var result = await Export(path, false, "FixturesCI", selection);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(17L, SqliteTestData.Scalar(path, "SELECT Id FROM MarkedBaseTable"));
    }

    [Fact]
    public async Task FullPrecisionDecimalsAndVariantsTransferWithExistingStorageTypes()
    {
        using var workspace = new TestWorkspace();
        var path = workspace.FilePath();
        await using var schemaReader = new SqlServerReader(new ConnectionOptions
        {
            Server = source.Server, Database = "FixturesCI", UseWindowsAuth = true
        });
        var mapping = await schemaReader.GetTableSchemaAsync("dbo", "DecimalValues");
        Assert.Collection(mapping.Columns.Where(c => c.SourceType is "decimal" or "numeric"),
            c => Assert.Equal((38, 0), (c.NumericPrecision, c.NumericScale)),
            c => Assert.Equal((38, 38), (c.NumericPrecision, c.NumericScale)),
            c => Assert.Equal((18, 4), (c.NumericPrecision, c.NumericScale)),
            c => Assert.Equal((38, 35), (c.NumericPrecision, c.NumericScale)));
        await using var connection = source.Connect();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, Wide, Fraction, Ordinary, Computed, Variant, Amount, Marker FROM dbo.DecimalValues ORDER BY Id";
        await using var expected = await command.ExecuteReaderAsync();
        Assert.True(await expected.ReadAsync());
        // Establish that this data reproduces the reported SqlClient overflow.
        Assert.Throws<OverflowException>(() => expected.GetValue(1));
        Assert.Throws<OverflowException>(() => expected.GetValue(2));
        Assert.Throws<OverflowException>(() => expected.GetValue(4));

        var result = await Export(path, false, "FixturesCI", "dbo.DecimalValues");
        Assert.True(result.ExitCode == 0, result.Output + result.Error);
        Assert.Equal(3, result.Metric("Total rows"));
        using var destination = SqliteTestData.Open(path);
        using var query = destination.CreateCommand();
        query.CommandText = command.CommandText.Replace("dbo.DecimalValues", "DecimalValues");
        using var actual = query.ExecuteReader();
        do
        {
            Assert.True(actual.Read());
            Assert.Equal(expected.GetInt32(0), actual.GetInt64(0));
            for (var ordinal = 1; ordinal <= 4; ordinal++)
            {
                if (expected.IsDBNull(ordinal)) Assert.True(actual.IsDBNull(ordinal));
                else Assert.Equal(expected.GetSqlDecimal(ordinal).ToDouble(), actual.GetDouble(ordinal));
            }
            if (expected.IsDBNull(5)) Assert.True(actual.IsDBNull(5));
            else Assert.Equal(expected.GetSqlValue(5).ToString(), actual.GetString(5));
            if (expected.IsDBNull(6)) Assert.True(actual.IsDBNull(6));
            else Assert.Equal((double)expected.GetDecimal(6), actual.GetDouble(6));
            if (expected.IsDBNull(7)) Assert.True(actual.IsDBNull(7));
            else Assert.Equal(expected.GetString(7), actual.GetString(7));
        } while (await expected.ReadAsync());
        Assert.False(actual.Read());
        Assert.Equal("real", SqliteTestData.Scalar(path, "SELECT typeof(Wide) FROM DecimalValues WHERE Id=1"));
        Assert.Equal("text", SqliteTestData.Scalar(path, "SELECT typeof(Variant) FROM DecimalValues WHERE Id=1"));
    }

    [Fact]
    public async Task NonDecimalVariantsKeepClrTextRepresentation()
    {
        using var workspace = new TestWorkspace();
        var path = workspace.FilePath();
        var result = await Export(path, false, "FixturesCI", "dbo.VariantValues");
        Assert.True(result.ExitCode == 0, result.Output + result.Error);
        Assert.Equal(17, result.Metric("Total rows"));
        await using var connection = source.Connect();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, Value FROM dbo.VariantValues ORDER BY Id";
        await using var expected = await command.ExecuteReaderAsync();
        using var destination = SqliteTestData.Open(path);
        using var query = destination.CreateCommand();
        query.CommandText = "SELECT Id, Value FROM VariantValues ORDER BY Id";
        using var actual = query.ExecuteReader();
        while (await expected.ReadAsync())
        {
            Assert.True(actual.Read());
            Assert.Equal(expected.GetInt32(0), actual.GetInt64(0));
            if (expected.IsDBNull(1)) Assert.True(actual.IsDBNull(1));
            else Assert.Equal(expected.GetValue(1).ToString(), actual.GetString(1));
        }
        Assert.False(actual.Read());
    }

    [Fact]
    public async Task SqlReadFailureReturnsNonzeroAndLeavesExplicitPartialOutput()
    {
        using var workspace = new TestWorkspace();
        var path = workspace.FilePath();
        await SqliteTestData.SeedSentinel(path);
        var result = await Export(path, true, "FixturesCI", "dbo.Customer", "dbo.ReadFailure");
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("  Source table: dbo.ReadFailure", result.Lines);
        Assert.Contains("  Destination table: ReadFailure", result.Lines);
        Assert.Contains("  Operation: Read source row", result.Lines);
        Assert.Contains("  Source row position: 1", result.Lines);
        Assert.Contains("  Rows written: 0; rows committed: 0", result.Lines);
        Assert.Contains("  Exception type: Microsoft.Data.SqlClient.SqlException", result.Lines);
        Assert.DoesNotContain("Extraction completed successfully.", result.Lines);
        Assert.Equal(1L, SqliteTestData.Scalar(path, "SELECT COUNT(*) FROM Customer"));
        Assert.Equal(0L, SqliteTestData.Scalar(path, "SELECT COUNT(*) FROM ReadFailure"));
        Assert.Equal(0L, SqliteTestData.Scalar(path, "SELECT COUNT(*) FROM sqlite_master WHERE name='Sentinel'"));
    }

    [Fact]
    public async Task PreflightKeepsFirstSelectionOrderAndCanonicalSourceIdentity()
    {
        await using var reader = new SqlServerReader(new ConnectionOptions
        {
            Server = source.Server, Database = "FixturesCI", UseWindowsAuth = true
        });
        var mappings = await ExportPreflight.BuildAsync(reader, ["BulkTable", "dbo.Cust*", "customer", "EmptyTable"]);
        Assert.Equal(new[] { "dbo.BulkTable", "dbo.Customer", "dbo.EmptyTable" }, mappings.Select(t => t.SourceTable));
        Assert.Equal(3, mappings.Select(t => t.SourceIdentity!.ObjectId).Distinct().Count());
        Assert.Equal(new[] { "Id", "Part", "Marker" }, mappings[0].Columns.Select(c => c.SourceColumn));
    }

    [Fact]
    public async Task ConnectionFailureDoesNotModifyExistingOutput()
    {
        using var workspace = new TestWorkspace();
        var path = workspace.FilePath();
        await SqliteTestData.SeedSentinel(path);
        var before = await File.ReadAllBytesAsync(path);
        var result = await Export(path, true, "MissingFixtureDatabase", "dbo.Customer");
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Failed to connect to SQL Server. Check your connection settings.", result.Lines);
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
        Assert.Equal("preserve me", SqliteTestData.Scalar(path, "SELECT Value FROM Sentinel"));
    }
}
