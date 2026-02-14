using System.Data;
using Microsoft.Data.SqlClient;
using Siphon.Models;

namespace Siphon.Services;

/// <summary>
/// Service for reading schema and data from SQL Server databases.
/// </summary>
public class SqlServerReader : IAsyncDisposable
{
    private readonly ConnectionOptions _options;
    private readonly string _connectionString;
    private SqlConnection? _connection;

    /// <summary>
    /// Initializes a new instance of the <see cref="SqlServerReader"/> class.
    /// </summary>
    /// <param name="options">The connection options for the SQL Server database.</param>
    public SqlServerReader(ConnectionOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _connectionString = BuildConnectionString();
    }

    /// <summary>
    /// Tests if the connection to SQL Server is valid.
    /// </summary>
    /// <returns>True if the connection succeeds; otherwise, false.</returns>
    public async Task<bool> TestConnectionAsync()
    {
        try
        {
            await EnsureConnectionAsync();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Retrieves the schema information for a specified table.
    /// </summary>
    /// <param name="schemaName">The schema name of the table.</param>
    /// <param name="tableName">The name of the table.</param>
    /// <returns>A <see cref="TableMapping"/> containing column information.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the table does not exist.</exception>
    public async Task<TableMapping> GetTableSchemaAsync(string schemaName, string tableName)
    {
        await EnsureConnectionAsync();

        var mapping = new TableMapping
        {
            SourceSchema = schemaName,
            SourceTable = $"{schemaName}.{tableName}",
            DestinationTable = tableName
        };

        var columnsDict = new Dictionary<string, ColumnMapping>();

        var columnQuery = @"
            SELECT 
                COLUMN_NAME,
                DATA_TYPE,
                IS_NULLABLE
            FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = @schemaName AND TABLE_NAME = @tableName
            ORDER BY ORDINAL_POSITION";

        await using (var cmd = new SqlCommand(columnQuery, _connection))
        {
            cmd.Parameters.AddWithValue("@schemaName", schemaName);
            cmd.Parameters.AddWithValue("@tableName", tableName);

            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var columnName = reader.GetString(0);
                var dataType = reader.GetString(1);
                var isNullable = reader.GetString(2) == "YES";

                columnsDict[columnName] = new ColumnMapping
                {
                    SourceColumn = columnName,
                    SourceType = dataType,
                    DestinationColumn = columnName,
                    DestinationType = TypeMapper.MapToSqlite(dataType),
                    IsNullable = isNullable,
                    IsPrimaryKey = false
                };
            }
        }

        if (columnsDict.Count == 0)
        {
            throw new InvalidOperationException($"Table '{schemaName}.{tableName}' does not exist or has no columns.");
        }

        var pkQuery = @"
            SELECT kcu.COLUMN_NAME
            FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
            INNER JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE kcu
                ON tc.CONSTRAINT_NAME = kcu.CONSTRAINT_NAME
                AND tc.TABLE_SCHEMA = kcu.TABLE_SCHEMA
            WHERE tc.TABLE_SCHEMA = @schemaName 
                AND tc.TABLE_NAME = @tableName
                AND tc.CONSTRAINT_TYPE = 'PRIMARY KEY'
            ORDER BY kcu.ORDINAL_POSITION";

        await using (var cmd = new SqlCommand(pkQuery, _connection))
        {
            cmd.Parameters.AddWithValue("@schemaName", schemaName);
            cmd.Parameters.AddWithValue("@tableName", tableName);

            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var pkColumn = reader.GetString(0);
                if (columnsDict.TryGetValue(pkColumn, out var column))
                {
                    column.IsPrimaryKey = true;
                }
            }
        }

        mapping.Columns = [.. columnsDict.Values];
        return mapping;
    }

    /// <summary>
    /// Streams data from a table using a forward-only cursor.
    /// </summary>
    /// <param name="schemaName">The schema name of the table.</param>
    /// <param name="tableName">The name of the table.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>An async enumerable of <see cref="IDataRecord"/> objects.</returns>
    public async IAsyncEnumerable<IDataRecord> ReadTableDataAsync(
        string schemaName, 
        string tableName, 
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await EnsureConnectionAsync(cancellationToken);

        var query = $"SELECT * FROM [{schemaName}].[{tableName}]";

        await using var cmd = new SqlCommand(query, _connection);
        await using var reader = await cmd.ExecuteReaderAsync(
            CommandBehavior.SequentialAccess, 
            cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return reader;
        }
    }

    /// <summary>
    /// Returns the approximate row count for a table.
    /// </summary>
    /// <param name="schemaName">The schema name of the table.</param>
    /// <param name="tableName">The name of the table.</param>
    /// <returns>The approximate number of rows in the table.</returns>
    public async Task<long> GetRowCountAsync(string schemaName, string tableName)
    {
        await EnsureConnectionAsync();

        var query = @"
            SELECT SUM(p.rows)
            FROM sys.partitions p
            INNER JOIN sys.tables t ON p.object_id = t.object_id
            INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
            WHERE s.name = @schemaName 
                AND t.name = @tableName
                AND p.index_id IN (0, 1)";

        await using var cmd = new SqlCommand(query, _connection);
        cmd.Parameters.AddWithValue("@schemaName", schemaName);
        cmd.Parameters.AddWithValue("@tableName", tableName);

        var result = await cmd.ExecuteScalarAsync();
        return result == null || result == DBNull.Value ? 0 : Convert.ToInt64(result);
    }

    /// <summary>
    /// Disposes the connection asynchronously.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_connection != null)
        {
            await _connection.DisposeAsync();
            _connection = null;
        }
    }

    private string BuildConnectionString()
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = _options.Server,
            InitialCatalog = _options.Database,
            TrustServerCertificate = true,
            ConnectTimeout = 30
        };

        if (_options.UseWindowsAuth)
        {
            builder.IntegratedSecurity = true;
        }
        else
        {
            builder.UserID = _options.Username;
            builder.Password = _options.Password;
        }

        return builder.ConnectionString;
    }

    private async Task EnsureConnectionAsync(CancellationToken cancellationToken = default)
    {
        if (_connection == null)
        {
            _connection = new SqlConnection(_connectionString);
            await _connection.OpenAsync(cancellationToken);
        }
        else if (_connection.State != ConnectionState.Open)
        {
            await _connection.OpenAsync(cancellationToken);
        }
    }
}
