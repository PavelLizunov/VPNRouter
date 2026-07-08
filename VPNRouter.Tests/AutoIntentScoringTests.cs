using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

public sealed class AutoIntentScoringTests
{
    [Fact]
    public void Gaming_PrefersUdpNativeTransportOverVless()
    {
        var pick = ConnectionIntentScorer.Pick(new[]
        {
            S("VLESS", "vless"),
            S("HY2", "hysteria2"),
            S("AWG", "amneziawg")
        }, ConnectionIntent.Gaming);

        Assert.NotNull(pick);
        Assert.Equal("AWG", pick!.Server.Name);
        Assert.Contains("games", pick.Reason);
    }

    [Fact]
    public void Privacy_DoesNotInventDirectBypass()
    {
        var pick = ConnectionIntentScorer.Pick(new[]
        {
            S("VLESS", "vless"),
            S("HY2", "hysteria2")
        }, ConnectionIntent.Privacy);

        Assert.NotNull(pick);
        Assert.NotEqual("direct", pick!.Server.Protocol);
        Assert.Contains("direct bypass is not automatic", pick.Reason);
    }

    [Fact]
    public void General_KeepsExplicitAliveServer()
    {
        var vless = new ServerLiveness(S("VLESS", "vless"), true, 20);
        var hy2 = new ServerLiveness(S("HY2", "hysteria2"), true, 5);

        var pick = ConnectionIntentScorer.PickServer(
            new[] { vless, hy2 },
            ConnectionIntent.General,
            "VLESS");

        Assert.Equal("VLESS", pick?.Name);
    }

    private static VlessServerEntry S(string name, string protocol) => new()
    {
        Name = name,
        Protocol = protocol,
        Server = "1.2.3.4",
        Port = 443
    };
}
