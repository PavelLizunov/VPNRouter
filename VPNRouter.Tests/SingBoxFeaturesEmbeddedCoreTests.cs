using System;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

[Collection("SingBoxFeaturesSerial")]
public sealed class SingBoxFeaturesEmbeddedCoreTests : IDisposable
{
    public SingBoxFeaturesEmbeddedCoreTests()
    {
        SingBoxFeatures.ResetForTests();
        SingBoxFeatures.EmbeddedCore = true;
    }

    public void Dispose() => SingBoxFeatures.ResetForTests();

    [Fact]
    public void EmbeddedCore_ReportsForkProtocolsWithoutAnExecutable()
    {
        Assert.True(SingBoxFeatures.AwgAvailable);
        Assert.True(SingBoxFeatures.XhttpAvailable);
    }

    [Fact]
    public void EmbeddedCore_SubscriptionKeepsAwgAndXhttpLines()
    {
        const string text =
            "awg://PEER@1.2.3.4:51820?private_key=PRIV&address=10.13.13.2/32#awg-one\n" +
            "vless://11111111-1111-1111-1111-111111111111@example.com:443?security=reality" +
            "&pbk=KEY&sid=01ab&type=xhttp&path=%2Fp&host=cdn.example.com&mode=packet-up&sni=example.com#xhttp-one\n";

        var servers = ServerUriParser.ParseMultiple(text);

        Assert.Equal(2, servers.Count);
        Assert.Contains(servers, s => s.Protocol == "amneziawg");
        Assert.Contains(servers, s => s.Transport?.Type == "xhttp");
        Assert.True(ServerUriParser.IsSupportedScheme("awg3://PEER@1.2.3.4:51820"));
    }

    [Fact]
    public void Override_StillWinsOverEmbeddedCore()
    {
        SingBoxFeatures.OverrideAwg = false;
        SingBoxFeatures.OverrideXhttp = false;

        Assert.False(SingBoxFeatures.AwgAvailable);
        Assert.False(SingBoxFeatures.XhttpAvailable);
    }
}
