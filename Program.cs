using System.CommandLine;
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

    return await RunAsync(options);
});

return await rootCommand.Parse(args).InvokeAsync();

static async Task<int> RunAsync(ConnectionOptions options)
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
            return 1;
        }
        ConsoleExtensions.WriteSuccess("Connected successfully.");
        Console.WriteLine();

        var hasWildcards = options.Tables.Any(ExportPreflight.ContainsWildcard);
        if (hasWildcards)
            ConsoleExtensions.WriteInfo("Discovering tables for wildcard patterns...");
        var mappings = await ExportPreflight.BuildAsync(reader, options.Tables, (pattern, count) =>
        {
            if (count == 0)
            {
                ConsoleExtensions.WriteWarning($"  Pattern '{pattern}' matched no tables");
            }
            else
            {
                ConsoleExtensions.WriteSuccess($"  Pattern '{pattern}' matched {count} table(s)");
            }
        });
        Console.WriteLine();

        using var writer = new SqliteWriter(options.OutputPath);
        await writer.InitializeAsync(options.Overwrite);
        ConsoleExtensions.WriteSuccess($"Created SQLite database: {options.OutputPath}");
        Console.WriteLine();

        var result = await TableExportRunner.RunAsync(mappings,
            mapping => ExportTableAsync(mapping, reader, writer),
            (_, ex) =>
        {
            ConsoleExtensions.WriteError($"  Error: {ex.Message}");
            Console.WriteLine();
        });

        ConsoleExtensions.WriteInfo("Summary:");
        if (hasWildcards)
        {
            Console.WriteLine($"  Patterns specified: {options.Tables.Count}");
            Console.WriteLine($"  Tables processed:   {result.TablesProcessed}");
        }
        else
        {
            Console.WriteLine($"  Tables processed: {result.TablesProcessed}");
        }
        if (result.TablesFailed > 0)
            Console.WriteLine($"  Tables failed:    {result.TablesFailed}");
        Console.WriteLine($"  Total rows:       {result.TotalRows:N0}");
        Console.WriteLine($"  Output file:      {Path.GetFullPath(options.OutputPath)}");
        Console.WriteLine();
        if (result.ExitCode == 0)
            ConsoleExtensions.WriteSuccess(result.CompletionMessage);
        else
            ConsoleExtensions.WriteError(result.CompletionMessage);
        return result.ExitCode;
    }
    catch (InvalidOperationException ex)
    {
        ConsoleExtensions.WriteError($"Error: {ex.Message}");
        return 1;
    }
    catch (SqlException ex)
    {
        ConsoleExtensions.WriteError($"SQL Server error: {ex.Message}");
        return 1;
    }
    catch (SqliteException ex)
    {
        ConsoleExtensions.WriteError($"SQLite error: {ex.Message}");
        return 1;
    }
    catch (Exception ex)
    {
        ConsoleExtensions.WriteError($"Error: {ex.Message}");
        Console.WriteLine();
        Console.WriteLine("Stack trace:");
        Console.WriteLine(ex.StackTrace);
        return 1;
    }
}

static async Task<long> ExportTableAsync(TableMapping mapping, SqlServerReader reader, SqliteWriter writer)
{
    var identity = mapping.SourceIdentity!;
    ConsoleExtensions.WriteInfo($"Processing table: {mapping.SourceTable}");
    Console.WriteLine($"  Columns: {mapping.Columns.Count}");
    await writer.CreateTableAsync(mapping);
    ConsoleExtensions.WriteSuccess($"  Created table: {mapping.DestinationTable}");

    var rowCount = await reader.GetRowCountAsync(identity.SchemaName, identity.TableName);
    Console.WriteLine($"  Source rows: {rowCount:N0}");
    long inserted = 0;
    if (rowCount > 0)
    {
        var lastProgress = 0;
        inserted = await writer.InsertDataAsync(mapping, reader.ReadTableDataAsync(mapping),
            batchSize: 1000, progressCallback: rows =>
            {
                var progress = (int)Math.Min(rows, rowCount);
                if (progress > lastProgress)
                {
                    ConsoleExtensions.WriteProgress(progress, (int)rowCount, identity.TableName);
                    lastProgress = progress;
                }
            });
        ConsoleExtensions.WriteSuccess($"  Transferred {inserted:N0} rows");
    }

    Console.WriteLine();
    return inserted;
}
