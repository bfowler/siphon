namespace Siphon.Services;

// SQLite folds ASCII A-Z when comparing identifiers; non-ASCII characters stay distinct.
internal sealed class SqliteIdentifierComparer : IEqualityComparer<string>
{
    public static SqliteIdentifierComparer Instance { get; } = new();

    public bool Equals(string? x, string? y)
    {
        if (x is null || y is null) return x == y;
        if (x.Length != y.Length) return false;
        for (var i = 0; i < x.Length; i++)
            if (Fold(x[i]) != Fold(y[i])) return false;
        return true;
    }

    public int GetHashCode(string value)
    {
        var hash = new HashCode();
        foreach (var c in value) hash.Add(Fold(c));
        return hash.ToHashCode();
    }

    private static char Fold(char c) => c is >= 'A' and <= 'Z' ? (char)(c + ('a' - 'A')) : c;
}
