using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Siphon.Tests;

internal sealed class TestWorkspace : IDisposable
{
    private static readonly string Parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "siphon-tests"));
    public string Root { get; } = Path.Combine(Parent, Guid.NewGuid().ToString("N"));

    public TestWorkspace() => Directory.CreateDirectory(Root);
    public string FilePath(string name = "output.db") => Path.Combine(Root, name);

    public void Dispose()
    {
        // Only this fixture's GUID directory is eligible for recursive cleanup.
        var target = Path.GetFullPath(Root);
        if (Path.GetDirectoryName(target) != Parent || !Guid.TryParseExact(Path.GetFileName(target), "N", out _))
            throw new InvalidOperationException("Unsafe test cleanup target.");
        foreach (var path in Directory.EnumerateFiles(target, "*.db", SearchOption.AllDirectories))
        {
            using var poolKey = new SqliteConnection($"Data Source={path};Cache=Shared;Mode=ReadWriteCreate");
            SqliteConnection.ClearPool(poolKey);
        }
        Directory.Delete(target, recursive: true);
    }
}

internal sealed record ProcessResult(int ExitCode, string Output, string Error)
{
    public string[] Lines => Output.Split('\n').Select(line => line.TrimEnd('\r')).ToArray();
    public long Metric(string label) => long.Parse(Regex.Match(Output,
        $@"(?m)^\s*{Regex.Escape(label)}:\s*([\d,]+)\s*$").Groups[1].Value.Replace(",", ""));
}

internal static class TestProcess
{
    public static Task<ProcessResult> Cli(params string[] args) => Run("dotnet",
        [Path.Combine(AppContext.BaseDirectory, "cli", "siphon.dll"), .. args]);

    public static async Task<ProcessResult> Run(string command, string[] args)
    {
        var start = new ProcessStartInfo(command)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Cannot start {command}.");
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw new TimeoutException($"{command} did not finish within 60 seconds.");
        }
        return new ProcessResult(process.ExitCode, await output, await error);
    }
}

internal static class SqliteTestData
{
    public static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ConnectionString);
        connection.Open();
        return connection;
    }

    public static async Task SeedSentinel(string path)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE Sentinel (Value TEXT); INSERT INTO Sentinel VALUES ('preserve me');";
        await command.ExecuteNonQueryAsync();
    }

    public static object? Scalar(string path, string sql)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }
}
