namespace Siphon.Models;

/// <summary>
/// Configuration options for database connection and extraction settings.
/// </summary>
public class ConnectionOptions
{
    /// <summary>
    /// SQL Server hostname or instance name.
    /// </summary>
    public string Server { get; set; } = string.Empty;

    /// <summary>
    /// Source database name on SQL Server.
    /// </summary>
    public string Database { get; set; } = string.Empty;

    /// <summary>
    /// Optional SQL authentication username.
    /// If null and UseWindowsAuth is false, will prompt for credentials.
    /// </summary>
    public string? Username { get; set; }

    /// <summary>
    /// Optional SQL authentication password.
    /// If null and UseWindowsAuth is false, will prompt for credentials.
    /// </summary>
    public string? Password { get; set; }

    /// <summary>
    /// Whether to use Windows integrated authentication.
    /// </summary>
    public bool UseWindowsAuth { get; set; } = false;

    /// <summary>
    /// Output path for the SQLite database file.
    /// </summary>
    public string OutputPath { get; set; } = string.Empty;

    /// <summary>
    /// List of table names to extract from the source database.
    /// </summary>
    public List<string> Tables { get; set; } = [];

    /// <summary>
    /// Whether to overwrite an existing SQLite file at OutputPath.
    /// </summary>
    public bool Overwrite { get; set; } = false;
}