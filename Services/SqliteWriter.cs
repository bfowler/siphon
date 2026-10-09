using System;
using System.Data;
using System.Data.SqlTypes;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Siphon.Models;

namespace Siphon.Services;

/// <summary>
/// Service that writes data to a SQLite database file.
/// </summary>
public sealed class SqliteWriter : IDisposable
{
    private readonly string _outputPath;
    private SqliteConnection? _connection;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="SqliteWriter"/> class.
    /// </summary>
    /// <param name="outputPath">The path to the SQLite database file.</param>
    public SqliteWriter(string outputPath)
    {
        _outputPath = outputPath ?? throw new ArgumentNullException(nameof(outputPath));
    }

    /// <summary>
    /// Creates or opens the SQLite database file.
    /// </summary>
    /// <param name="overwrite">If true and the file exists, it will be deleted first.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task InitializeAsync(bool overwrite = false)
    {
        if (overwrite && File.Exists(_outputPath))
        {
            File.Delete(_outputPath);
        }

        var directory = Path.GetDirectoryName(_outputPath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var connectionString = $"Data Source={_outputPath};Cache=Shared;Mode=ReadWriteCreate";
        _connection = new SqliteConnection(connectionString);
        await _connection.OpenAsync();

        using (var command = _connection.CreateCommand())
        {
            command.CommandText = "PRAGMA journal_mode=WAL;";
            await command.ExecuteNonQueryAsync();
        }

        using (var command = _connection.CreateCommand())
        {
            command.CommandText = "PRAGMA foreign_keys=ON;";
            await command.ExecuteNonQueryAsync();
        }
    }

    /// <summary>
    /// Creates a table in the SQLite database from the specified mapping.
    /// </summary>
    /// <param name="tableMapping">The table mapping containing column definitions.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task CreateTableAsync(TableMapping tableMapping)
    {
        if (_connection == null)
            throw new InvalidOperationException("Database not initialized. Call InitializeAsync first.");

        using var dropCommand = _connection.CreateCommand();
        dropCommand.CommandText = $"DROP TABLE IF EXISTS {SqlIdentifier.Sqlite(tableMapping.DestinationTable)};";
        await dropCommand.ExecuteNonQueryAsync();

        var createSql = BuildCreateTableSql(tableMapping);
        using var createCommand = _connection.CreateCommand();
        createCommand.CommandText = createSql;
        await createCommand.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Inserts data into the specified table in batches.
    /// </summary>
    /// <param name="tableMapping">The table mapping containing column definitions.</param>
    /// <param name="records">The data records to insert.</param>
    /// <param name="batchSize">The number of rows per transaction batch.</param>
    /// <param name="progressCallback">Optional callback invoked with the row count periodically.</param>
    /// <returns>The total number of rows inserted.</returns>
    public async Task<long> InsertDataAsync(
        TableMapping tableMapping,
        IAsyncEnumerable<IDataRecord> records,
        int batchSize = 1000,
        Action<int>? progressCallback = null)
    {
        if (_connection == null)
            throw new InvalidOperationException("Database not initialized. Call InitializeAsync first.");

        var insertSql = BuildInsertSql(tableMapping);
        var columns = tableMapping.Columns;
        long totalRows = 0;
        int batchCount = 0;
        long committedRows = 0;
        long? rowNumber = null;
        int? columnOrdinal = null;
        var operation = "Begin SQLite batch";

        SqliteTransaction? transaction = null;

        try
        {
            transaction = (SqliteTransaction)await _connection.BeginTransactionAsync();

            operation = "Read source row";
            rowNumber = 1;
            await foreach (var record in records)
            {
                using var command = _connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = insertSql;

                for (int i = 0; i < columns.Count; i++)
                {
                    var column = columns[i];
                    columnOrdinal = i;
                    operation = "Read column";
                    var value = ReadValue(record, i);
                    operation = "Convert column";
                    var convertedValue = TypeMapper.ConvertValue(value, column.SourceType);
                    operation = "Bind parameter";
                    var parameter = command.CreateParameter();
                    parameter.ParameterName = $"@p{i}";
                    parameter.Value = convertedValue ?? DBNull.Value;
                    command.Parameters.Add(parameter);
                }

                columnOrdinal = null;
                operation = "Insert SQLite row";
                await command.ExecuteNonQueryAsync();
                totalRows++;
                batchCount++;

                if (batchCount >= batchSize)
                {
                    operation = "Commit SQLite batch";
                    await transaction.CommitAsync();
                    committedRows = totalRows;
                    operation = "Dispose SQLite batch";
                    await transaction.DisposeAsync();
                    operation = "Report progress";
                    progressCallback?.Invoke((int)totalRows);
                    batchCount = 0;
                    operation = "Begin SQLite batch";
                    transaction = (SqliteTransaction)await _connection.BeginTransactionAsync();
                }
                operation = "Read source row";
                rowNumber = totalRows + 1;
            }

            if (batchCount > 0)
            {
                rowNumber = totalRows;
                operation = "Commit SQLite batch";
                await transaction.CommitAsync();
                committedRows = totalRows;
                operation = "Report progress";
                progressCallback?.Invoke((int)totalRows);
            }
        }
        catch (Exception exception)
        {
            var column = columnOrdinal is { } ordinal ? columns[ordinal] : null;
            new ExportFailureContext(tableMapping.SourceTable, tableMapping.DestinationTable, operation,
                rowNumber, totalRows, committedRows, column?.SourceColumn,
                column is null ? null : ExportFailureContext.DescribeSourceType(column),
                column?.DestinationColumn, column?.DestinationType,
                columnOrdinal is null ? null : columnOrdinal + 1).Attach(exception);
            throw;
        }
        finally
        {
            if (transaction != null)
                await transaction.DisposeAsync();
        }

        return totalRows;
    }

    private static object ReadValue(IDataRecord record, int ordinal)
    {
        if (record is SqlDataReader reader)
        {
            // GetValue narrows SQL decimal/numeric to System.Decimal, which cannot
            // represent SQL Server's full precision and scale. Preserve SqlDecimal
            // until the explicit conversion to SQLite REAL (or sql_variant TEXT).
            if (reader.GetProviderSpecificFieldType(ordinal) == typeof(SqlDecimal))
                return reader.GetSqlDecimal(ordinal);
            if (reader.GetDataTypeName(ordinal) == "sql_variant")
                return reader.GetSqlValue(ordinal);
        }
        return record.GetValue(ordinal);
    }

    private static string BuildCreateTableSql(TableMapping tableMapping)
    {
        var sb = new StringBuilder();
        sb.Append($"CREATE TABLE {SqlIdentifier.Sqlite(tableMapping.DestinationTable)} (");

        var primaryKeys = tableMapping.Columns
            .Where(c => c.IsPrimaryKey)
            .Select(c => c.DestinationColumn)
            .ToList();

        for (int i = 0; i < tableMapping.Columns.Count; i++)
        {
            var column = tableMapping.Columns[i];
            if (i > 0)
                sb.Append(", ");

            sb.Append($"{SqlIdentifier.Sqlite(column.DestinationColumn)} {column.DestinationType}");

            if (!column.IsNullable)
                sb.Append(" NOT NULL");
        }

        if (primaryKeys.Count > 0)
        {
            sb.Append(", PRIMARY KEY (");
            for (int i = 0; i < primaryKeys.Count; i++)
            {
                if (i > 0)
                    sb.Append(", ");
                sb.Append(SqlIdentifier.Sqlite(primaryKeys[i]));
            }
            sb.Append(")");
        }

        sb.Append(");");
        return sb.ToString();
    }

    private static string BuildInsertSql(TableMapping tableMapping)
    {
        var sb = new StringBuilder();
        sb.Append($"INSERT INTO {SqlIdentifier.Sqlite(tableMapping.DestinationTable)} (");

        for (int i = 0; i < tableMapping.Columns.Count; i++)
        {
            if (i > 0)
                sb.Append(", ");
            sb.Append(SqlIdentifier.Sqlite(tableMapping.Columns[i].DestinationColumn));
        }

        sb.Append(") VALUES (");

        for (int i = 0; i < tableMapping.Columns.Count; i++)
        {
            if (i > 0)
                sb.Append(", ");
            sb.Append($"@p{i}");
        }

        sb.Append(");");
        return sb.ToString();
    }

    /// <summary>
    /// Closes the database connection and releases resources.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _connection?.Close();
        _connection?.Dispose();
        _connection = null;
        _disposed = true;
    }
}
