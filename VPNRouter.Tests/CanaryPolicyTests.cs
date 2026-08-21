using System;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// Pins <see cref="CanaryPolicy"/>: URL redaction, TTL staleness, and reducing per-target
/// canary outcomes to the <see cref="ServerHealthPhases.BlockedTargetCanary"/> phase.
/// Pure, no network (probing is deferred R4).
/// </summary>
public class CanaryPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 9, 12, 0, 0, TimeSpan.Zero);

    // ── URL redaction (never log path/query) ────────────────────────────────

    [Theory]
    [InlineData("https://www.youtube.com/generate_204?foo=bar", "https://www.youtube.com")]
    [InlineData("https://www.gstatic.com/generate_204", "https://www.gstatic.com")]
    [InlineData("http://example.org/a/b/c#frag", "http://example.org")]
    [InlineData("https://user:password@www.youtube.com/generate_204?foo=bar", "https://www.youtube.com")]
    public void RedactUrl_StripsPathAndQuery(string url, string expected)
        => Assert.Equal(expected, CanaryPolicy.RedactUrl(url));

    [Fact]
    public void RedactUrl_Empty_IsNone() => Assert.Equal("(none)", CanaryPolicy.RedactUrl(""));

    [Fact]
    public void RedactUrl_Malformed_DoesNotThrow_AndDropsPath()
        => Assert.Equal("not a url", CanaryPolicy.RedactUrl("not a url/secret?token=abc"));

    [Fact]
    public void RedactUrl_MalformedWithCredentials_StripsUserInfo()
        => Assert.Equal("https://invalid_host:port_bad", CanaryPolicy.RedactUrl("https://user:secretpassword@invalid_host:port_bad/secret_path?token=123"));

    // ── Staleness ───────────────────────────────────────────────────────────

    [Fact]
    public void IsStale_BlockedTargetOlderThanTtl_IsStale()
    {
        var t = new CanaryTarget("https://x", CanaryTier.PopularBlocked, "video", Now.AddDays(-40));
        Assert.True(CanaryPolicy.IsStale(t, Now, TimeSpan.FromDays(30)));
    }

    [Fact]
    public void IsStale_BlockedTargetWithinTtl_IsFresh()
    {
        var t = new CanaryTarget("https://x", CanaryTier.LessPopularBlocked, "misc", Now.AddDays(-5));
        Assert.False(CanaryPolicy.IsStale(t, Now, TimeSpan.FromDays(30)));
    }

    [Fact]
    public void IsStale_ControlCanary_IsNeverStale()
    {
        var t = new CanaryTarget("https://www.gstatic.com/generate_204", CanaryTier.Control, "control", Now.AddYears(-5));
        Assert.False(CanaryPolicy.IsStale(t, Now, TimeSpan.FromDays(30)));
    }

    // ── Aggregate → BlockedTargetCanary phase ───────────────────────────────

    [Fact]
    public void Evaluate_ControlFailed_IsUnknown()
        => Assert.Equal(PhaseOutcome.Unknown,
            CanaryPolicy.Evaluate(controlPassed: false, new[] { (true, false) }).BlockedTargetCanary);

    [Fact]
    public void Evaluate_AnyFreshPassed_IsPass()
        => Assert.Equal(PhaseOutcome.Pass,
            CanaryPolicy.Evaluate(true, new[] { (false, false), (true, false) }).BlockedTargetCanary);

    [Fact]
    public void Evaluate_AllFreshFailed_IsFail_OnlyControlWorks()
    {
        // The audit's key case: control ok + every blocked target fails => not "connected ok".
        var agg = CanaryPolicy.Evaluate(true, new[] { (false, false), (false, false) });
        Assert.Equal(PhaseOutcome.Fail, agg.BlockedTargetCanary);

        // And through the classifier this becomes OnlyControlWorks, not Healthy.
        var phases = new ServerHealthPhases(
            TcpConnect: PhaseOutcome.Pass,
            ProxiedHttpControl: PhaseOutcome.Pass,
            BlockedTargetCanary: agg.BlockedTargetCanary);
        Assert.Equal(ServerHealthVerdict.OnlyControlWorks, ServerHealthClassifier.Classify(phases).Verdict);
    }

    [Fact]
    public void Evaluate_AllStale_IsUnknownAndFlagged()
    {
        var agg = CanaryPolicy.Evaluate(true, new[] { (false, true), (true, true) });
        Assert.Equal(PhaseOutcome.Unknown, agg.BlockedTargetCanary);
        Assert.True(agg.StaleOrAmbiguous);
    }

    [Fact]
    public void Evaluate_NoBlockedTargets_IsUnknown()
        => Assert.Equal(PhaseOutcome.Unknown,
            CanaryPolicy.Evaluate(true, Array.Empty<(bool, bool)>()).BlockedTargetCanary);
}
