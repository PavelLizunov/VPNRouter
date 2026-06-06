using System;
using System.Collections.Generic;
using System.Linq;
using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

/// <summary>
/// r8 #6: single source of truth for NaiveProxy ↔ UDP-sibling pairing.
///
/// <para>NaiveProxy is TCP-only (HTTP/2 CONNECT, or HTTP/3 over QUIC — neither
/// carries arbitrary UDP), so to make UDP apps work a naive server is paired
/// with a co-located QUIC-native carrier (Hysteria2 / TUIC) that takes the UDP
/// half. Both <see cref="ConfigGenerator"/> (to actually route UDP through the
/// sibling) and the Servers-list UI (to label a naive row "naive + hy2") MUST
/// use the SAME rule — otherwise the UI could claim a pairing the generator
/// wouldn't make, which is exactly what the naive-hy2 review flagged (#6).</para>
/// </summary>
public static class NaivePairing
{
    /// <summary>True when the entry is a NaiveProxy server.</summary>
    public static bool IsNaive(VlessServerEntry? s) =>
        "naive".Equals(s?.Protocol, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A naive UDP sibling must be a QUIC-native carrier — Hysteria2 / TUIC.
    /// VLESS / Shadowsocks / unknown are rejected so we never auto-pair a
    /// carrier that may not carry UDP (and never skip the QUIC reject for it).
    /// </summary>
    public static bool IsUdpCapable(VlessServerEntry? s) =>
        (s?.Protocol ?? "").ToLowerInvariant() is "hysteria2" or "hy2" or "tuic";

    /// <summary>
    /// Find the UDP-capable sibling for a naive server, in priority order:
    /// <list type="number">
    /// <item><see cref="VlessServerEntry.PairGroup"/> tag match (bulletproof —
    /// the subscription marks naive + its same-node HY2 with the same value).</item>
    /// <item>Base-name match — strip the protocol token and compare the
    /// remainder (transition fallback before a refresh ships the tag).</item>
    /// </list>
    /// Returns null when no UDP-capable sibling exists (caller keeps naive
    /// TCP-only).
    /// </summary>
    public static VlessServerEntry? FindUdpSibling(VlessServerEntry naive, IEnumerable<VlessServerEntry> pool)
    {
        if (naive == null || pool == null) return null;
        var list = pool as IReadOnlyList<VlessServerEntry> ?? pool.ToList();

        // 1. PairGroup tag.
        if (!string.IsNullOrWhiteSpace(naive.PairGroup))
        {
            var byTag = list.Where(s => !IsNaive(s)
                && string.Equals(s.PairGroup, naive.PairGroup, StringComparison.OrdinalIgnoreCase));
            var pick = PreferUdp(byTag);
            if (pick != null) return pick;
        }

        // 2. Base-name fallback — ONLY when unambiguous (r9 follow-up #3). Matching
        // by stripped display name can pair the WRONG node if two share a base name
        // and neither has a pair= tag; the UDP exit IP would then differ from the
        // naive TCP path. So require EXACTLY ONE UDP-capable candidate — otherwise
        // don't guess (keep naive TCP-only). PairGroup above stays authoritative.
        var baseName = StripProtocolToken(naive.Name);
        if (!string.IsNullOrWhiteSpace(baseName))
        {
            var byName = list.Where(s => !IsNaive(s) && IsUdpCapable(s)
                && string.Equals(StripProtocolToken(s.Name), baseName, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (byName.Count == 1) return byName[0];
        }
        return null;
    }

    // Among UDP-capable candidates, prefer Hysteria2 then TUIC.
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

    /// <summary>
    /// Strip the protocol token from a server display name so co-located entries
    /// pair: "Latvia NAIVE ~brat" and "Latvia HY2 ~brat" both reduce to
    /// "Latvia ~brat".
    /// </summary>
    public static string StripProtocolToken(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        var tokens = new[] { "naive", "hysteria2", "hy2", "tuic", "shadowsocks", "ss", "vless" };
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => !tokens.Contains(w.ToLowerInvariant()));
        return string.Join(" ", words).Trim();
    }
}
