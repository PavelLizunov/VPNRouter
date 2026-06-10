using System;
using System.Text;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

/// <summary>
/// v2.41.x — DNS-tunnel (slipstream) share-link parsing. Link form is
/// <c>dns-tunnel://&lt;base64url-JSON&gt;[#name]</c> where JSON carries
/// {domain, resolvers[], fingerprint, uuid}. The VLESS uuid is reused; the
/// outbound is later generated against 127.0.0.1, so Server holds the domain
/// only as a dedup/display identity. See
/// plans/dns-tunnel-slipstream-integration-2026-06-10.md.
/// </summary>
public class ServerUriParserDnsTunnelTests
{
    private const string SampleUuid = "11111111-1111-1111-1111-111111111111";

    private static string B64Url(string json)
    {
        var b = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
        return b.TrimEnd('=').Replace('+', '-').Replace('/', '_'); // url-safe, unpadded
    }

    private static string Link(string json, string? frag = null)
        => "dns-tunnel://" + B64Url(json) + (frag == null ? "" : "#" + frag);

    private const string GoodJson =
        "{\"domain\":\"tunnel.example.org\"," +
        "\"resolvers\":[\"195.208.4.1:53\",\"195.208.5.1:53\"]," +
        "\"fingerprint\":\"deadbeef\"," +
        "\"uuid\":\"" + SampleUuid + "\"}";

    [Fact]
    public void Parse_ValidLink_PopulatesAllFields()
    {
        var e = ServerUriParser.Parse(Link(GoodJson, "Emergency DNS"));

        Assert.Equal("dns-tunnel", e.Protocol);
        Assert.Equal("Emergency DNS", e.Name);
        Assert.Equal("tunnel.example.org", e.DnsDomain);
        Assert.Equal("tunnel.example.org", e.Server); // identity mirror
        Assert.Equal(SampleUuid, e.Uuid);
        Assert.Equal("deadbeef", e.DnsLeafFingerprint);
        Assert.Equal(new[] { "195.208.4.1:53", "195.208.5.1:53" }, e.DnsResolvers);
    }

    [Fact]
    public void Parse_NoFragment_NameDefaultsToDomain()
    {
        var e = ServerUriParser.Parse(Link(GoodJson));
        Assert.Equal("tunnel.example.org", e.Name);
    }

    [Fact]
    public void Parse_MissingDomain_Throws()
    {
        var json = "{\"resolvers\":[\"195.208.4.1:53\"],\"uuid\":\"" + SampleUuid + "\"}";
        var ex = Assert.Throws<FormatException>(() => ServerUriParser.Parse(Link(json)));
        Assert.Contains("domain", ex.Message);
    }

    [Fact]
    public void Parse_MissingResolvers_Throws()
    {
        var json = "{\"domain\":\"t.org\",\"uuid\":\"" + SampleUuid + "\"}";
        var ex = Assert.Throws<FormatException>(() => ServerUriParser.Parse(Link(json)));
        Assert.Contains("resolvers", ex.Message);
    }

    [Fact]
    public void Parse_EmptyResolversArray_Throws()
    {
        var json = "{\"domain\":\"t.org\",\"resolvers\":[],\"uuid\":\"" + SampleUuid + "\"}";
        Assert.Throws<FormatException>(() => ServerUriParser.Parse(Link(json)));
    }

    [Fact]
    public void Parse_MissingUuid_Throws()
    {
        var json = "{\"domain\":\"t.org\",\"resolvers\":[\"195.208.4.1:53\"]}";
        var ex = Assert.Throws<FormatException>(() => ServerUriParser.Parse(Link(json)));
        Assert.Contains("uuid", ex.Message);
    }

    [Fact]
    public void Parse_FingerprintOptional_DefaultsEmpty()
    {
        var json = "{\"domain\":\"t.org\",\"resolvers\":[\"195.208.4.1:53\"],\"uuid\":\"" + SampleUuid + "\"}";
        var e = ServerUriParser.Parse(Link(json));
        Assert.Equal(string.Empty, e.DnsLeafFingerprint);
    }

    [Fact]
    public void Parse_InvalidBase64_Throws()
    {
        var ex = Assert.Throws<FormatException>(() => ServerUriParser.Parse("dns-tunnel://!!!not-base64!!!"));
        Assert.Contains("base64url", ex.Message);
    }

    [Fact]
    public void Parse_ValidBase64ButNotJson_Throws()
    {
        var notJson = B64Url("this is not json at all");
        Assert.Throws<FormatException>(() => ServerUriParser.Parse("dns-tunnel://" + notJson));
    }

    [Fact]
    public void Parse_EmptyPayload_Throws()
    {
        Assert.Throws<FormatException>(() => ServerUriParser.Parse("dns-tunnel://"));
    }

    [Fact]
    public void IsSupportedScheme_DnsTunnel_TrueWhenRuntimeAvailable()
    {
        // On the Windows dev host / Linux CI the slipstream runtime is available.
        Assert.True(ServerUriParser.SlipstreamRuntimeAvailable);
        Assert.True(ServerUriParser.IsSupportedScheme(Link(GoodJson)));
    }

    [Fact]
    public void ParseMultiple_IncludesDnsTunnelLine()
    {
        var blob = "vless://" + SampleUuid + "@1.2.3.4:443?security=none\n" + Link(GoodJson);
        var list = ServerUriParser.ParseMultiple(blob);
        Assert.Contains(list, s => s.Protocol == "dns-tunnel" && s.DnsDomain == "tunnel.example.org");
    }

    [Fact]
    public void Parse_PlatformGate_RefusesWhenRuntimeUnavailable()
    {
        // Simulate macOS / Android where the slipstream-client sidecar can't run.
        var saved = ServerUriParser.SlipstreamRuntimeAvailable;
        try
        {
            ServerUriParser.SlipstreamRuntimeAvailable = false;
            var ex = Assert.Throws<FormatException>(() => ServerUriParser.Parse(Link(GoodJson)));
            Assert.Contains("Windows and Linux", ex.Message);
            Assert.False(ServerUriParser.IsSupportedScheme(Link(GoodJson)));
        }
        finally
        {
            ServerUriParser.SlipstreamRuntimeAvailable = saved;
        }
    }
}
