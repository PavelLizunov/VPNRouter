using System.Collections.Generic;
using System.Linq;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// Pins <see cref="ServerRankingScorer"/>: verdict-based scoring, high-risk-ASN penalty,
/// and the ASN-diversity tie-break. Pure; does not change any live selection.
/// </summary>
public class ServerRankingScorerTests
{
    private static readonly ISet<string> NoRisk = new HashSet<string>();

    [Fact]
    public void Healthy_OutranksBlocked()
    {
        var ranked = ServerRankingScorer.Rank(new[]
        {
            new RankableServer("blocked", ServerHealthVerdict.ProtocolHandshakeBlockedLikely, "AS1"),
            new RankableServer("healthy", ServerHealthVerdict.Healthy, "AS2"),
        });
        Assert.Equal("healthy", ranked[0].Id);
        Assert.Equal("blocked", ranked[1].Id);
    }

    [Fact]
    public void VerdictScoreOrder_IsMonotone()
    {
        Assert.True(ServerRankingScorer.BaseScore(ServerHealthVerdict.Healthy)
                  > ServerRankingScorer.BaseScore(ServerHealthVerdict.OnlyControlWorks));
        Assert.True(ServerRankingScorer.BaseScore(ServerHealthVerdict.OnlyControlWorks)
                  > ServerRankingScorer.BaseScore(ServerHealthVerdict.TcpOpenProtocolUntested));
        Assert.True(ServerRankingScorer.BaseScore(ServerHealthVerdict.TcpOpenProtocolUntested)
                  > ServerRankingScorer.BaseScore(ServerHealthVerdict.ProtocolHandshakeBlockedLikely));
        Assert.True(ServerRankingScorer.BaseScore(ServerHealthVerdict.ProtocolHandshakeBlockedLikely)
                  >= ServerRankingScorer.BaseScore(ServerHealthVerdict.HostUnreachable));
    }

    [Fact]
    public void HighRiskAsn_Penalized_DropsBelowHealthyOnGoodAsn()
    {
        var risk = new HashSet<string> { "AS-BAD" };
        var ranked = ServerRankingScorer.Rank(new[]
        {
            new RankableServer("onBad",  ServerHealthVerdict.Healthy, "AS-BAD"),
            new RankableServer("onGood", ServerHealthVerdict.Healthy, "AS-GOOD"),
        }, risk);
        // Same verdict, but the high-risk ASN is penalized -> onGood first.
        Assert.Equal("onGood", ranked[0].Id);
        Assert.True(ranked.Single(r => r.Id == "onGood").Score > ranked.Single(r => r.Id == "onBad").Score);
    }

    [Fact]
    public void Score_NeverNegative()
    {
        var risk = new HashSet<string> { "AS-BAD" };
        var s = ServerRankingScorer.Score(
            new RankableServer("x", ServerHealthVerdict.HostUnreachable, "AS-BAD"), risk);
        Assert.True(s >= 0);
    }

    [Fact]
    public void Diversity_TieBreak_InterleavesAsns()
    {
        // Three equally-healthy servers: two on AS-A, one on AS-B. The diversity tie-break
        // must not cluster both AS-A at the top; AS-B should surface at position 2.
        var ranked = ServerRankingScorer.Rank(new[]
        {
            new RankableServer("a1", ServerHealthVerdict.Healthy, "AS-A"),
            new RankableServer("a2", ServerHealthVerdict.Healthy, "AS-A"),
            new RankableServer("b1", ServerHealthVerdict.Healthy, "AS-B"),
        });
        Assert.Equal(new[] { "AS-A", "AS-B", "AS-A" }, ranked.Select(r => r.Asn).ToArray());
    }

    [Fact]
    public void Rank_EmptyInput_IsEmpty()
        => Assert.Empty(ServerRankingScorer.Rank(System.Array.Empty<RankableServer>()));

    [Fact]
    public void Rank_StableWithinFullTie()
    {
        // Same verdict + same ASN => original input order preserved.
        var ranked = ServerRankingScorer.Rank(new[]
        {
            new RankableServer("first",  ServerHealthVerdict.Healthy, "AS-A"),
            new RankableServer("second", ServerHealthVerdict.Healthy, "AS-A"),
        });
        Assert.Equal(new[] { "first", "second" }, ranked.Select(r => r.Id).ToArray());
    }
}
