using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Core.Services.EmergencyChannel;

namespace VPNRouter.Tests;
// ═══════════════════════════════════════════════════════════════════════════════
// VlessUriParser
// ═══════════════════════════════════════════════════════════════════════════════

public class VlessUriParserTests
{
    // Q14 (2026-05-17): swapped from the v2.32.3 PlaceholderGuard sample
    // (pbk=DnT9hI...nckU + sid=78ca7952) to a synthesized real-looking
    // Reality fingerprint so VlessUriParser.Parse() doesn't reject the
    // fixture as placeholder bait. The synthesized pbk is base64url-shaped
    // 43 chars (matches what real X25519 pubkeys look like) and is NOT in
    // PlaceholderDefense.KnownPubkeys / KnownShortIds.
    private const string RealityUri =
        "vless://2d54442d-158f-49e2-b225-67ba1a5b77f4@194.87.222.111:443" +
        "?security=reality&sni=yahoo.com&fp=firefox" +
        "&pbk=vJgL2dRZSp_DOaXEm9wYwK0pH-c5fJqr1L3y7zT8xK4&sid=deadbeef" +
        "&spx=/&type=tcp&flow=xtls-rprx-vision&encryption=none#bratik";

    [Fact]
    public void Parse_RealityUri_ExtractsAllFields()
    {
        var entry = VlessUriParser.Parse(RealityUri);

        Assert.Equal("2d54442d-158f-49e2-b225-67ba1a5b77f4", entry.Uuid);
        Assert.Equal("194.87.222.111", entry.Server);
        Assert.Equal(443, entry.Port);
        Assert.Equal("xtls-rprx-vision", entry.Flow);
        Assert.Equal("reality", entry.Security);
        Assert.Equal("bratik", entry.Name);
    }

    [Fact]
    public void Parse_RealityUri_ExtractsRealityConfig()
    {
        var entry = VlessUriParser.Parse(RealityUri);

        Assert.Equal("yahoo.com", entry.Reality.ServerName);
        Assert.Equal("firefox", entry.Reality.Fingerprint);
        Assert.Equal("vJgL2dRZSp_DOaXEm9wYwK0pH-c5fJqr1L3y7zT8xK4", entry.Reality.PublicKey);
        Assert.Equal("deadbeef", entry.Reality.ShortId);
    }

    [Fact]
    public void Parse_RealityUri_ExtractsTransport()
    {
        var entry = VlessUriParser.Parse(RealityUri);

        Assert.Equal("tcp", entry.Transport.Type);
        Assert.Equal("/", entry.Transport.Path);
    }

    [Fact]
    public void Parse_NonDefaultPort()
    {
        var uri = "vless://uuid@server.com:8443?security=tls&type=tcp#test";
        var entry = VlessUriParser.Parse(uri);

        Assert.Equal("server.com", entry.Server);
        Assert.Equal(8443, entry.Port);
    }

    [Fact]
    public void Parse_DefaultPort_Is443()
    {
        // Port not specified — should default to 443
        var uri = "vless://uuid@server.com?security=tls&type=tcp#test";
        var entry = VlessUriParser.Parse(uri);

        Assert.Equal(443, entry.Port);
    }

    [Fact]
    public void Parse_TlsSecurity_SetsTlsConfig()
    {
        var uri = "vless://uuid@server.com:443?security=tls&sni=example.com&type=tcp#test";
        var entry = VlessUriParser.Parse(uri);

        Assert.Equal("tls", entry.Security);
        Assert.True(entry.Tls.Enabled);
        Assert.Equal("example.com", entry.Tls.ServerName);
    }

    [Fact]
    public void Parse_FragmentName_UrlDecoded()
    {
        var uri = "vless://uuid@server.com:443?security=tls&type=tcp#bratik-nout";
        var entry = VlessUriParser.Parse(uri);

        Assert.Equal("bratik-nout", entry.Name);
    }

    [Fact]
    public void Parse_NoFragment_EmptyName()
    {
        var uri = "vless://uuid@server.com:443?security=tls&type=tcp";
        var entry = VlessUriParser.Parse(uri);

        Assert.Equal("", entry.Name);
    }

    [Fact]
    public void Parse_InvalidScheme_Throws()
    {
        Assert.Throws<FormatException>(() =>
            VlessUriParser.Parse("https://server.com"));
    }

    [Fact]
    public void Parse_MissingUuid_Throws()
    {
        Assert.Throws<FormatException>(() =>
            VlessUriParser.Parse("vless://server.com:443?security=tls"));
    }

    [Fact]
    public void ParseMultiple_MultipleLines()
    {
        var text = @"
vless://uuid1@server1.com:443?security=reality&sni=yahoo.com&fp=firefox&pbk=key1&sid=aa&type=tcp&flow=xtls-rprx-vision#main
vless://uuid2@server2.com:443?security=reality&sni=yahoo.com&fp=chrome&pbk=key2&sid=bb&type=tcp&flow=xtls-rprx-vision#backup
";
        var entries = VlessUriParser.ParseMultiple(text);

        Assert.Equal(2, entries.Count);
        Assert.Equal("server1.com", entries[0].Server);
        Assert.Equal("uuid1", entries[0].Uuid);
        Assert.Equal("main", entries[0].Name);
        Assert.Equal("server2.com", entries[1].Server);
        Assert.Equal("uuid2", entries[1].Uuid);
        Assert.Equal("backup", entries[1].Name);
    }

    [Fact]
    public void ParseMultiple_SkipsEmptyAndNonVlessLines()
    {
        var text = @"
some random text
vless://uuid@server.com:443?security=tls&type=tcp#test

another line
";
        var entries = VlessUriParser.ParseMultiple(text);

        Assert.Single(entries);
        Assert.Equal("server.com", entries[0].Server);
    }

    [Fact]
    public void TryParse_InvalidUri_ReturnsNull()
    {
        var result = VlessUriParser.TryParse("not a uri");
        Assert.Null(result);
    }

    [Fact]
    public void TryParse_ValidUri_ReturnsEntry()
    {
        var result = VlessUriParser.TryParse(RealityUri);
        Assert.NotNull(result);
        Assert.Equal("194.87.222.111", result.Server);
    }

    [Fact]
    public void Parse_WebSocketTransport()
    {
        var uri = "vless://uuid@server.com:443?security=tls&sni=example.com&type=ws&path=%2Fws&host=cdn.example.com#ws-server";
        var entry = VlessUriParser.Parse(uri);

        Assert.Equal("ws", entry.Transport.Type);
        Assert.Equal("/ws", entry.Transport.Path);
        Assert.NotNull(entry.Transport.Headers);
        Assert.Equal("cdn.example.com", entry.Transport.Headers["Host"]);
    }
}
