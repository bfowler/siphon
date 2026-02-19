using System.CommandLine;
using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Siphon.Extensions;
using Siphon.Models;
using Siphon.Services;

var serverOption = new Option<string>("--server", "-s")
{
    Description = "SQL Server hostname or instance name"
};

var databaseOption = new Option<string>("--database", "-d")
{
    Description = "Source database name"
};

var tableOption = new Option<List<string>>("--table", "-t")
{
    Description = "Table names to extract (supports wildcards: * and ?)",
    AllowMultipleArgumentsPerToken = true
};

var outputOption = new Option<string>("--output", "-o")
{
    Description = "Output SQLite file path"
};

var usernameOption = new Option<string?>("--username", "-u")
{
    Description = "SQL authentication username"
};

var passwordOption = new Option<string?>("--password", "-p")
{
    Description = "SQL authentication password"
};

var windowsAuthOption = new Option<bool>("--windows-auth", "-w")
{
    Description = "Use Windows integrated authentication",
    DefaultValueFactory = _ => false
};

var overwriteOption = new Option<bool>("--overwrite")
{
    Description = "Overwrite existing SQLite file",
    DefaultValueFactory = _ => false
};

var rootCommand = new RootCommand("Siphon - Extract SQL Server tables to SQLite")
{
    serverOption,
    databaseOption,
    tableOption,
    outputOption,
    usernameOption,
    passwordOption,
    windowsAuthOption,
    overwriteOption
};

rootCommand.SetAction(async parseResult =>
{
    var server = parseResult.GetValue(serverOption);
    var database = parseResult.GetValue(databaseOption);
    var tables = parseResult.GetValue(tableOption);
    var output = parseResult.GetValue(outputOption);
    var username = parseResult.GetValue(usernameOption);
    var password = parseResult.GetValue(passwordOption);
    var windowsAuth = parseResult.GetValue(windowsAuthOption);
    var overwrite = parseResult.GetValue(overwriteOption);

    if (string.IsNullOrEmpty(server))
    {
        server = ConsoleExtensions.PromptString("SQL Server hostname or instance");
    }

    if (string.IsNullOrEmpty(database))
    {
        database = ConsoleExtensions.PromptString("Source database name");
    }

    if (tables == null || tables.Count == 0)
    {
        var tablesInput = ConsoleExtensions.PromptString("Table names (comma-separated, schema.table format optional)");
        tables = tablesInput
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
    }

    if (string.IsNullOrEmpty(output))
    {
        output = ConsoleExtensions.PromptString("Output SQLite file path");
    }

    if (!windowsAuth && string.IsNullOrEmpty(username))
    {
        var useWindows = ConsoleExtensions.PromptYesNo("Use Windows authentication?", true);
        if (useWindows)
        {
            windowsAuth = true;
        }
        else
        {
            username = ConsoleExtensions.PromptString("SQL authentication username");
            password = ConsoleExtensions.PromptPassword("SQL authentication password");
        }
    }
    else if (!windowsAuth && !string.IsNullOrEmpty(username) && string.IsNullOrEmpty(password))
    {
        password = ConsoleExtensions.PromptPassword("SQL authentication password");
    }

    var options = new ConnectionOptions
    {
        Server = server!,
        Database = database!,
        Tables = tables!,
        OutputPath = output!,
        Username = username,
        Password = password,
        UseWindowsAuth = windowsAuth,
        Overwrite = overwrite
    };

    await RunAsync(options);
});

return rootCommand.Parse(args).Invoke();

static async Task RunAsync(ConnectionOptions options)
{
    ConsoleExtensions.WriteInfo("Siphon - SQL Server to SQLite Extractor");
    Console.WriteLine();

    ConsoleExtensions.WriteInfo("Connection Settings:");
    Console.WriteLine($"  Server:   {options.Server}");
    Console.WriteLine($"  Database: {options.Database}");
    Console.WriteLine($"  Auth:     {(options.UseWindowsAuth ? "Windows" : "SQL")}");
    Console.WriteLine($"  Tables:   {string.Join(", ", options.Tables)}");
    Console.WriteLine($"  Output:   {options.OutputPath}");
    Console.WriteLine();

    try
    {
        await using var reader = new SqlServerReader(options);

        ConsoleExtensions.WriteInfo("Testing SQL Server connection...");
        var connected = await reader.TestConnectionAsync();
        if (!connected)
        {
            ConsoleExtensions.WriteError("Failed to connect to SQL Server. Check your connection settings.");
            Environment.Exit(1);
            return;
        }
        ConsoleExtensions.WriteSuccess("Connected successfully.");
        Console.WriteLine();

        var expandedTables = new List<string>();
        var hasWildcards = options.Tables.Any(ContainsWildcard);

        if (hasWildcards)
        {
            ConsoleExtensions.WriteInfo("Discovering tables for wildcard patterns...");
            var allTables = await reader.GetAllTablesAsync();

            foreach (var tableSpec in options.Tables)
            {
                if (ContainsWildcard(tableSpec))
                {
                    var matches = allTables
                        .Where(t => WildcardMatcher.IsMatch(tableSpec, t))
                        .ToList();

                    if (matches.Count == 0)
                    {
                        ConsoleExtensions.WriteWarning($"  Pattern '{tableSpec}' matched no tables");
                    }
                    else
                    {
                        ConsoleExtensions.WriteSuccess($"  Pattern '{tableSpec}' matched {matches.Count} table(s)");
                        expandedTables.AddRange(matches);
                    }
                }
                else
                {
                    expandedTables.Add(tableSpec);
                }
            }

            expandedTables = expandedTables.Distinct().ToList();
            Console.WriteLine();
        }
        else
        {
            expandedTables = options.Tables;
        }

        using var writer = new SqliteWriter(options.OutputPath);
        await writer.InitializeAsync(options.Overwrite);
        ConsoleExtensions.WriteSuccess($"Created SQLite database: {options.OutputPath}");
        Console.WriteLine();

        var totalRows = 0L;
        var tablesProcessed = 0;

        foreach (var tableSpec in expandedTables)
        {
            var (schema, tableName) = ParseTableName(tableSpec);

            ConsoleExtensions.WriteInfo($"Processing table: {schema}.{tableName}");

            try
            {
                var tableMapping = await reader.GetTableSchemaAsync(schema, tableName);
                Console.WriteLine($"  Columns: {tableMapping.Columns.Count}");

                await writer.CreateTableAsync(tableMapping);
                ConsoleExtensions.WriteSuccess($"  Created table: {tableName}");

                var rowCount = await reader.GetRowCountAsync(schema, tableName);
                Console.WriteLine($"  Source rows: {rowCount:N0}");

                if (rowCount > 0)
                {
                    var lastProgress = 0;

                    var records = reader.ReadTableDataAsync(schema, tableName);
                    var inserted = await writer.InsertDataAsync(
                        tableMapping,
                        records,
                        batchSize: 1000,
                        progressCallback: rows =>
                        {
                            if (rowCount > 0)
                            {
                                var progress = (int)Math.Min(rows, rowCount);
                                if (progress > lastProgress)
                                {
                                    ConsoleExtensions.WriteProgress(progress, (int)rowCount, $"{tableName}");
                                    lastProgress = progress;
                                }
                            }
                        });

                    totalRows += inserted;
                    ConsoleExtensions.WriteSuccess($"  Transferred {inserted:N0} rows");
                }

                tablesProcessed++;
                Console.WriteLine();
            }
            catch (InvalidOperationException ex)
            {
                ConsoleExtensions.WriteError($"  Error: {ex.Message}");
                Console.WriteLine();
            }
        }

        ConsoleExtensions.WriteInfo("Summary:");
        if (hasWildcards)
        {
            Console.WriteLine($"  Patterns specified: {options.Tables.Count}");
            Console.WriteLine($"  Tables processed:   {tablesProcessed}");
        }
        else
        {
            Console.WriteLine($"  Tables processed: {tablesProcessed}");
        }
        Console.WriteLine($"  Total rows:       {totalRows:N0}");
        Console.WriteLine($"  Output file:      {Path.GetFullPath(options.OutputPath)}");
        Console.WriteLine();
        ConsoleExtensions.WriteSuccess("Extraction completed successfully.");
    }
    catch (SqlException ex)
    {
        ConsoleExtensions.WriteError($"SQL Server error: {ex.Message}");
        Environment.Exit(1);
    }
    catch (SqliteException ex)
    {
        ConsoleExtensions.WriteError($"SQLite error: {ex.Message}");
        Environment.Exit(1);
    }
    catch (Exception ex)
    {
        ConsoleExtensions.WriteError($"Error: {ex.Message}");
        Console.WriteLine();
        Console.WriteLine("Stack trace:");
        Console.WriteLine(ex.StackTrace);
        Environment.Exit(1);
    }
}

static (string schema, string table) ParseTableName(string tableSpec)
{
    var parts = tableSpec.Split('.', 2, StringSplitOptions.TrimEntries);
    return parts.Length == 2
        ? (parts[0], parts[1])
        : ("dbo", parts[0]);
}

static bool ContainsWildcard(string tableSpec)
{
    return tableSpec.Contains('*') || tableSpec.Contains('?');
}
