#nullable enable
using System.Collections.Generic;
using System.IO;
using System.Linq;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// Local-only acceptance reproduction for B0: runs the real classifier over the
/// actual diag-bundle sing-box logs and asserts the per-category counts.
///
/// <para>These are the <em>classifier's</em> corrected numbers, a refinement of the
/// grep-based figures in the review (which counted EOF and "forcibly closed"
/// separately): the classifier folds every relay-open failure cause into
/// RelayOpenFail, so 214717 = 2178 relay-open fails (1952 EOF + 224 dial-timeout +
/// 2 reset) and 739 local closes; 205004 = 1588 (1587 EOF + 1 dial-timeout) + 216
/// local closes. The key invariants from the review hold: the EOF sub-count is
/// exactly 1952 / 1587, and the 739 / 216 local closes are NOT relay-open fails.</para>
///
/// <para>The raw logs hold real traffic destinations and are <strong>not</strong>
/// committed. The test reads them from <c>VPNROUTER_DIAG_FIXTURES</c> (a directory
/// with <c>&lt;bundle&gt;/singbox-tail.log</c>) and skips silently when absent — so it
/// never runs in CI, only on a machine that has the bundles. ProxyStreamError
/// additionally needs the node endpoint via <c>VPNROUTER_TEST_PROXY_EP</c>
/// (e.g. "1.2.3.4:443"); that assertion is skipped when unset. No user data lives
/// in this file.</para>
/// </summary>
public sealed class ConnectionHealthFixtureCountsTests
{
    private static string? FixtureDir => System.Environment.GetEnvironmentVariable("VPNROUTER_DIAG_FIXTURES");
    private static string? ProxyEp => System.Environment.GetEnvironmentVariable("VPNROUTER_TEST_PROXY_EP");

    private sealed record Counts(Dictionary<ConnHealthCategory, int> Cats, int EofFails);

    private static Counts? CountFixture(string bundle)
    {
        var dir = FixtureDir;
        if (string.IsNullOrWhiteSpace(dir))
            return null;
        var path = Path.Combine(dir, bundle, "singbox-tail.log");
        if (!File.Exists(path))
            return null;

        IReadOnlySet<string>? eps = string.IsNullOrWhiteSpace(ProxyEp) ? null : new HashSet<string> { ProxyEp! };
        var cats = new Dictionary<ConnHealthCategory, int>();
        int eofFails = 0;
        foreach (var line in File.ReadLines(path))
        {
            var ev = ConnectionHealthClassifier.Classify(line, eps);
            if (ev is null) continue;
            cats[ev.Category] = cats.GetValueOrDefault(ev.Category) + 1;
            if (ev.Category == ConnHealthCategory.RelayOpenFail && ev.FailKind == RelayFailKind.Eof)
                eofFails++;
        }
        return new Counts(cats, eofFails);
    }

    [Fact]
    public void Bundle214717_FullTunnel_Counts()
    {
        var c = CountFixture("diag-214717");
        if (c is null) return; // no local fixtures — skip
        Assert.Equal(2178, c.Cats.GetValueOrDefault(ConnHealthCategory.RelayOpenFail));
        Assert.Equal(1952, c.EofFails);
        Assert.Equal(739, c.Cats.GetValueOrDefault(ConnHealthCategory.LocalClose));
        if (!string.IsNullOrWhiteSpace(ProxyEp))
            Assert.Equal(6, c.Cats.GetValueOrDefault(ConnHealthCategory.ProxyStreamError));
    }

    [Fact]
    public void Bundle205004_Split_Counts()
    {
        // 205004 extracts to "diag-20260619" on the reference machine; accept either name.
        var c = CountFixture("diag-205004") ?? CountFixture("diag-20260619");
        if (c is null) return; // no local fixtures — skip
        Assert.Equal(1588, c.Cats.GetValueOrDefault(ConnHealthCategory.RelayOpenFail));
        Assert.Equal(1587, c.EofFails);
        Assert.Equal(216, c.Cats.GetValueOrDefault(ConnHealthCategory.LocalClose));
    }
}
