using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace VPNRouter.Core.Services;

/// <summary>
/// Shared bounded cache for wildcard process-name patterns.
/// Both the Windows and macOS scanners use the same compiled regex shape and
/// timeout, so keeping one cache avoids unbounded growth on either platform.
/// </summary>
internal static class ProcessPatternRegexCache
{
    internal const int MaxCachedPatterns = 256;
    internal const int MatchTimeoutMilliseconds = 250;

    private static readonly ConcurrentDictionary<string, Regex> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly object Gate = new();

    internal static Regex Get(string pattern)
    {
        if (Cache.TryGetValue(pattern, out var cached))
            return cached;

        lock (Gate)
        {
            if (Cache.TryGetValue(pattern, out cached))
                return cached;

            var regex = Create(pattern);
            if (Cache.Count >= MaxCachedPatterns)
                TrimOneLocked();

            Cache[pattern] = regex;
            return regex;
        }
    }

    internal static int Count => Cache.Count;

    internal static void ResetForTests()
    {
        lock (Gate)
            Cache.Clear();
    }

    private static void TrimOneLocked()
    {
        foreach (var key in Cache.Keys)
        {
            if (Cache.TryRemove(key, out _))
                return;
        }
    }

    private static Regex Create(string pattern)
    {
        var regexPattern = "^" + Regex.Escape(pattern)
            .Replace(@"\*", ".*")
            .Replace(@"\?", ".") + "$";

        return new Regex(regexPattern,
            RegexOptions.IgnoreCase | RegexOptions.Compiled,
            TimeSpan.FromMilliseconds(MatchTimeoutMilliseconds));
    }
}
