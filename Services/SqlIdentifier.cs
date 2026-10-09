namespace Siphon.Services;

internal static class SqlIdentifier
{
    public static string SqlServer(string name) => "[" + name.Replace("]", "]]") + "]";
    public static string Sqlite(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";
}
