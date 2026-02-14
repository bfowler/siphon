using System;
using System.Data;
using System.Text;
using System.Threading.Tasks;
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
        dropCommand.CommandText = $"DROP TABLE IF EXISTS [{tableMapping.DestinationTable}];";
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

        SqliteTransaction? transaction = null;

        try
        {
            transaction = (SqliteTransaction)await _connection.BeginTransactionAsync();

            await foreach (var record in records)
            {
                using var command = _connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = insertSql;

                for (int i = 0; i < columns.Count; i++)
                {
                    var column = columns[i];
                    var value = record.GetValue(i);
                    var convertedValue = TypeMapper.ConvertValue(value, column.SourceType);
                    var parameter = command.CreateParameter();
                    parameter.ParameterName = $"@p{i}";
                    parameter.Value = convertedValue ?? DBNull.Value;
                    command.Parameters.Add(parameter);
                }

                await command.ExecuteNonQueryAsync();
                totalRows++;
                batchCount++;

                if (batchCount >= batchSize)
                {
                    await transaction.CommitAsync();
                    await transaction.DisposeAsync();
                    progressCallback?.Invoke((int)totalRows);
                    batchCount = 0;
                    transaction = (SqliteTransaction)await _connection.BeginTransactionAsync();
                }
            }

            if (batchCount > 0)
            {
                await transaction.CommitAsync();
                progressCallback?.Invoke((int)totalRows);
            }
        }
        finally
        {
            if (transaction != null)
                await transaction.DisposeAsync();
        }

        return totalRows;
    }

    private static string BuildCreateTableSql(TableMapping tableMapping)
    {
        var sb = new StringBuilder();
        sb.Append($"CREATE TABLE [{tableMapping.DestinationTable}] (");

        var primaryKeys = tableMapping.Columns
            .Where(c => c.IsPrimaryKey)
            .Select(c => c.DestinationColumn)
            .ToList();

        for (int i = 0; i < tableMapping.Columns.Count; i++)
        {
            var column = tableMapping.Columns[i];
            if (i > 0)
                sb.Append(", ");

            sb.Append($"[{column.DestinationColumn}] {column.DestinationType}");

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
                sb.Append($"[{primaryKeys[i]}]");
            }
            sb.Append(")");
        }

        sb.Append(");");
        return sb.ToString();
    }

    private static string BuildInsertSql(TableMapping tableMapping)
    {
        var sb = new StringBuilder();
        sb.Append($"INSERT INTO [{tableMapping.DestinationTable}] (");

        for (int i = 0; i < tableMapping.Columns.Count; i++)
        {
            if (i > 0)
                sb.Append(", ");
            sb.Append($"[{tableMapping.Columns[i].DestinationColumn}]");
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