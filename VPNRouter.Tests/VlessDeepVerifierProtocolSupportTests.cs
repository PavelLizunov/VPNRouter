#nullable enable

using System.Text.Json.Nodes;
using Serilog;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

public class VlessDeepVerifierProtocolSupportTests
{
    private static ILogger SilentLogger() => new LoggerConfiguration().CreateLogger();
    private const string NoBinaryPath = @"Z:\definitely\not\here\sing-box.exe";

    private static VlessServerEntry AwgEntry() => new()
    {
        Name = "awg-test",
        Protocol = "amneziawg",
        Server = "1.2.3.4",
        Port = 51820,
        Awg = new AwgConfig
        {
            PrivateKey = "priv",
            Address = new List<string> { "10.66.0.2/32" },
            PeerPublicKey = "pub",
            Jc = 4, Jmin = 40, Jmax = 70, S1 = 15, S2 = 68,
            H1 = "123456",
        },
    };

    private static VlessServerEntry XhttpEntry() => new()
    {
        Name = "xhttp-test",
        Protocol = "vless",
        Server = "1.2.3.4",
        Port = 443,
        Uuid = "11111111-2222-3333-4444-555555555555",
        Flow = "xtls-rprx-vision",
        Transport = new VlessTransportConfig { Type = "xhttp", Path = "/probe" },
    };

    private static void WithOverrides(bool awg, bool xhttp, Action body)
    {
        SingBoxFeatures.OverrideAwg = awg;
        SingBoxFeatures.OverrideXhttp = xhttp;
        try { body(); }
        finally { SingBoxFeatures.ResetForTests(); }
    }

    private static async Task WithOverridesAsync(bool awg, bool xhttp, Func<Task> body)
    {
        SingBoxFeatures.OverrideAwg = awg;
        SingBoxFeatures.OverrideXhttp = xhttp;
        try { await body(); }
        finally { SingBoxFeatures.ResetForTests(); }
    }

    [Fact]
    public async Task VerifyAsync_Awg_CoreWithoutAwg_ReturnsUnsupportedByVerifier()
        => await WithOverridesAsync(awg: false, xhttp: false, async () =>
        {
            var verifier = new VlessDeepVerifier(SilentLogger(), NoBinaryPath);
            var r = await verifier.VerifyAsync(AwgEntry(), measureBandwidth: false,
                TestContext.Current.CancellationToken);
            Assert.False(r.Ok);
            Assert.Equal(DeepVerifyFailurePhase.UnsupportedByVerifier, r.FailurePhase);
            Assert.Contains("with_awg", r.Error);
        });

    [Fact]
    public async Task VerifyAsync_Xhttp_CoreWithoutXhttp_ReturnsUnsupportedByVerifier()
        => await WithOverridesAsync(awg: false, xhttp: false, async () =>
        {
            var verifier = new VlessDeepVerifier(SilentLogger(), NoBinaryPath);
            var r = await verifier.VerifyAsync(XhttpEntry(), measureBandwidth: false,
                TestContext.Current.CancellationToken);
            Assert.False(r.Ok);
            Assert.Equal(DeepVerifyFailurePhase.UnsupportedByVerifier, r.FailurePhase);
            Assert.Contains("with_xhttp", r.Error);
        });

    [Fact]
    public async Task VerifyAsync_Awg_CoreWithAwg_PassesGate_FailsOnMissingBinaryAsLocalSpawn()
        => await WithOverridesAsync(awg: true, xhttp: false, async () =>
        {
            var verifier = new VlessDeepVerifier(SilentLogger(), NoBinaryPath);
            var r = await verifier.VerifyAsync(AwgEntry(), measureBandwidth: false,
                TestContext.Current.CancellationToken);
            Assert.False(r.Ok);
            Assert.Equal(DeepVerifyFailurePhase.LocalSpawn, r.FailurePhase);
        });

    [Fact]
    public void BuildSingleOutboundConfig_Awg_EmitsEndpointNotVlessOutbound()
    {
        var json = VlessDeepVerifier.BuildSingleOutboundConfig(AwgEntry(), 1080, 9090);
        var root = JsonNode.Parse(json)!.AsObject();

        var endpoints = Assert.IsType<JsonArray>(root["endpoints"]);
        var ep = Assert.IsType<JsonObject>(endpoints[0]);
        Assert.Equal("wireguard", (string?)ep["type"]);
        Assert.Equal("proxy", (string?)ep["tag"]);
        Assert.Equal(4, (int?)ep["jc"]);
        Assert.Equal("1.2.3.4", (string?)ep["peers"]![0]!["address"]);

        var outbounds = Assert.IsType<JsonArray>(root["outbounds"]);
        Assert.DoesNotContain(outbounds, o => (string?)o?["type"] == "vless");
        Assert.Equal("proxy", (string?)root["route"]!["final"]);
    }

    [Fact]
    public void BuildVlessOutbound_Xhttp_EmitsTransport_AndDropsVisionFlow()
    {
        var outbound = VlessDeepVerifier.BuildVlessOutbound(XhttpEntry());

        var t = Assert.IsType<JsonObject>(outbound["transport"]);
        Assert.Equal("xhttp", (string?)t["type"]);
        Assert.Equal("auto", (string?)t["mode"]);
        Assert.Equal("/probe", (string?)t["path"]);

        Assert.True(outbound["flow"] is null);
    }
}
