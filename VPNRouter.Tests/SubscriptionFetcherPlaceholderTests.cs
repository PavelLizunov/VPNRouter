using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

// ═══════════════════════════════════════════════════════════════════════════════
// SubscriptionFetcher — placeholder filter (v2.32.3 Phase 2b)
// ═══════════════════════════════════════════════════════════════════════════════
//
// Pin: ParseBody silently drops entries whose Reality public_key / short_id /
// server IP matches a known placeholder fingerprint (see PlaceholderGuard /
// ConfigSanityCheck.KnownPlaceholderPubkeys). One bad sample URL scraped by a
// subscription provider must NOT poison the user's other working servers —
// drop the bait, keep the rest, log a warning that aggregates the count.
//
// These tests live alongside SubscriptionFetcherParserTests (UnitTest1.cs) and
// share the same internal-access pattern via InternalsVisibleTo.
//
// Why these are a separate file (vs. extending the existing parser tests):
// 1. The placeholder filter is a v2.32.3 surface — keeping it segregated makes
//    it easy to spot from the test class name and from `dotnet test --filter`.
// 2. Failures here point straight at the placeholder-guard wiring rather than
//    mixing with format-parse failures (base64, JSON wrapper, dedup) that the
//    pre-existing class already covers.

public class SubscriptionFetcherPlaceholderTests
{
    // The known-bait Reality public key (mirrors ConfigSanityCheck.KnownPlaceholderPubkeys).
    // Pre-v2.32.3 it leaked out of Android smoke-test code into share-links and
    // now propagates through subscriptions that re-scrape sample URLs.
    private const string PlaceholderPubkey =
        "DnT9hIvt5QEx07unHUeXbWxN4Qo1gnecN4p0s62nckU";

    // Clean URI templates — distinct uuid + server so dedup sees them as
    // separate entries. Format mirrors SubscriptionFetcherParserTests.
    private const string CleanVless1 =
        "vless://uuid1@server1.example:443?security=tls&type=tcp&flow=xtls-rprx-vision#clean1";
    private const string CleanVless2 =
        "vless://uuid2@server2.example:443?security=tls&type=tcp#clean2";
    private const string CleanVless3 =
        "vless://uuid3@server3.example:443?security=tls&type=tcp#clean3";

    // Placeholder URIs — three distinct uuid/server pairs, all carrying the
    // bait pubkey. We use Reality security so the pubkey actually gets
    // populated on the parsed VlessServerEntry.
    private static string PlaceholderVless(int i) =>
        $"vless://uuid-bad-{i}@bad{i}.example:443?security=reality&sni=yahoo.com&fp=firefox" +
        $"&pbk={PlaceholderPubkey}&sid=78ca7952&spx=/&type=tcp&flow=xtls-rprx-vision&encryption=none#bad{i}";

    private static string Base64(string s) =>
        Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(s));

    [Fact]
    public void ParseBody_OneCleanOnePlaceholder_DropsPlaceholder()
    {
        // Two URIs in body, one clean, one placeholder. Expected result: only
        // the clean entry survives. Aggregated warning is fired via the
        // logger (not asserted here — covered by the diagnostics overload
        // exercised in ParseBody_OutParam_ReportsDroppedCount below).
        var body = $"{CleanVless1}\n{PlaceholderVless(1)}\n";

        var result = SubscriptionFetcher.ParseBody(body);

        Assert.Single(result);
        Assert.Equal("server1.example", result[0].Server);
    }

    [Fact]
    public void ParseBody_AllPlaceholders_ReturnsEmptyList()
    {
        // All three lines are bait. ParseBody returns empty; RefreshEntryAsync's
        // existing "Refresh returned 0 servers, keeping cached" branch then
        // preserves the user's previously-cached list. Last good content
        // wins — we do NOT wipe the user's working server list because the
        // current refresh happened to be all-bad.
        var body =
            $"{PlaceholderVless(1)}\n" +
            $"{PlaceholderVless(2)}\n" +
            $"{PlaceholderVless(3)}\n";

        var result = SubscriptionFetcher.ParseBody(body);

        Assert.Empty(result);
    }

    [Fact]
    public void ParseBody_MixedSchemes_PlaceholderInVless_DropsOnlyThat()
    {
        // The placeholder filter is keyed on Reality fingerprint, so it only
        // affects the bait vless line. Clean hysteria2 / tuic entries are
        // independent of Reality-class placeholders and must pass through
        // untouched. This guards against an over-broad filter that would
        // mistakenly drop other-protocol entries.
        var body =
            $"{PlaceholderVless(1)}\n" +
            "hysteria2://pass@hy.example:9443/?sni=foo.example&insecure=0#hy-clean\n" +
            "tuic://u-uid:pw@tuic.example:443?sni=foo.example&congestion_control=cubic#tuic-clean\n";

        var result = SubscriptionFetcher.ParseBody(body);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, e => e.Server == "hy.example");
        Assert.Contains(result, e => e.Server == "tuic.example");
        Assert.DoesNotContain(result, e => e.Server == "bad1.example");
    }

    [Fact]
    public void ParseBody_CleanList_AllPass()
    {
        // Sanity: a fully clean subscription must round-trip every entry.
        // If a future refactor accidentally trips the placeholder branch on
        // entries that don't match the fingerprint list, this test catches
        // the false-positive regression — false-positive bans kill VPN for
        // the user (see PlaceholderGuard comment "Add new entries only after
        // a concrete user-report").
        var body =
            $"{CleanVless1}\n" +
            $"{CleanVless2}\n" +
            $"{CleanVless3}\n";

        var result = SubscriptionFetcher.ParseBody(body);

        Assert.Equal(3, result.Count);
        Assert.Equal("server1.example", result[0].Server);
        Assert.Equal("server2.example", result[1].Server);
        Assert.Equal("server3.example", result[2].Server);
    }

    [Fact]
    public void ParseBody_JsonWrapper_PlaceholderInBase64Content_DropsPlaceholder()
    {
        // ninitux.com format: {"config":"<base64>"}. The bait can live inside
        // any of the body formats — JSON-wrapped, raw base64, plain URIs. The
        // placeholder filter runs *after* decode, so it catches all three.
        // This test pins the JSON-wrapper path explicitly because that's the
        // format the canonical ninitux.com subscription uses, and a regression
        // that only filters at the outer body layer would silently leak bait
        // through that wrapper.
        var inner = Base64($"{CleanVless1}\n{PlaceholderVless(1)}\n");
        var body = $@"{{""config"":""{inner}""}}";

        var result = SubscriptionFetcher.ParseBody(body);

        Assert.Single(result);
        Assert.Equal("server1.example", result[0].Server);
    }

    [Fact]
    public void ParseBody_OutParam_ReportsDroppedCount()
    {
        // Internal diagnostics overload — pins that the out-param surfaces
        // the placeholder drop count so FetchWithDiagnosticsAsync /
        // RefreshEntryAsync can emit their dedicated warning. Three bait
        // entries in, two clean entries through → out=3, list=2.
        var body =
            $"{CleanVless1}\n" +
            $"{PlaceholderVless(1)}\n" +
            $"{PlaceholderVless(2)}\n" +
            $"{CleanVless2}\n" +
            $"{PlaceholderVless(3)}\n";

        var result = SubscriptionFetcher.ParseBody(body, out var droppedCount);

        Assert.Equal(2, result.Count);
        Assert.Equal(3, droppedCount);
    }
}
