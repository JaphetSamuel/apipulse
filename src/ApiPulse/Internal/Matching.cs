namespace ApiPulse.Internal;

/// <summary>Case-insensitive glob matching supporting <c>*</c> (any run of characters) and <c>?</c> (one character).</summary>
internal static class Glob
{
    public static bool IsMatch(string pattern, string input)
    {
        int p = 0, i = 0, star = -1, mark = 0;
        while (i < input.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || char.ToUpperInvariant(pattern[p]) == char.ToUpperInvariant(input[i])))
            {
                p++;
                i++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                mark = i;
            }
            else if (star >= 0)
            {
                p = star + 1;
                i = ++mark;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*')
        {
            p++;
        }

        return p == pattern.Length;
    }

    public static bool HasWildcard(string value) => value.AsSpan().IndexOfAny('*', '?') >= 0;
}

/// <summary>A set of exact names and glob patterns. Exact names are a hash lookup; only wildcard patterns are scanned.</summary>
internal sealed class NameMatcher
{
    private readonly HashSet<string> _exact = new(StringComparer.OrdinalIgnoreCase);
    private readonly string[] _globs;

    public NameMatcher(IEnumerable<string>? patterns, Func<string, string>? normalize = null)
    {
        var globs = new List<string>();
        foreach (var raw in patterns ?? Enumerable.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var pattern = normalize is null ? raw.Trim() : normalize(raw.Trim());
            if (Glob.HasWildcard(pattern))
            {
                globs.Add(pattern);
            }
            else
            {
                _exact.Add(pattern);
            }
        }

        _globs = globs.ToArray();
    }

    public bool IsEmpty => _exact.Count == 0 && _globs.Length == 0;

    public bool IsMatch(string name)
    {
        if (_exact.Contains(name))
        {
            return true;
        }

        foreach (var glob in _globs)
        {
            if (Glob.IsMatch(glob, name))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>Matches request paths against exclusion patterns, ignoring case and trailing slashes.</summary>
internal sealed class PathMatcher
{
    private readonly NameMatcher _matcher;

    public PathMatcher(IEnumerable<string>? patterns)
    {
        _matcher = new NameMatcher(patterns, Normalize);
    }

    public bool IsEmpty => _matcher.IsEmpty;

    public bool IsMatch(string? path) => !_matcher.IsEmpty && _matcher.IsMatch(Normalize(path ?? "/"));

    private static string Normalize(string path)
    {
        if (path.Length == 0 || path[0] != '/')
        {
            path = "/" + path;
        }

        return path.Length > 1 && path[^1] == '/' ? path.TrimEnd('/') : path;
    }
}
