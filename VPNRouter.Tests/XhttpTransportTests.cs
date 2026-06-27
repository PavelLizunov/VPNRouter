using System;
using System.Collections.Generic;
using System.Linq;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// XHTTP transport (managed level) for the sing-box-lx fork. XHTTP tunnels VLESS over
/// plain HTTP/2, composes with Reality, and is INCOMPATIBLE with XTLS-Vision (so no flow).
/// The emitted transport shape (host top-level, mode, x_padding_bytes, no_grpc_header) was
/// verified vs the real `sing-box-lx check`. Needs a sing-box-lx (with_xhttp) client.
/// <para>Forces <see cref="SingBoxFeatures.OverrideXhttp"/> = true so the type=xhttp
/// intake gate (default-closed on official builds) lets these fork tests run; shares the
/// serial collection with the other fork tests.</para>
/// </summary>
[Collection("SingBoxFeaturesSerial")]
public sealed class XhttpTransportTests : IDisposable
{
    public XhttpTransportTests() => SingBoxFeatures.OverrideXhttp = true;
    public void Dispose() => SingBoxFeatures.ResetForTests();

    [Fact]
    public void Parse_VlessXhttpUri_SetsTransport()
    {
        var e = VlessUriParser.Parse(
            "vless://11111111-1111-1111-1111-111111111111@example.com:443?security=reality" +
            "&pbk=KEY&sid=01ab&type=xhttp&path=%2Fp&host=cdn.example.com&mode=packet-up" +
            "&x_padding_bytes=100-1000&sni=example.com#X");
        Assert.Equal("xhttp", e.Transport.Type);
        Assert.Equal("/p", e.Transport.Path);
        Assert.Equal("cdn.example.com", e.Transport.Host);     // top-level, not a header
        Assert.Empty(e.Transport.Headers);                     // host did NOT go into headers
        Assert.Equal("packet-up", e.Transport.Mode);
        Assert.Equal("100-1000", e.Transport.XPaddingBytes);
    }

    [Fact]
    public void Generate_VlessXhttpServer_EmitsXhttpTransport_NoFlow()
    {
        var cfg = ConfigGenerator.Generate(
            new Profile { Name = "t", DnsMode = "vpn_only" }, Array.Empty<string>(), XhttpSettings());
        var proxy = cfg.Outbounds.First(o => o.Type == "vless" && o.Tag == "proxy");
        Assert.NotNull(proxy.Transport);
        Assert.Equal("xhttp", proxy.Transport!.Type);
        Assert.Equal("auto", proxy.Transport.Mode);            // empty -> defaulted to auto
        Assert.Equal("cdn.example.com", proxy.Transport.Host);
        Assert.Null(proxy.Flow);                               // XTLS-Vision incompatible
    }

    private static AppSettings XhttpSettings() => new()
    {
        App = new AppConfig { LogLevel = "info", RoutingMode = "full" },
        Dns = new DnsSettings { VpnDns = "https://1.1.1.1/dns-query" },
        SingBox = new SingBoxSettings(),
        Tun = new TunSettings(),
        Vless = new VlessConfig
        {
            ActiveServer = "x",
            Servers = new List<VlessServerEntry>
            {
                new()
                {
                    Name = "x", Protocol = "vless", Server = "example.com", Port = 443,
                    Uuid = "11111111-1111-1111-1111-111111111111",
                    Security = "reality",
                    Reality = new VlessRealityConfig { PublicKey = "KEY", ShortId = "01ab" },
                    Flow = "xtls-rprx-vision", // present but must be DROPPED for xhttp
                    Transport = new VlessTransportConfig
                    {
                        Type = "xhttp", Path = "/p", Host = "cdn.example.com",
                        XPaddingBytes = "100-1000",
                    },
                },
            },
        },
    };
}
