#nullable enable
using System;
using System.Collections.Generic;

namespace VPNRouter.Core.Services;

public static class SuffixMatch
{
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
