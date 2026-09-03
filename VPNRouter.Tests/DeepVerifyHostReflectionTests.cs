using System.Net;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

public sealed class DeepVerifyHostReflectionTests
{
    [Fact]
    public void EvaluateProbeResponse_ValidExternalIp_Passes()
    {
        var body = "ip=198.51.100.42\nloc=DE\n";
        var hostPublicIp = IPAddress.Parse("203.0.113.1");

        var (ok, err) = DeepVerifyProbe.EvaluateProbeResponse(body, hostPublicIp);

        Assert.True(ok);
        Assert.Null(err);
    }

    [Theory]
    [InlineData("ip=127.0.0.1\n")]
    [InlineData("ip=10.0.0.5\n")]
    [InlineData("ip=172.16.1.1\n")]
    [InlineData("ip=192.168.1.100\n")]
    [InlineData("ip=100.64.0.1\n")]
    public void EvaluateProbeResponse_LocalOrPrivateIp_Rejected(string body)
    {
        var (ok, err) = DeepVerifyProbe.EvaluateProbeResponse(body);

        Assert.False(ok);
        Assert.Equal("local ip in response", err);
    }

    [Fact]
    public void EvaluateProbeResponse_ReflectsHostPublicIp_Rejected()
    {
        // FCP-02: verify that when a proxy returns the host machine's public IP,
        // it is rejected as transparent/non-anonymizing.
        var hostPublicIp = IPAddress.Parse("203.0.113.88");
        var body = "ip=203.0.113.88\nloc=US\n";

        var (ok, err) = DeepVerifyProbe.EvaluateProbeResponse(body, hostPublicIp);

        Assert.False(ok);
        Assert.Equal("proxy reflects host public ip", err);
    }

    [Fact]
    public void EvaluateProbeResponse_ReflectsKnownHostPublicIp_Rejected()
    {
        var previous = DeepVerifyProbe.KnownHostPublicIp;
        try
        {
            DeepVerifyProbe.KnownHostPublicIp = IPAddress.Parse("198.51.100.77");
            var body = "ip=198.51.100.77\n";

            var (ok, err) = DeepVerifyProbe.EvaluateProbeResponse(body);

            Assert.False(ok);
            Assert.Equal("proxy reflects host public ip", err);
        }
        finally
        {
            DeepVerifyProbe.KnownHostPublicIp = previous;
        }
    }

    [Fact]
    public void EvaluateProbeResponse_MalformedOrMissingIp_Rejected()
    {
        var body = "<html><body>error</body></html>";
        var (ok, err) = DeepVerifyProbe.EvaluateProbeResponse(body);

        Assert.False(ok);
        Assert.Equal("bad response", err);
    }
}
