using System;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

[Collection("SingBoxFeaturesSerial")]
public sealed class AwgLimitTests : IDisposable
{
    public AwgLimitTests()
    {
        SingBoxFeatures.OverrideAwg = true;
        SingBoxFeatures.OverrideXhttp = true;
    }

    public void Dispose() => SingBoxFeatures.ResetForTests();

    private static string Link(string extra) =>
        "awg://PEER@1.2.3.4:51820?private_key=PRIV&address=10.13.13.2/32" + extra;

    [Theory]
    [InlineData("&jc=128&jmin=40&jmax=65507")]
    [InlineData("&jc=0")]
    [InlineData("&jc=5&jmin=50&jmax=50")]
    public void Link_WithinTheCoreLimits_IsAccepted(string extra)
    {
        var entry = ServerUriParser.Parse(Link(extra));
        Assert.NotNull(entry.Awg);
    }

    [Theory]
    [InlineData("&jc=129", "jc must be between 0 and 128")]
    [InlineData("&jc=4&jmin=10&jmax=65508", "jmax must be at most 65507")]
    [InlineData("&jc=4&jmin=100&jmax=50", "jmin must not exceed jmax")]
    [InlineData("&jc=4&jmin=10", "jmin must not exceed jmax")]
    public void Link_BeyondTheCoreLimits_IsRejectedWithTheReason(string extra, string reason)
    {
        var ex = Assert.Throws<FormatException>(() => ServerUriParser.Parse(Link(extra)));
        Assert.Contains(reason, ex.Message);
    }

    [Fact]
    public void Conf_BeyondTheCoreLimits_IsRejectedWithTheReason()
    {
        const string conf = "[Interface]\nPrivateKey = PRIV\nAddress = 10.13.13.2/32\nJc = 200\nJmin = 10\nJmax = 50\n" +
                            "[Peer]\nPublicKey = PEER\nEndpoint = 1.2.3.4:51820\nAllowedIPs = 0.0.0.0/0\n";
        var ex = Assert.Throws<FormatException>(() => ServerUriParser.ParseWireGuardConf(conf));
        Assert.Contains("jc must be between 0 and 128", ex.Message);
    }

    [Fact]
    public void Conf_WithinTheCoreLimits_IsAccepted()
    {
        const string conf = "[Interface]\nPrivateKey = PRIV\nAddress = 10.13.13.2/32\nJc = 4\nJmin = 10\nJmax = 50\n" +
                            "[Peer]\nPublicKey = PEER\nEndpoint = 1.2.3.4:51820\nAllowedIPs = 0.0.0.0/0\n";
        var entry = ServerUriParser.ParseWireGuardConf(conf);
        Assert.Equal(4, entry.Awg!.Jc);
    }
}
