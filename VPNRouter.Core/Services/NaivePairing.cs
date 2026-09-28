using System;
using System.Collections.Generic;
using System.Linq;
using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

public static class NaivePairing
{
    public static bool IsNaive(VlessServerEntry? s) =>
        "naive".Equals(s?.Protocol, StringComparison.OrdinalIgnoreCase);

    public static bool IsUdpCapable(VlessServerEntry? s) =>
        (s?.Protocol ?? "").ToLowerInvariant() is "hysteria2" or "hy2" or "tuic";

    public static VlessServerEntry? FindUdpSibling(
        VlessServerEntry naive, IEnumerable<VlessServerEntry> pool,
        Func<VlessServerEntry, bool>? isAlive = null)
    {
        if (naive == null || pool == null) return null;
        var list = pool as IReadOnlyList<VlessServerEntry> ?? pool.ToList();
        bool Alive(VlessServerEntry s) => isAlive == null || isAlive(s);

        if (!string.IsNullOrWhiteSpace(naive.PairGroup))
        {
            var byTag = list.Where(s => !IsNaive(s) && Alive(s)
                && string.Equals(s.PairGroup, naive.PairGroup, StringComparison.OrdinalIgnoreCase));
            var pick = PreferUdp(byTag);
            if (pick != null) return pick;
        }

        var baseName = StripProtocolToken(naive.Name);
        if (!string.IsNullOrWhiteSpace(baseName))
        {
            var byName = list.Where(s => !IsNaive(s) && IsUdpCapable(s) && Alive(s)
                && string.Equals(StripProtocolToken(s.Name), baseName, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (byName.Count == 1) return byName[0];
        }

        if (isAlive != null)
        {
            if (!string.IsNullOrWhiteSpace(naive.Server))
            {
                var sameHost = list.Where(s => !IsNaive(s) && IsUdpCapable(s) && Alive(s)
                    && string.Equals(s.Server, naive.Server, StringComparison.OrdinalIgnoreCase));
                var pickSameHost = PreferUdp(sameHost);
                if (pickSameHost != null) return pickSameHost;
            }

            var anyAlive = list.Where(s => !IsNaive(s) && IsUdpCapable(s) && Alive(s));
            var pick = PreferUdp(anyAlive);
            if (pick != null) return pick;
        }
        return null;
    }

    private static VlessServerEntry? PreferUdp(IEnumerable<VlessServerEntry> candidates)
    {
        static int Rank(VlessServerEntry s) => (s.Protocol ?? "").ToLowerInvariant() switch
        {
            "hysteria2" => 0,
            "hy2"       => 0,
            "tuic"      => 1,
            _           => 2,
        };
        return candidates.Where(IsUdpCapable).OrderBy(Rank).FirstOrDefault();
    }

    public static string StripProtocolToken(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        var tokens = new[] { "naive", "hysteria2", "hy2", "tuic", "shadowsocks", "ss", "vless" };
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => !tokens.Contains(w.ToLowerInvariant()));
        return string.Join(" ", words).Trim();
    }
}
