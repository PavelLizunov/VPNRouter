using System.Text.Json;
using VPNRouter.Core.Services;
using VPNRouter.Core.Services.FreeConfigs;

namespace VPNRouter.Tests;

public sealed class DeepVerifierDnsPrivacyTests
{
    [Fact]
    public void SelectionConfigUsesConfiguredDnsAndRoutesUdpStunToTheCandidate()
    {
        var config = VlessDeepVerifier.BuildSingleOutboundConfig(VlessDeepVerifierTests.CleanVlessEntry(),
            new VerificationProxy(10808), localDns: "tls://resolver.example.org:8853", udpPort: 10809);
        using var document = JsonDocument.Parse(config);
        var root = document.RootElement;
        var dns = root.GetProperty("dns").GetProperty("servers");
        Assert.Equal("tls", dns[0].GetProperty("type").GetString());
        Assert.Equal("resolver.example.org", dns[0].GetProperty("server").GetString());
        Assert.Equal(8853, dns[0].GetProperty("server_port").GetInt32());
        Assert.Equal("dns-direct-out", dns[0].GetProperty("detour").GetString());
        Assert.Equal("local", dns[1].GetProperty("type").GetString());
        Assert.Equal("proxy", root.GetProperty("route").GetProperty("final").GetString());
        var udp = root.GetProperty("inbounds")[1];
        Assert.Equal("127.0.0.1", udp.GetProperty("listen").GetString());
        Assert.Equal("udp", udp.GetProperty("network").GetString());
        Assert.Equal(DeepVerifyConstants.StunHost, udp.GetProperty("override_address").GetString());
        Assert.Equal(DeepVerifyConstants.StunPort, udp.GetProperty("override_port").GetInt32());
    }

    [Theory]
    [InlineData(false, false, false, true)]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, false)]
    public void StunReplyRequiresTheExactTransactionAndMappedAddress(
        bool wrongTransaction, bool truncated, bool wrongAttribute, bool valid)
    {
        byte[] request = [0, 1, 0, 0, 0x21, 0x12, 0xa4, 0x42, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12];
        var response = new byte[truncated ? 28 : 32];
        request.CopyTo(response, 0);
        response[0] = 1;
        response[3] = 12;
        response[21] = wrongAttribute ? (byte)0x7f : (byte)0x20;
        response[23] = 8;
        response[25] = 1;
        if (wrongTransaction) response[19]++;

        Assert.Equal(valid, DeepVerifyProbe.IsMatchingStunResponse(response, request));
    }

    [Fact]
    public void TemporaryConfigsUseEncryptedDirectBootstrapDns()
    {
        var entry = VlessDeepVerifierTests.CleanVlessEntry();
        var configs = new[]
        {
            VlessDeepVerifier.BuildSingleOutboundConfig(entry, new VerificationProxy(10808)),
            FreeConfigDeepVerifier.BuildSingleOutboundConfig(entry, new VerificationProxy(10809))
        };

        foreach (var config in configs)
        {
            using var document = JsonDocument.Parse(config);
            var server = document.RootElement
                .GetProperty("dns")
                .GetProperty("servers")[0];

            Assert.Equal("https", server.GetProperty("type").GetString());
            Assert.Equal("8.8.8.8", server.GetProperty("server").GetString());
            Assert.Equal("/dns-query", server.GetProperty("path").GetString());
            Assert.Equal("dns-direct-out", server.GetProperty("detour").GetString());
        }
    }
}
