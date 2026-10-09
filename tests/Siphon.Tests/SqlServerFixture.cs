using Microsoft.Data.SqlClient;

namespace Siphon.Tests;

public sealed class SqlServerFixture : IAsyncLifetime
{
    private readonly string _instance = "SiphonTests" + Guid.NewGuid().ToString("N");
    private bool _created;
    private bool _started;
    public string Server => @"(localdb)\" + _instance;

    public SqlConnection Connect(string database = "FixturesCI") => new(new SqlConnectionStringBuilder
    {
        DataSource = Server, InitialCatalog = database, IntegratedSecurity = true,
        TrustServerCertificate = true, Pooling = false, ConnectTimeout = 15
    }.ConnectionString);

    public async Task InitializeAsync()
    {
        if (!OperatingSystem.IsWindows())
            throw new InvalidOperationException("SqlServerIntegration tests require Windows and SqlLocalDB.");
        try
        {
            var create = await TestProcess.Run("SqlLocalDB", ["create", _instance]);
            if (create.ExitCode != 0) throw new InvalidOperationException(create.Output + create.Error);
            _created = true;
            var start = await TestProcess.Run("SqlLocalDB", ["start", _instance]);
            if (start.ExitCode != 0) throw new InvalidOperationException(start.Output + start.Error);
            _started = true;
            await Execute("CREATE DATABASE FixturesCI COLLATE Latin1_General_100_CI_AS; CREATE DATABASE FixturesCS COLLATE Latin1_General_100_CS_AS;", "master");
            await Execute("CREATE SCHEMA sales;");
            await Execute("CREATE SCHEMA [odd]] schema];");
            await Execute("""
                CREATE TABLE dbo.Customer (Id int NOT NULL PRIMARY KEY, Marker nvarchar(40) NULL);
                INSERT dbo.Customer VALUES (1, N'dbo row');
                CREATE TABLE sales.Customer (Id int NOT NULL PRIMARY KEY, Marker nvarchar(40) NULL);
                INSERT sales.Customer VALUES (2, N'sales row');
                CREATE TABLE dbo.EmptyTable (Id int NOT NULL PRIMARY KEY);
                CREATE TABLE dbo.BulkTable (Id int NOT NULL, Part int NOT NULL, Marker nvarchar(40) NULL, PRIMARY KEY (Id, Part));
                INSERT dbo.BulkTable
                SELECT TOP (1005) ROW_NUMBER() OVER (ORDER BY a.object_id, b.object_id), 7, NULL
                FROM sys.all_objects a CROSS JOIN sys.all_objects b;
                CREATE TABLE dbo.TemporalSample (
                    Id int NOT NULL PRIMARY KEY,
                    ValidFrom datetime2 GENERATED ALWAYS AS ROW START HIDDEN NOT NULL,
                    ValidTo datetime2 GENERATED ALWAYS AS ROW END HIDDEN NOT NULL,
                    Marker nvarchar(40) NOT NULL,
                    PERIOD FOR SYSTEM_TIME (ValidFrom, ValidTo)
                ) WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = dbo.TemporalHistory));
                INSERT dbo.TemporalSample (Id, Marker) VALUES (1, N'temporal row');
                CREATE TABLE [odd]] schema].[select]] "table"] ([key]] "id"] int NOT NULL PRIMARY KEY, [value "label]]] nvarchar(40) NULL);
                INSERT [odd]] schema].[select]] "table"] VALUES (1, N'quoted row');
                CREATE TABLE dbo.[Name.With.Dot] (Id int NOT NULL PRIMARY KEY);
                INSERT dbo.[Name.With.Dot] VALUES (1);
                CREATE TABLE dbo.sqlite_Reserved (Id int NOT NULL PRIMARY KEY);
                CREATE TABLE dbo.ReadFailure (Id int NOT NULL PRIMARY KEY, Divisor int NOT NULL, Broken AS (1 / Divisor));
                INSERT dbo.ReadFailure VALUES (1, 0);
                CREATE TABLE dbo.MarkedBaseTable (Id int NOT NULL PRIMARY KEY);
                INSERT dbo.MarkedBaseTable VALUES (17);
                EXEC sys.sp_MS_marksystemobject @objname = N'dbo.MarkedBaseTable';
                """);
            await Execute("CREATE VIEW dbo.CustomerView AS SELECT * FROM dbo.Customer;");
            await Execute("""
                CREATE TABLE dbo.Customer (Id int NOT NULL);
                CREATE TABLE dbo.customer (Id int NOT NULL);
                CREATE TABLE dbo.ColumnCollision (Id int NOT NULL, id int NULL);
                CREATE TABLE dbo.[É] ([É] int NOT NULL);
                CREATE TABLE dbo.[é] ([é] int NOT NULL);
                INSERT dbo.[É] VALUES (1);
                INSERT dbo.[é] VALUES (2);
                CREATE TABLE dbo.UnicodeColumns ([É] int NOT NULL, [é] int NOT NULL);
                INSERT dbo.UnicodeColumns VALUES (1, 2);
                """, "FixturesCS");
        }
        catch (Exception setupFailure)
        {
            try
            {
                await DisposeAsync();
            }
            catch (Exception cleanupFailure)
            {
                throw new AggregateException("SQL fixture setup and cleanup failed.", setupFailure, cleanupFailure);
            }
            throw;
        }
    }

    public async Task Execute(string sql, string database = "FixturesCI")
    {
        await using var connection = Connect(database);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        if (!_created) return;
        var failures = new List<Exception>();
        // Always attempt instance cleanup even if connecting/dropping a fixture database fails.
        if (_started)
        {
            try
            {
                await using var connection = Connect("master");
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    IF DB_ID('FixturesCI') IS NOT NULL BEGIN
                        ALTER DATABASE FixturesCI SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                        DROP DATABASE FixturesCI;
                    END;
                    IF DB_ID('FixturesCS') IS NOT NULL BEGIN
                        ALTER DATABASE FixturesCS SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                        DROP DATABASE FixturesCS;
                    END;
                    """;
                await command.ExecuteNonQueryAsync();
            }
            catch (Exception ex)
            {
                failures.Add(ex);
            }
            await CleanupCommand("stop");
        }
        await CleanupCommand("delete");
        if (failures.Count > 0) throw new AggregateException("SQL fixture cleanup failed.", failures);

        async Task CleanupCommand(string operation)
        {
            try
            {
                var result = await TestProcess.Run("SqlLocalDB", [operation, _instance]);
                if (result.ExitCode != 0) throw new InvalidOperationException(result.Output + result.Error);
                if (operation == "stop") _started = false;
                if (operation == "delete") _created = false;
            }
            catch (Exception ex)
            {
                failures.Add(ex);
            }
        }
    }
}
