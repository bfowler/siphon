using Microsoft.Data.Sqlite;
using Siphon.Models;
using Siphon.Services;

namespace Siphon.Tests;

public sealed class PreflightValidationTests
{
    [Theory]
    [InlineData("Customer", "customer", true)]
    [InlineData("É", "é", false)]
    [InlineData("selectA", "selectB", false)]
    public void DestinationComparisonMatchesBundledSqlite(string first, string second, bool collision)
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"CREATE TABLE {SqlIdentifier.Sqlite(first)} (Id INTEGER)";
        command.ExecuteNonQuery();
        command.CommandText = $"CREATE TABLE {SqlIdentifier.Sqlite(second)} (Id INTEGER)";
        var sqliteFailure = Record.Exception(() => command.ExecuteNonQuery());
        var validationFailure = Record.Exception(() => ExportPreflight.ValidateDestinations([Mapping(first), Mapping(second)]));
        Assert.Equal(collision, sqliteFailure is SqliteException);
        Assert.Equal(collision, validationFailure is InvalidOperationException);
        var names = new HashSet<string>(SqliteIdentifierComparer.Instance) { first, second };
        Assert.Equal(collision ? 1 : 2, names.Count);
    }

    [Fact]
    public void ColumnCollisionDiagnosticUsesMappingOrdinalOrderAcrossGroups()
    {
        var mapping = Mapping("Columns");
        mapping.Columns = new[] { "Id", "Code", "id", "code" }.Select(name => new ColumnMapping
        {
            SourceColumn = name, DestinationColumn = name
        }).ToList();
        var error = Assert.Throws<InvalidOperationException>(() => ExportPreflight.ValidateDestinations([mapping]));
        Assert.Equal("Source table 'dbo.Columns' has conflicting SQLite column names: Id, Code, id, code.", error.Message);
    }

    private static TableMapping Mapping(string name) => new() { SourceTable = "dbo." + name, DestinationTable = name };
}
