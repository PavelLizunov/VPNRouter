#nullable enable

using System.Text.Json;
using VPNRouter.Core;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using Xunit;
using CoreStrings = VPNRouter.Core.Localization.Strings;

namespace VPNRouter.Tests;

/// <summary>
/// Pins the urltest R5 verdict-driven Auto-pool filter in <see cref="ConfigGenerator"/>:
/// with AutoSelectBestServer on, members with a FRESH persisted
/// ProtocolHandshakeBlockedLikely verdict are dropped from the urltest group;
/// fail-open keeps the full pool when everything is blocked; manual (non-auto)
/// selection is never overridden. Plus the audit's wording pin: Auto is a
/// "quick web test", never a claim of full verification.
/// </summary>
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
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
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

    /// <summary>Members of the urltest "proxy" group, or null when no group was emitted.</summary>
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

        // A wrong verdict must never brick connectivity — the full pool survives.
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
        Assert.Equal(3, members!.Count);   // recovered server gets its chance back
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
        // Auto OFF: the user explicitly picked the (blocked-marked) server — the
        // generator must still emit it as the proxy. Respect the human.
        var a = Vless("srv-a", "10.0.0.1");
        ServerHealthStore.Record(a, ServerHealthVerdict.ProtocolHandshakeBlockedLikely);

        var config = ConfigGenerator.Generate(FullProfile(), new[] { "x.exe" }, Settings(autoSelect: false, a));
        var json = JsonSerializer.Serialize(config, VPNRouter.Core.Json.AppJsonContext.Default.SingBoxConfig);

        Assert.Contains("\"10.0.0.1\"", json);       // the chosen server is the proxy
        Assert.Null(UrltestMembers(config));          // no auto group in manual mode
    }

    // ── R3: provider/subnet-level drop ───────────────────────────────────────

    [Fact]
    public void HighRiskSubnet_DropsItsUntestedSiblingsToo()
    {
        // Two blocked on net:10.0.0.0/24 + one healthy elsewhere → the subnet is
        // HighRisk (>=2 blocked + healthy alternative) → its UNTESTED sibling is
        // dropped from the pool along with the blocked ones.
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

        // Pool collapsed to the single healthy member → direct outbound, no group.
        Assert.Null(UrltestMembers(config));
        Assert.Contains("\"77.7.7.7\"", json);
        Assert.DoesNotContain("\"10.0.0.3\"", json);   // the untested sibling went with its subnet
    }

    [Fact]
    public void OneBlockedOnSubnet_DoesNotCondemnTheSubnet()
    {
        // Below the >=2 threshold the subnet is NOT HighRisk — only the blocked
        // member itself is dropped (R5), its sibling stays in the group.
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
        // No healthy server elsewhere → could be a client-wide outage, not a
        // subnet block — nothing extra is dropped beyond the R5 individual drop.
        var blocked1 = Vless("blk-1",   "10.0.0.1");
        var blocked2 = Vless("blk-2",   "10.0.0.2");
        var sibling  = Vless("sibling", "10.0.0.3");
        ServerHealthStore.Record(blocked1, ServerHealthVerdict.ProtocolHandshakeBlockedLikely, providerKey: "net:10.0.0.0/24");
        ServerHealthStore.Record(blocked2, ServerHealthVerdict.ProtocolHandshakeBlockedLikely, providerKey: "net:10.0.0.0/24");
        ServerHealthStore.Record(sibling,  ServerHealthVerdict.TcpOpenProtocolUntested,        providerKey: "net:10.0.0.0/24");

        var config = ConfigGenerator.Generate(FullProfile(), new[] { "x.exe" },
            Settings(autoSelect: true, blocked1, blocked2, sibling));
        var json = JsonSerializer.Serialize(config, VPNRouter.Core.Json.AppJsonContext.Default.SingBoxConfig);

        Assert.Contains("\"10.0.0.3\"", json);   // the sibling survives
    }

    // ── Audit regression #6: wording pins ────────────────────────────────────

    [Fact]
    public void AutoSelectWording_IsQuickWebTest_NotBestServerClaim()
    {
        var prev = CoreStrings.Lang;
        try
        {
            CoreStrings.Lang = "ru";
            Assert.Contains("быстрому веб-тесту", CoreStrings.AutoSelectBestServer);
            Assert.DoesNotContain("лучшего сервера", CoreStrings.AutoSelectBestServer);
            Assert.Contains("не полная проверка", CoreStrings.AutoSelectBestServerTip);

            CoreStrings.Lang = "en";
            Assert.Contains("quick web test", CoreStrings.AutoSelectBestServer);
            Assert.Contains("not full VPN-protocol verification", CoreStrings.AutoSelectBestServerTip);
        }
        finally { CoreStrings.Lang = prev; }
    }
}
