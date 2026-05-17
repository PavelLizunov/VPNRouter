using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Core.Services.EmergencyChannel;

namespace VPNRouter.Tests;
// ═══════════════════════════════════════════════════════════════════════════════
// SubscriptionFetcher.ParseBody (v2.31.5+)
// ═══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Pin the three subscription-body formats SubscriptionFetcher accepts —
/// JSON wrapper, raw base64, plain URIs — plus the dedup + unsupported-
/// scheme filter behaviour. Pre-v2.31.5 these branches only had
/// integration coverage via real subscription URLs, which means a
/// regression on a corner case (provider returns JSON without 'config',
/// malformed JSON, duplicate URIs, comment lines) could ship invisibly.
///
/// <para>The parser is reached via <c>internal static ParseBody</c> —
/// extracted in v2.31.5 from the inline FetchAsync body so we can test
/// without an HTTP round-trip. <see cref="VPNRouter.Core.Services.SubscriptionFetcher.FetchAsync"/>
/// remains the only production caller.</para>
/// </summary>
public class SubscriptionFetcherParserTests
{
    // Distinct uuid + server pairs so dedup sees them as separate. The
    // VLESS URI parser doesn't enforce GUID format on the user-info
    // segment (see VlessUriParserTests.Parse_NonDefaultPort which uses
    // bare "uuid"), so simple stable strings are fine here.
    private const string Uri1 = "vless://uuid1@server1.example:443?security=tls&type=tcp&flow=xtls-rprx-vision#one";
    private const string Uri2 = "vless://uuid2@server2.example:443?security=tls&type=tcp#two";

    private static string Base64(string s) =>
        Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(s));

    [Fact]
    public void ParseBody_PlainVlessUris_ReturnsAllEntries()
    {
        var body = $"{Uri1}\n{Uri2}\n";

        var result = SubscriptionFetcher.ParseBody(body);

        Assert.Equal(2, result.Count);
        Assert.Equal("server1.example", result[0].Server);
        Assert.Equal("server2.example", result[1].Server);
    }

    [Fact]
    public void ParseBody_RawBase64_DecodesAndParses()
    {
        // v2rayNG / Streisand / Hiddify format: HTTP body is a base64
        // blob whose decoded bytes are the newline-separated URI list.
        var body = Base64($"{Uri1}\n{Uri2}");

        var result = SubscriptionFetcher.ParseBody(body);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void ParseBody_JsonWrapperWithConfig_DecodesBase64Inner()
    {
        // ninitux.com format: {"config":"<base64-encoded URI list>"}.
        // Two layers of decoding: JSON → string → base64 → URI list.
        var inner = Base64($"{Uri1}\n{Uri2}");
        var body = $@"{{""config"":""{inner}""}}";

        var result = SubscriptionFetcher.ParseBody(body);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void ParseBody_JsonWithoutConfigField_ReturnsEmpty()
    {
        // Provider returns valid JSON but without "config". We log a
        // warning and return empty rather than guessing at unknown
        // shapes. RefreshEntryAsync's "keep cached on 0" guard then
        // protects the user from a bad refresh wiping their list.
        var body = @"{""servers"":[]}";

        var result = SubscriptionFetcher.ParseBody(body);

        Assert.Empty(result);
    }

    [Fact]
    public void ParseBody_MalformedJson_FallsBackToTrimmedBodyThenEmpty()
    {
        // Body starts with "{" → JSON path is tried first. JsonDocument
        // throws → catch sets decoded=trimmed (= the malformed string)
        // → split-by-newline → no line passes IsSupportedScheme → 0
        // entries. Pin this graceful-fallback so a regression that
        // re-throws the JsonException doesn't propagate to FetchAsync's
        // outer catch and silently drop everything.
        var body = "{not-json";

        var result = SubscriptionFetcher.ParseBody(body);

        Assert.Empty(result);
    }

    [Fact]
    public void ParseBody_DuplicateUri_DeduplicatedByServerPortUuidFlow()
    {
        // Dedup key is "Server:Port:UUID:Flow". Same URI twice → one
        // entry. Different Flow on otherwise-identical entry → two
        // entries (TCP/UDP split pair). This test only exercises the
        // exact-duplicate branch; UDP-pair preservation is tested
        // implicitly via VlessServersResolverTests.
        var body = $"{Uri1}\n{Uri1}\n{Uri2}\n";

        var result = SubscriptionFetcher.ParseBody(body);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void ParseBody_UnsupportedSchemes_FilteredOut()
    {
        // ServerUriParser.IsSupportedScheme gates the line loop: only
        // vless / hysteria2 / hy2 / tuic / ss are accepted. Random
        // http://, comment lines, blank-ish junk all drop silently so
        // a partially-bad subscription doesn't fail the whole import.
        var body =
            $"{Uri1}\n" +
            "http://not-a-vpn.example/\n" +
            "# this is a comment line\n" +
            "random-garbage-no-scheme\n" +
            $"{Uri2}\n";

        var result = SubscriptionFetcher.ParseBody(body);

        Assert.Equal(2, result.Count); // only the two vless lines kept
    }

    [Fact]
    public void ParseBody_EmptyBody_ReturnsEmpty()
    {
        Assert.Empty(SubscriptionFetcher.ParseBody(""));
        Assert.Empty(SubscriptionFetcher.ParseBody("   \n\t  \n"));
    }
}
