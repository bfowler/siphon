using Siphon.Models;

namespace Siphon.Services;

internal static class ExportPreflight
{
    public static bool ContainsWildcard(string spec) => spec.Contains('*') || spec.Contains('?');

    public static async Task<List<TableMapping>> BuildAsync(
        SqlServerReader reader, IReadOnlyList<string> specs, Action<string, int>? reportMatches = null)
    {
        var available = specs.Any(ContainsWildcard) ? await reader.DiscoverTablesAsync() : [];
        var identities = new List<SourceTableIdentity>();
        var seen = new HashSet<int>();
        foreach (var spec in specs)
        {
            IEnumerable<SourceTableIdentity> matches;
            if (ContainsWildcard(spec))
            {
                var expanded = available.Where(t => WildcardMatcher.IsMatch(spec, t.DisplayName)).ToList();
                reportMatches?.Invoke(spec, expanded.Count);
                matches = expanded;
            }
            else
            {
                var parts = spec.Split('.', 2, StringSplitOptions.TrimEntries);
                var schema = parts.Length == 2 ? parts[0] : "dbo";
                var table = parts.Length == 2 ? parts[1] : parts[0];
                matches = [await reader.ResolveTableAsync(schema, table)];
            }
            foreach (var identity in matches)
                if (seen.Add(identity.ObjectId)) identities.Add(identity);
        }

        if (identities.Count == 0)
            throw new InvalidOperationException("No tables matched the requested selection.");

        var mappings = new List<TableMapping>();
        foreach (var identity in identities)
            mappings.Add(await reader.GetTableSchemaAsync(identity));
        ValidateDestinations(mappings);
        return mappings;
    }

    internal static void ValidateDestinations(IReadOnlyList<TableMapping> mappings)
    {
        var comparer = SqliteIdentifierComparer.Instance;
        var collision = mappings.GroupBy(t => t.DestinationTable, comparer).FirstOrDefault(g => g.Count() > 1);
        if (collision is not null)
            throw new InvalidOperationException($"Destination table '{collision.Key}' would receive multiple source tables: {string.Join(", ", collision.Select(t => t.SourceTable))}. Export these tables to separate output files.");

        foreach (var mapping in mappings)
        {
            var name = mapping.DestinationTable;
            if (name.Length >= 7 && comparer.Equals(name[..7], "sqlite_"))
                throw new InvalidOperationException($"Source table '{mapping.SourceTable}' has reserved SQLite destination name '{name}'.");

            var collidingNames = mapping.Columns.GroupBy(c => c.DestinationColumn, comparer)
                .Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(comparer);
            if (collidingNames.Count > 0)
            {
                var columns = mapping.Columns.Where(c => collidingNames.Contains(c.DestinationColumn)).Select(c => c.SourceColumn);
                throw new InvalidOperationException($"Source table '{mapping.SourceTable}' has conflicting SQLite column names: {string.Join(", ", columns)}.");
            }
        }
    }
}
