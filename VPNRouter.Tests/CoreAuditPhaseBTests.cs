using System;
using System.Diagnostics;
using System.Linq;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// Phase B (routing) core-functionality audit — property/regression pins for the
/// ProcessScanner wildcard-matching invariants (B3) and the B3-1 ReDoS fix.
///
/// <para>The Phase B adversarial sweep (2026-06-03) confirmed B1 (case-exactness),
/// B2 (include/exclude correctness) and B4 (survivor-guard) HOLD by tracing the
/// code; those are pinned by existing suites (ConfigGeneratorTests,
/// RoutingAppListEditorTests, LeakAuditFixTests). It found B3-1 HIGH: scan_patterns
/// (untrusted — GitHub/Local profile JSON + the Apps UI) compiled to a regex with
/// no match timeout, so a catastrophic-backtracking pattern (e.g. "a*a*...b.exe")
/// could pin a thread for seconds on the ETW process-launch / rescan hot paths and
/// wedge the routing engine (intended apps then leak by starvation). Fix added a
/// 250ms match timeout + fail-safe catch. These tests lock that in plus the
/// anchored/escaped regex-construction invariants.</para>
/// </summary>
public sealed class CoreAuditPhaseBTests
{
    // ── B3-1: catastrophic pattern must fail fast (matchTimeout), not hang ──
    [Fact]
    public void MatchesPattern_CatastrophicWildcard_FailsFastInsteadOfHanging()
    {
        // "a*a*a*...b.exe" → "^a.*a.*...b\.exe$"; against a long all-'a' input
        // with no 'b' this backtracks exponentially (measured ~8.5s pre-fix).
        var pattern = string.Concat(Enumerable.Repeat("a*", 20)) + "b.exe";
        var input = new string('a', 60) + ".exe"; // never matches (no 'b')

        var sw = Stopwatch.StartNew();
        var matched = ProcessScanner.MatchesPattern(input, pattern);
        sw.Stop();

        Assert.False(matched); // fail-safe: a pattern that can't decide → no match
        Assert.True(sw.ElapsedMilliseconds < 2000,
            $"match took {sw.ElapsedMilliseconds}ms — the 250ms matchTimeout is not " +
            "enforced (B3-1 ReDoS regression: an untrusted scan_pattern can wedge the " +
            "routing engine).");
    }

    // ── B3 regex construction: anchored + metachars escaped ──
    [Theory]
    [InlineData("Discord.exe", "Discord.exe", true)]    // exact match
    [InlineData("evilDiscord.exe", "Discord.exe", false)] // anchored start — no substring over-match
    [InlineData("Discord.exe.bak", "Discord.exe", false)] // anchored end
    [InlineData("Discordxexe", "Discord.exe", false)]     // '.' is escaped (literal), not "any char"
    [InlineData("a.b.exe", "a.b.exe", true)]              // literal dots match literal dots
    [InlineData("axbxexe", "a.b.exe", false)]             // escaped dots do NOT act as wildcards
    [InlineData("Discord.exe", "Disc*", true)]            // '*' wildcard works
    [InlineData("Telegram.exe", "Disc*", false)]          // '*' wildcard is still anchored
    [InlineData("Discord.exe", "Discord.?xe", true)]      // '?' = single char
    [InlineData("Discord.xe", "Discord.?xe", false)]      // '?' requires exactly one char
    public void MatchesPattern_AnchoredAndEscaped(string processName, string pattern, bool expected)
    {
        Assert.Equal(expected, ProcessScanner.MatchesPattern(processName, pattern));
    }

    // ── B3-1 fail-safe also applies to a plain (non-catastrophic) miss ──
    [Fact]
    public void MatchesPattern_NormalMiss_ReturnsFalseFast()
    {
        var sw = Stopwatch.StartNew();
        var matched = ProcessScanner.MatchesPattern("chrome.exe", "Discord*");
        sw.Stop();
        Assert.False(matched);
        Assert.True(sw.ElapsedMilliseconds < 500); // legitimate matches are sub-ms
    }
}
