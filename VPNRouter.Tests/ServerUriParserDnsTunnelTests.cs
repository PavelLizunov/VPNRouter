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

    // Leaf PEM as it appears INSIDE the JSON payload (newlines escaped \n per
    // JSON spec) and the same after JSON decoding (real newlines). Fake content
    // — the parser only checks for the BEGIN CERTIFICATE marker, not X.509.
    private const string PemInJson =
        "-----BEGIN CERTIFICATE-----\\nMIIBfakeLineOne\\nMIIBfakeLineTwo\\n-----END CERTIFICATE-----\\n";
    // Parser .Trim()s surrounding whitespace, so the trailing newline is dropped.
    private static readonly string PemDecoded =
        "-----BEGIN CERTIFICATE-----\nMIIBfakeLineOne\nMIIBfakeLineTwo\n-----END CERTIFICATE-----";

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
        "\"cert\":\"" + PemInJson + "\"," +
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
        Assert.Equal(PemDecoded, e.DnsLeafCertPem); // full PEM, newlines decoded
        Assert.Equal(new[] { "195.208.4.1:53", "195.208.5.1:53" }, e.DnsResolvers);
        Assert.Empty(e.DnsAuthoritative); // none in the baseline link
    }

    [Fact]
    public void Parse_AuthoritativeString_ShortKey_Populates()
    {
        // Server publishes a single authoritative endpoint (short "auth" key) to
        // let the client bypass the rate-limiting recursive resolver.
        var json =
            "{\"d\":\"t.ninitux.top\"," +
            "\"r\":[\"195.208.4.1:53\"]," +
            "\"auth\":\"213.155.15.93:53\"," +
            "\"cert\":\"" + PemInJson + "\"," +
            "\"uuid\":\"" + SampleUuid + "\"}";
        var e = ServerUriParser.Parse(Link(json));
        Assert.Equal(new[] { "213.155.15.93:53" }, e.DnsAuthoritative);
        Assert.Equal(new[] { "195.208.4.1:53" }, e.DnsResolvers); // recursive preserved (multipath)
    }

    [Fact]
    public void Parse_AuthoritativeArray_LongKey_Populates()
    {
        var json =
            "{\"domain\":\"t.org\"," +
            "\"resolvers\":[\"195.208.4.1:53\"]," +
            "\"authoritative\":[\"213.155.15.93:53\",\"213.155.15.93:5353\"]," +
            "\"cert\":\"" + PemInJson + "\"," +
            "\"uuid\":\"" + SampleUuid + "\"}";
        var e = ServerUriParser.Parse(Link(json));
        Assert.Equal(new[] { "213.155.15.93:53", "213.155.15.93:5353" }, e.DnsAuthoritative);
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
        var json = "{\"domain\":\"t.org\",\"resolvers\":[\"195.208.4.1:53\"]," +
                   "\"cert\":\"" + PemInJson + "\",\"uuid\":\"" + SampleUuid + "\"}";
        var e = ServerUriParser.Parse(Link(json));
        Assert.Equal(string.Empty, e.DnsLeafFingerprint);
        Assert.Equal(PemDecoded, e.DnsLeafCertPem); // cert still required + present
    }

    [Fact]
    public void Parse_MissingCert_Throws()
    {
        // Valid domain/resolvers/uuid but no cert — the PEM is load-bearing.
        var json = "{\"domain\":\"t.org\",\"resolvers\":[\"195.208.4.1:53\"],\"uuid\":\"" + SampleUuid + "\"}";
        var ex = Assert.Throws<FormatException>(() => ServerUriParser.Parse(Link(json)));
        Assert.Contains("cert", ex.Message);
    }

    [Fact]
    public void Parse_CertNotPem_Throws()
    {
        var json = "{\"domain\":\"t.org\",\"resolvers\":[\"195.208.4.1:53\"]," +
                   "\"cert\":\"hello-not-a-pem\",\"uuid\":\"" + SampleUuid + "\"}";
        var ex = Assert.Throws<FormatException>(() => ServerUriParser.Parse(Link(json)));
        Assert.Contains("PEM", ex.Message);
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

    // ── Production server schema: SHORT keys (d/r/fp), the authoritative form ──

    private const string ShortKeyJson =
        "{\"cert\":\"" + PemInJson + "\"," +
        "\"d\":\"tunnel.example.org\"," +
        "\"fp\":\"DE:AD:BE:EF\"," +
        "\"r\":[\"195.208.4.1:53\",\"195.208.5.1:53\"]," +
        "\"uuid\":\"" + SampleUuid + "\",\"v\":2}";

    [Fact]
    public void Parse_ShortKeys_ProductionSchema_PopulatesAllFields()
    {
        // The real slipstream server emits {cert,d,fp,r,uuid,v}. The colon-
        // separated fingerprint is carried verbatim (SlipstreamManager normalises
        // it before the sha256 cross-check). "v" is ignored.
        var e = ServerUriParser.Parse(Link(ShortKeyJson, "main-brat"));

        Assert.Equal("dns-tunnel", e.Protocol);
        Assert.Equal("main-brat", e.Name);
        Assert.Equal("tunnel.example.org", e.DnsDomain);
        Assert.Equal("tunnel.example.org", e.Server);
        Assert.Equal(SampleUuid, e.Uuid);
        Assert.Equal("DE:AD:BE:EF", e.DnsLeafFingerprint);
        Assert.Equal(PemDecoded, e.DnsLeafCertPem);
        Assert.Equal(new[] { "195.208.4.1:53", "195.208.5.1:53" }, e.DnsResolvers);
    }

    // The exact production link (server "main-brat", domain t.ninitux.top). Pins
    // that a real, deployed-server link parses — the field-name mismatch that
    // would have rejected it (long-key parser vs short-key emitter) is the bug
    // this regression locks. The leaf is a *public* server cert (safe to embed).
    private const string RealProductionLink =
        "dns-tunnel://eyJjZXJ0IjoiLS0tLS1CRUdJTiBDRVJUSUZJQ0FURS0tLS0tXG5NSUlCS3pDQjBhQURBZ0VDQWhFQTByelEwNjBGOElWMnBOZUZDRy9LdWpBS0JnZ3Foa2pPUFFRREFqQVZNUk13XG5FUVlEVlFRRERBcHpiR2x3YzNSeVpXRnRNQ0lZRHpJd01qWXdOakE0TWpBMU5qQTRXaGdQTXpBeU5URXdNRGt5XG5NRFUyTURoYU1CVXhFekFSQmdOVkJBTU1Dbk5zYVhCemRISmxZVzB3V1RBVEJnY3Foa2pPUFFJQkJnZ3Foa2pPXG5QUU1CQndOQ0FBVGw1T09EcGVzc2dyY2JtMU90T3dlRmo0bHRsMFBkMTI0Q2l5cjVCRmxqTDJESGZ4R1ZMcHM3XG5ZazBaWGNhTTRpTk8wQWFMdEpUdXpvNXlHci83bUQ0Yk1Bb0dDQ3FHU000OUJBTUNBMGtBTUVZQ0lRRE9FM0V2XG5GUW5ueDZvcG5yZ2gvODB3ZDNlaE0vOXBtRFV2VmV2YVpGaGxyUUloQU5NQUx1eGZpZnBaUHltei9EM0tVYXYxXG5hMEVTd3pXOVVYb1RCcWFsYnZxclxuLS0tLS1FTkQgQ0VSVElGSUNBVEUtLS0tLSIsImQiOiJ0Lm5pbml0dXgudG9wIiwiZnAiOiI0NzoxRTo4Nzo4RjozRTo0ODpDODoxQzo1RjpCRjozMDoyRTpCODpBODozQTowNTo3MjowRDpCOTo3NzpBMjoxMTo4MTowOTpFNjpFNTpFRjo5MjpDNDo2Njo3Qjo5MiIsInIiOlsiMTk1LjIwOC40LjE6NTMiLCIxOTUuMjA4LjUuMTo1MyJdLCJ1dWlkIjoiNTU1MDA1MWMtMmIxMC00YzExLThkNzMtYjkxODExOGY4NmVmIiwidiI6Mn0#main-brat";

    [Fact]
    public void Parse_RealProductionLink_Parses()
    {
        var e = ServerUriParser.Parse(RealProductionLink);

        Assert.Equal("dns-tunnel", e.Protocol);
        Assert.Equal("main-brat", e.Name);
        Assert.Equal("t.ninitux.top", e.DnsDomain);
        Assert.Equal("5550051c-2b10-4c11-8d73-b918118f86ef", e.Uuid);
        Assert.Equal(new[] { "195.208.4.1:53", "195.208.5.1:53" }, e.DnsResolvers);
        Assert.StartsWith("-----BEGIN CERTIFICATE-----", e.DnsLeafCertPem);
        Assert.Contains("-----END CERTIFICATE-----", e.DnsLeafCertPem);
        Assert.Equal(
            "47:1E:87:8F:3E:48:C8:1C:5F:BF:30:2E:B8:A8:3A:05:72:0D:B9:77:A2:11:81:09:E6:E5:EF:92:C4:66:7B:92",
            e.DnsLeafFingerprint);
    }
}
