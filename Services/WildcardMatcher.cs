using System;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace Siphon.Services;

/// <summary>
/// Static class that converts shell-style glob patterns to regex patterns and matches strings.
/// </summary>
public static class WildcardMatcher
{
    private static readonly ConcurrentDictionary<string, Regex> _regexCache = new();

    private static readonly char[] RegexSpecialChars = { '.', '+', '(', ')', '[', ']', '{', '}', '^', '$', '|', '\\' };

    /// <summary>
    /// Determines whether the specified input string matches the given glob pattern.
    /// </summary>
    /// <param name="pattern">The glob pattern to match against. Supports * (zero or more characters) and ? (exactly one character).</param>
    /// <param name="input">The input string to test against the pattern.</param>
    /// <returns>true if the input matches the pattern; otherwise, false.</returns>
    public static bool IsMatch(string pattern, string input)
    {
        if (pattern == null)
            throw new ArgumentNullException(nameof(pattern));

        if (input == null)
            throw new ArgumentNullException(nameof(input));

        var regex = _regexCache.GetOrAdd(pattern, p =>
        {
            var regexPattern = GlobToRegex(p);
            return new Regex(regexPattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
        });

        return regex.IsMatch(input);
    }

    /// <summary>
    /// Converts a shell-style glob pattern to a regex pattern.
    /// </summary>
    /// <param name="glob">The glob pattern to convert.</param>
    /// <returns>A regex pattern string with ^ and $ anchors.</returns>
    private static string GlobToRegex(string glob)
    {
        var result = new System.Text.StringBuilder();

        foreach (var c in glob)
        {
            if (c == '*')
            {
                result.Append(".*");
            }
            else if (c == '?')
            {
                result.Append('.');
            }
            else if (Array.IndexOf(RegexSpecialChars, c) >= 0)
            {
                result.Append('\\');
                result.Append(c);
            }
            else
            {
                result.Append(c);
            }
        }

        return "^" + result.ToString() + "$";
    }
}
