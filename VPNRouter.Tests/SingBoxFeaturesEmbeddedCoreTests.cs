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

    [Theory]
    [InlineData("1.14.4-vpnctl.4", true)]
    [InlineData("1.14.4-vpnctl.5", true)]
    [InlineData("1.15.0-vpnctl.1", true)]
    [InlineData("1.15.0-vpnctl.invalid", false)]
    [InlineData("1.14.4-vpnctl.3", false)]
    [InlineData("1.14.0-vpnctl.9", false)]
    [InlineData("1.14.4-vpnctl-turnrelay.5", true)]
    [InlineData("1.14.4-vpnctl-turnrelay.4", false)]
    [InlineData("1.14.3-vpnctl-turnrelay.5", false)]
    [InlineData("1.14.4", false)]
    public void HasTurnRelayVersion_PinsTheReleaseGate(string version, bool expected)
    {
        Assert.Equal(expected, SingBoxFeatures.HasTurnRelayVersion(version));
    }

    [Fact]
    public void SetEmbeddedCoreVersion_EnablesTurnRelayForThePinnedBuild()
    {
        SingBoxFeatures.SetEmbeddedCoreVersion("1.14.4-vpnctl.4");
        Assert.True(SingBoxFeatures.TurnRelayAvailable);
    }

    [Fact]
    public void SetEmbeddedCoreVersion_KeepsTurnRelayOffForOlderBuilds()
    {
        SingBoxFeatures.SetEmbeddedCoreVersion("1.14.4-vpnctl.3");
        Assert.False(SingBoxFeatures.TurnRelayAvailable);
    }

    [Fact]
    public void Override_StillWinsOverEmbeddedCore()
    {
        SingBoxFeatures.OverrideAwg = false;
        SingBoxFeatures.OverrideXhttp = false;

        Assert.False(SingBoxFeatures.AwgAvailable);
        Assert.False(SingBoxFeatures.XhttpAvailable);
    }

    [Fact]
    public void HealthCheck_EmbeddedCoreIsNotReportedAsMissingBinary()
    {
        var results = new System.Collections.Generic.List<HealthCheck.Result>();

        HealthCheck.CheckSingBoxBinary(results);

        var only = Assert.Single(results);
        Assert.Equal(HealthCheck.Level.Ok, only.Severity);
    }
}
