using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Core.Services.EmergencyChannel;

namespace VPNRouter.Tests;
// ═══════════════════════════════════════════════════════════════════════════════
// GetEffectiveServers
// ═══════════════════════════════════════════════════════════════════════════════

public class GetEffectiveServersTests
{
    [Fact]
    public void MultiServerList_ReturnsServersList()
    {
        var config = new VlessConfig
        {
            Server = "legacy.example.com",
            Servers = new List<VlessServerEntry>
            {
                new() { Server = "server1.com", Port = 443, Uuid = "uuid-1" },
                new() { Server = "server2.com", Port = 443, Uuid = "uuid-2" }
            }
        };

        var servers = config.GetEffectiveServers();

        Assert.Equal(2, servers.Count);
        Assert.Equal("server1.com", servers[0].Server);
        Assert.Equal("server2.com", servers[1].Server);
    }

    [Fact]
    public void LegacySingleServer_BuildsOneEntry()
    {
        var config = new VlessConfig
        {
            Server = "legacy.example.com",
            Port = 8443,
            Uuid = "test-uuid",
            Flow = "xtls-rprx-vision",
            Security = "reality"
        };

        var servers = config.GetEffectiveServers();

        Assert.Single(servers);
        Assert.Equal("legacy.example.com", servers[0].Server);
        Assert.Equal(8443, servers[0].Port);
        Assert.Equal("test-uuid", servers[0].Uuid);
        Assert.Equal("xtls-rprx-vision", servers[0].Flow);
        Assert.Equal("reality", servers[0].Security);
    }

    [Fact]
    public void NoServersNoLegacy_ReturnsEmpty()
    {
        var config = new VlessConfig();
        var servers = config.GetEffectiveServers();
        Assert.Empty(servers);
    }

    [Fact]
    public void MultiServerList_IgnoresLegacyFields()
    {
        var config = new VlessConfig
        {
            Server = "should-be-ignored.com",
            Port = 9999,
            Uuid = "old-uuid",
            Servers = new List<VlessServerEntry>
            {
                new() { Server = "actual.com", Port = 443, Uuid = "new-uuid" }
            }
        };

        var servers = config.GetEffectiveServers();

        Assert.Single(servers);
        Assert.Equal("actual.com", servers[0].Server);
        Assert.Equal("new-uuid", servers[0].Uuid);
    }
}
