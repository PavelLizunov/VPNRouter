#nullable enable
using System.Linq;
using VPNRouter.Core.Models;
using Xunit;

namespace VPNRouter.Tests;

public sealed class AutoSelectServerPoolTests
{
    private static VlessServerEntry Srv(string name, string ip, string proto, string? flow) =>
        new() { Name = name, Server = ip, Port = 443, Uuid = "u", Protocol = proto, Flow = flow ?? "" };

    private static VlessConfig Cfg(bool auto, string active) => new()
    {
        AutoSelectBestServer = auto,
        ActiveServer = active,
        Servers = new()
        {
            Srv("DE", "1.1.1.1", "vless", "xtls-rprx-vision"),
            Srv("IS", "2.2.2.2", "vless", "xtls-rprx-vision"),
            Srv("NL", "3.3.3.3", "vless", "xtls-rprx-vision"),
            Srv("LV-HY2", "4.4.4.4", "hysteria2", null),
        }
    };

    [Fact]
    public void On_Vless_ReturnsAllSameProtocolFlowServers()
    {
        var pool = Cfg(true, "DE").GetActiveServers();
        Assert.Equal(3, pool.Count);
        Assert.All(pool, s => Assert.Equal("vless", s.Protocol));
        Assert.DoesNotContain(pool, s => s.Protocol == "hysteria2");
    }

    [Fact]
    public void On_VlessVision_DropsNoFlowSiblingToAvoidCrossNodeSplit()
    {
        var cfg = new VlessConfig
        {
            AutoSelectBestServer = true,
            ActiveServer = "DE",
            Servers = new()
            {
                Srv("DE", "1.1.1.1", "vless", "xtls-rprx-vision"),
                Srv("IS", "2.2.2.2", "vless", "xtls-rprx-vision"),
                Srv("NoFlowElsewhere", "9.9.9.9", "vless", null),
            }
        };
        var pool = cfg.GetActiveServers();
        Assert.Equal(2, pool.Count);
        Assert.All(pool, s => Assert.False(string.IsNullOrEmpty(s.Flow)));
    }
}
