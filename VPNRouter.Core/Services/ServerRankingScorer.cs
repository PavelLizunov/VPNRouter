#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace VPNRouter.Core.Services;

/// <summary>One server's inputs for ranking: an opaque id, its health verdict, and its ASN.</summary>
public sealed record RankableServer(string Id, ServerHealthVerdict Verdict, string Asn);

/// <summary>A ranked server with its computed score (higher = better).</summary>
public sealed record RankedServer(string Id, ServerHealthVerdict Verdict, string Asn, int Score);

/// <summary>
/// Pure, network-free server-ranking scorer for Auto selection. Scores by
/// <see cref="ServerHealthVerdict"/>, penalizes servers whose ASN is flagged high-risk
/// (from <see cref="ServerHealthClassifier.AnalyzeProviderRisk"/>), and breaks ties by ASN
/// diversity (prefer spreading across providers over clustering one high-risk subnet).
///
/// <para>This ONLY produces an order/score — it does NOT change any live selection or the
/// generated <c>urltest</c> group. Wiring it into Auto is deferred + behaviour-changing
/// (<c>plans/urltest-verification-deferred-risky-2026-07-09.md</c> R5).</para>
/// </summary>
public static class ServerRankingScorer
{
    /// <summary>Extra penalty subtracted when the server's ASN is flagged HighRisk.</summary>
    public const int HighRiskAsnPenalty = 50;

    /// <summary>Base score for a verdict (higher = prefer). Blocked/unreachable score near zero.</summary>
    public static int BaseScore(ServerHealthVerdict verdict) => verdict switch
    {
        ServerHealthVerdict.Healthy => 100,
        ServerHealthVerdict.OnlyControlWorks => 60,        // tunnel up, bypass unproven — usable but weak
        ServerHealthVerdict.UdpOrAppProfileFailed => 55,   // web ok, games/voice broken
        ServerHealthVerdict.ProxyStartedButHttpFailed => 40,
        ServerHealthVerdict.TcpOpenProtocolUntested => 30, // unproven, not condemned
        ServerHealthVerdict.Unknown => 20,
        ServerHealthVerdict.ProtocolHandshakeBlockedLikely => 5,  // TCP alive but protocol blocked
        ServerHealthVerdict.HostUnreachable => 0,
        _ => 10,
    };

    /// <summary>
    /// Score one server: <see cref="BaseScore"/> minus <see cref="HighRiskAsnPenalty"/> when its
    /// ASN is in <paramref name="highRiskAsns"/>. Never negative.
    /// </summary>
    public static int Score(RankableServer s, ISet<string> highRiskAsns)
    {
        if (s is null) throw new ArgumentNullException(nameof(s));
        var score = BaseScore(s.Verdict);
        if (!string.IsNullOrWhiteSpace(s.Asn) && highRiskAsns != null && highRiskAsns.Contains(s.Asn))
            score -= HighRiskAsnPenalty;
        return Math.Max(0, score);
    }

    /// <summary>
    /// Rank servers best-first. Primary key is the score; the tie-break is ASN diversity — a
    /// greedy pick that, among equally-scored candidates, prefers an ASN least represented so
    /// far so the top of the list isn't all one (possibly fragile) provider. Stable by input
    /// order within a full tie. Ordinal ASN comparison.
    /// </summary>
    public static IReadOnlyList<RankedServer> Rank(
        IEnumerable<RankableServer> servers, ISet<string>? highRiskAsns = null)
    {
        if (servers is null) throw new ArgumentNullException(nameof(servers));
        var risk = highRiskAsns ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var scored = servers
            .Select((s, i) => (server: s, score: Score(s, risk), index: i))
            .ToList();

        var picked = new List<RankedServer>(scored.Count);
        var seenAsn = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var remaining = new List<(RankableServer server, int score, int index)>(scored);

        while (remaining.Count > 0)
        {
            // Highest score; tie -> fewest-already-seen ASN; tie -> original input order.
            (RankableServer server, int score, int index) best = remaining[0];
            int bestSeen = seenAsn.TryGetValue(best.server.Asn ?? "", out var c0) ? c0 : 0;
            foreach (var cand in remaining.Skip(1))
            {
                int candSeen = seenAsn.TryGetValue(cand.server.Asn ?? "", out var c) ? c : 0;
                bool better = cand.score > best.score
                    || (cand.score == best.score && candSeen < bestSeen)
                    || (cand.score == best.score && candSeen == bestSeen && cand.index < best.index);
                if (better) { best = cand; bestSeen = candSeen; }
            }

            picked.Add(new RankedServer(best.server.Id, best.server.Verdict, best.server.Asn, best.score));
            var key = best.server.Asn ?? "";
            seenAsn[key] = (seenAsn.TryGetValue(key, out var cc) ? cc : 0) + 1;
            remaining.Remove(best);
        }

        return picked;
    }
}
