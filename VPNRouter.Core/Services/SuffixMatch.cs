#nullable enable
using System;
using System.Collections.Generic;

namespace VPNRouter.Core.Services;

/// <summary>
/// F3 (v2.45.0): longest-suffix matcher used to map a urltest member tag
/// (<c>"&lt;protocol&gt;-&lt;ServerName&gt;"</c>) back to its subscription row.
/// Server names can contain '-', so the longest candidate that is a suffix of
/// the tag wins. Single pass, no allocation/sort — replaces a per-poll LINQ
/// <c>Where(...).OrderByDescending(...).FirstOrDefault()</c> on the stats poll.
/// </summary>
public static class SuffixMatch
{
    /// <summary>Index of the item whose <paramref name="name"/> is the LONGEST
    /// suffix of <paramref name="nowTag"/>, or -1 if none match / tag empty.</summary>
    public static int LongestSuffixIndex<T>(IReadOnlyList<T> items, Func<T, string?> name, string? nowTag)
    {
        if (items is null || string.IsNullOrEmpty(nowTag)) return -1;
        int best = -1, bestLen = -1;
        for (int i = 0; i < items.Count; i++)
        {
            var n = name(items[i]);
            if (string.IsNullOrEmpty(n)) continue;
            if (n!.Length > bestLen && nowTag!.EndsWith(n, StringComparison.Ordinal))
            {
                best = i;
                bestLen = n.Length;
            }
        }
        return best;
    }
}
