#nullable enable

using System.Text.Json;
using VPNRouter.Core;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using Xunit;
using CoreStrings = VPNRouter.Core.Localization.Strings;

namespace VPNRouter.Tests;

public class ConfigGeneratorAutoSelectHealthFilterTests : IDisposable
{
    private readonly string _prevDataDir;
    private readonly string _tempDir;

    public ConfigGeneratorAutoSelectHealthFilterTests()
    {
        _prevDataDir = AppPaths.DataDir;
        _tempDir = Path.Combine(Path.GetTempPath(), $"vpnrouter-asf-{Guid.NewGuid():N}");
        AppPaths.OverrideDataDir(_tempDir);
        ServerHealthStore.ResetForTests();
    }

    public void Dispose()
    {
        ServerHealthStore.ResetForTests();
        AppPaths.OverrideDataDir(_prevDataDir);
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private static VlessServerEntry Vless(string name, string server) => new()
    {
        Name = name,
        Server = server,
        Port = 443,
        Uuid = "11111111-2222-3333-4444-555555555555",
        Flow = "xtls-rprx-vision",
        Security = "reality",
        Reality = new VlessRealityConfig
        {
            Enabled = true,
            ServerName = "www.cloudflare.com",
            Fingerprint = "chrome",
            PublicKey = "gDawCMB0X6iGXZkG8nZIFW5TaaW29x0DMzWijN-gc2A",
            ShortId = "d86e92a0c6dd2271",
        },
    };

    private static AppSettings Settings(bool autoSelect, params VlessServerEntry[] servers) => new()
    {
        App = new AppConfig { LogLevel = "info", ConfigMode = "generated", RoutingMode = "full" },
        Tun = new TunSettings(),
        Dns = new DnsSettings { VpnDns = "https://1.1.1.1/dns-query" },
        SingBox = new SingBoxSettings(),
        Vless = new VlessConfig
        {
            ActiveServer = servers.Length > 0 ? servers[0].Name : "",
            AutoSelectBestServer = autoSelect,
            Servers = new List<VlessServerEntry>(servers),
        },
    };

    private static Profile FullProfile() => new()
    {
        Name = "FullTunnel",
        DnsMode = "vpn_only",
        Processes = new(),
    };

    private static List<string>? UrltestMembers(SingBoxConfig config)
    {
        var json = JsonSerializer.Serialize(config, VPNRouter.Core.Json.AppJsonContext.Default.SingBoxConfig);
        using var doc = JsonDocument.Parse(json);
        foreach (var ob in doc.RootElement.GetProperty("outbounds").EnumerateArray())
        {
            if (ob.GetProperty("type").GetString() == "urltest")
            {
                var list = new List<string>();
                foreach (var m in ob.GetProperty("outbounds").EnumerateArray())
                    list.Add(m.GetString() ?? "");
                return list;
            }
        }
        return null;
    }

    private static VlessServerEntry Hy2(string name, string server) => new()
    {
        Name = name,
        Server = server,
        Port = 8444,
        Protocol = "hysteria2",
        Password = "pw",
        Tls = new VlessTlsConfig { Enabled = true, ServerName = "example.com" },
    };

    [Fact]
    public void AutoSelect_WithAHysteria2ServerSelected_GroupsTheHysteria2Servers_NotTheVlessOnes()
    {
        var h1 = Hy2("hy-a", "10.1.0.1");
        var h2 = Hy2("hy-b", "10.1.0.2");
        var h3 = Hy2("hy-c", "10.1.0.3");
        var v = Vless("srv-v", "10.0.0.9");

        var config = ConfigGenerator.Generate(FullProfile(), new[] { "x.exe" }, Settings(autoSelect: true, h2, v, h1, h3));
        var members = UrltestMembers(config);

        Assert.NotNull(members);
        Assert.Equal(3, members!.Count);
        Assert.All(members, m => Assert.Contains("hy-", m));
        Assert.DoesNotContain(members, m => m.Contains("srv-v"));
    }

    [Fact]
    public void AutoSelectOff_WithAHysteria2ServerSelected_UsesThatServerAlone()
    {
        var h1 = Hy2("hy-a", "10.1.0.1");
        var h2 = Hy2("hy-b", "10.1.0.2");

        var config = ConfigGenerator.Generate(FullProfile(), new[] { "x.exe" }, Settings(autoSelect: false, h1, h2));

        Assert.Null(UrltestMembers(config));
    }

    [Fact]
    public void AutoSelect_WithOnlyOneHysteria2Server_StaysASingleOutbound()
    {
        var h1 = Hy2("hy-a", "10.1.0.1");
        var v1 = Vless("srv-v1", "10.0.0.8");
        var v2 = Vless("srv-v2", "10.0.0.9");

        var config = ConfigGenerator.Generate(FullProfile(), new[] { "x.exe" }, Settings(autoSelect: true, h1, v1, v2));

        Assert.Null(UrltestMembers(config));
    }

    [Fact]
    public void FreshBlockedMember_IsDroppedFromAutoPool()
    {
        var a = Vless("srv-a", "10.0.0.1");
        var b = Vless("srv-b", "10.0.0.2");
        var c = Vless("srv-c", "10.0.0.3");
        ServerHealthStore.Record(b, ServerHealthVerdict.ProtocolHandshakeBlockedLikely);

        var config = ConfigGenerator.Generate(FullProfile(), new[] { "x.exe" }, Settings(autoSelect: true, a, b, c));
        var members = UrltestMembers(config);

        Assert.NotNull(members);
        Assert.Equal(2, members!.Count);
        Assert.DoesNotContain(members, m => m.Contains("srv-b"));
        Assert.Contains(members, m => m.Contains("srv-a"));
        Assert.Contains(members, m => m.Contains("srv-c"));
    }

    [Fact]
    public void AllBlocked_FailOpen_KeepsFullPool()
    {
        var a = Vless("srv-a", "10.0.0.1");
        var b = Vless("srv-b", "10.0.0.2");
        ServerHealthStore.Record(a, ServerHealthVerdict.ProtocolHandshakeBlockedLikely);
        ServerHealthStore.Record(b, ServerHealthVerdict.ProtocolHandshakeBlockedLikely);

        var config = ConfigGenerator.Generate(FullProfile(), new[] { "x.exe" }, Settings(autoSelect: true, a, b));
        var members = UrltestMembers(config);

        Assert.NotNull(members);
        Assert.Equal(2, members!.Count);
    }

    [Fact]
    public void StaleBlockedVerdict_DoesNotExclude()
    {
        var a = Vless("srv-a", "10.0.0.1");
        var b = Vless("srv-b", "10.0.0.2");
        var c = Vless("srv-c", "10.0.0.3");
        var longAgo = DateTimeOffset.UtcNow - ServerHealthStore.FreshTtl - TimeSpan.FromHours(1);
        ServerHealthStore.Record(b, ServerHealthVerdict.ProtocolHandshakeBlockedLikely, now: longAgo);

        var config = ConfigGenerator.Generate(FullProfile(), new[] { "x.exe" }, Settings(autoSelect: true, a, b, c));
        var members = UrltestMembers(config);

        Assert.NotNull(members);
        Assert.Equal(3, members!.Count);
    }

    [Fact]
    public void HealthyAndUntestedVerdicts_NeverExclude()
    {
        var a = Vless("srv-a", "10.0.0.1");
        var b = Vless("srv-b", "10.0.0.2");
        ServerHealthStore.Record(a, ServerHealthVerdict.Healthy);
        ServerHealthStore.Record(b, ServerHealthVerdict.TcpOpenProtocolUntested);

        var config = ConfigGenerator.Generate(FullProfile(), new[] { "x.exe" }, Settings(autoSelect: true, a, b));
        Assert.Equal(2, UrltestMembers(config)!.Count);
    }

    [Fact]
    public void ManualSelection_IsNeverOverriddenByVerdict()
    {
        var a = Vless("srv-a", "10.0.0.1");
        ServerHealthStore.Record(a, ServerHealthVerdict.ProtocolHandshakeBlockedLikely);

        var config = ConfigGenerator.Generate(FullProfile(), new[] { "x.exe" }, Settings(autoSelect: false, a));
        var json = JsonSerializer.Serialize(config, VPNRouter.Core.Json.AppJsonContext.Default.SingBoxConfig);

        Assert.Contains("\"10.0.0.1\"", json);
        Assert.Null(UrltestMembers(config));
    }

    [Fact]
    public void HighRiskSubnet_DropsItsUntestedSiblingsToo()
    {
        var blocked1  = Vless("blk-1",   "10.0.0.1");
        var blocked2  = Vless("blk-2",   "10.0.0.2");
        var untested  = Vless("sibling", "10.0.0.3");
        var healthy   = Vless("good",    "77.7.7.7");
        ServerHealthStore.Record(blocked1, ServerHealthVerdict.ProtocolHandshakeBlockedLikely, providerKey: "net:10.0.0.0/24");
        ServerHealthStore.Record(blocked2, ServerHealthVerdict.ProtocolHandshakeBlockedLikely, providerKey: "net:10.0.0.0/24");
        ServerHealthStore.Record(untested, ServerHealthVerdict.TcpOpenProtocolUntested,        providerKey: "net:10.0.0.0/24");
        ServerHealthStore.Record(healthy,  ServerHealthVerdict.Healthy,                        providerKey: "net:77.7.7.0/24");

        var config = ConfigGenerator.Generate(FullProfile(), new[] { "x.exe" },
            Settings(autoSelect: true, blocked1, blocked2, untested, healthy));
        var json = JsonSerializer.Serialize(config, VPNRouter.Core.Json.AppJsonContext.Default.SingBoxConfig);

        Assert.Null(UrltestMembers(config));
        Assert.Contains("\"77.7.7.7\"", json);
        Assert.DoesNotContain("\"10.0.0.3\"", json);
    }

    [Fact]
    public void OneBlockedOnSubnet_DoesNotCondemnTheSubnet()
    {
        var blocked  = Vless("blk",     "10.0.0.1");
        var sibling  = Vless("sibling", "10.0.0.3");
        var healthy  = Vless("good",    "77.7.7.7");
        ServerHealthStore.Record(blocked, ServerHealthVerdict.ProtocolHandshakeBlockedLikely, providerKey: "net:10.0.0.0/24");
        ServerHealthStore.Record(sibling, ServerHealthVerdict.TcpOpenProtocolUntested,        providerKey: "net:10.0.0.0/24");
        ServerHealthStore.Record(healthy, ServerHealthVerdict.Healthy,                        providerKey: "net:77.7.7.0/24");

        var config = ConfigGenerator.Generate(FullProfile(), new[] { "x.exe" },
            Settings(autoSelect: true, blocked, sibling, healthy));
        var members = UrltestMembers(config);

        Assert.NotNull(members);
        Assert.Equal(2, members!.Count);
        Assert.Contains(members, m => m.Contains("sibling"));
    }

    [Fact]
    public void HighRiskSubnet_WithoutHealthyAlternative_IsNotFlagged()
    {
        var blocked1 = Vless("blk-1",   "10.0.0.1");
        var blocked2 = Vless("blk-2",   "10.0.0.2");
        var sibling  = Vless("sibling", "10.0.0.3");
        ServerHealthStore.Record(blocked1, ServerHealthVerdict.ProtocolHandshakeBlockedLikely, providerKey: "net:10.0.0.0/24");
        ServerHealthStore.Record(blocked2, ServerHealthVerdict.ProtocolHandshakeBlockedLikely, providerKey: "net:10.0.0.0/24");
        ServerHealthStore.Record(sibling,  ServerHealthVerdict.TcpOpenProtocolUntested,        providerKey: "net:10.0.0.0/24");

        var config = ConfigGenerator.Generate(FullProfile(), new[] { "x.exe" },
            Settings(autoSelect: true, blocked1, blocked2, sibling));
        var json = JsonSerializer.Serialize(config, VPNRouter.Core.Json.AppJsonContext.Default.SingBoxConfig);

        Assert.Contains("\"10.0.0.3\"", json);
    }
}
