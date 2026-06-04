using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// v2.41.0-r4: QUIC-block-on-TCP-only-proxy (YouTube full-tunnel fix).
///
/// When the proxy is VLESS+Reality (TCP-only, no UDP-capable TUIC/Hysteria2
/// sibling), QUIC (HTTP/3 over UDP/443) tunneled over the reliable
/// VLESS-over-TCP stream stalls (head-of-line blocking / "TCP-over-TCP
/// meltdown"). <see cref="ConfigGenerator"/> emits a QUIC reject rule so the
/// browser falls back to HTTP/2-over-TCP, which rides VLESS cleanly.
///
/// Root-caused from a real Windows user diagnostics bundle
/// (VPNRouter-diagnostics-20260604-165536.zip): full-tunnel VLESS subscription,
/// 40 UDP packet-connections to *.googlevideo.com:443 routed into vless[proxy],
/// no QUIC rule → constant YouTube buffering.
/// </summary>
public class ConfigGeneratorQuicBlockTests
{
    // A valid 32-byte base64url Reality public key + hex short_id so the
    // generated config also passes LeakProtection.ValidateConfig (used by the
    // sing-box check integration test). Same key the resolver integration test
    // uses; the host is a placeholder.
    private const string RealityPublicKey = "gDawCMB0X6iGXZkG8nZIFW5TaaW29x0DMzWijN-gc2A";
    private const string RealityShortId = "d86e92a0c6dd2271";

    private static VlessServerEntry VisionServer(string name = "main", string host = "1.2.3.4") => new()
    {
        Name = name,
        Server = host,
        Port = 443,
        Uuid = "11111111-1111-1111-1111-111111111111",
        Flow = "xtls-rprx-vision",
        Security = "reality",
        Reality = new VlessRealityConfig
        {
            Enabled = true,
            ServerName = "www.microsoft.com",
            Fingerprint = "chrome",
            PublicKey = RealityPublicKey,
            ShortId = RealityShortId
        }
    };

    private static VlessServerEntry NoFlowServer(string name = "udp", string host = "5.6.7.8") => new()
    {
        Name = name,
        Server = host,
        Port = 443,
        Uuid = "22222222-2222-2222-2222-222222222222",
        Security = "reality",
        Reality = new VlessRealityConfig
        {
            Enabled = true,
            ServerName = "www.microsoft.com",
            Fingerprint = "chrome",
            PublicKey = RealityPublicKey,
            ShortId = RealityShortId
        }
    };

    private static AppSettings Settings(string routingMode, string appsMode = "include",
        bool blockQuic = true, bool mixed = false)
    {
        var s = new AppSettings
        {
            App = new AppConfig
            {
                LogLevel = "info",
                RoutingMode = routingMode,
                RoutingAppsMode = appsMode,
                BlockQuicOnTcpProxy = blockQuic
            },
            Tun = new TunSettings(),
            Dns = new DnsSettings { VpnDns = "https://1.1.1.1/dns-query" },
            SingBox = new SingBoxSettings(),
            Vless = new VlessConfig()
        };
        // For the dual-outbound (hasUdpProxy) case the flow + no-flow servers
        // must share an IP — GetActiveServers() only keeps the active server's
        // TCP+UDP pair (same host), so a different-IP no-flow server is dropped.
        s.Vless.Servers = mixed
            ? new List<VlessServerEntry> { VisionServer(), NoFlowServer(host: "1.2.3.4") }
            : new List<VlessServerEntry> { VisionServer() };
        return s;
    }

    private static Profile Profile() => new() { Name = "P", DnsMode = "vpn_only" };

    private static bool IsQuicReject(RouteRule r) => r.Protocol == "quic" && r.Action == "reject";

    [Fact]
    public void FullTunnel_VlessOnly_RejectsQuicGlobally()
    {
        var cfg = ConfigGenerator.Generate(Profile(), new[] { "Discord.exe" }, Settings("full"));

        var reject = cfg.Route.Rules.Single(IsQuicReject);
        Assert.Null(reject.ProcessName); // global — not scoped to any app
        Assert.Null(reject.Outbound);    // reject does not route to an outbound
        Assert.Null(reject.Network);     // protocol-sniff based, not raw udp/443
    }

    [Fact]
    public void SplitInclude_VlessOnly_RejectsQuicScopedToRoutedApps()
    {
        var cfg = ConfigGenerator.Generate(Profile(), new[] { "Discord.exe", "chrome.exe" },
            Settings("split", appsMode: "include"));

        var reject = cfg.Route.Rules.Single(IsQuicReject);
        Assert.NotNull(reject.ProcessName);
        Assert.Contains("Discord.exe", reject.ProcessName!);
        Assert.Contains("chrome.exe", reject.ProcessName!);
    }

    [Fact]
    public void ExcludeMode_VlessOnly_RejectsQuicGlobally()
    {
        var s = Settings("split", appsMode: "exclude");
        s.App.RoutingAppsExclude = new List<string> { "Steam.exe" };

        var cfg = ConfigGenerator.Generate(Profile(), Array.Empty<string>(), s);

        var reject = cfg.Route.Rules.Single(IsQuicReject);
        Assert.Null(reject.ProcessName); // exclude → final=proxy → block globally
    }

    [Fact]
    public void BlockQuicDisabled_NoRejectRule()
    {
        var cfg = ConfigGenerator.Generate(Profile(), new[] { "Discord.exe" },
            Settings("full", blockQuic: false));

        Assert.DoesNotContain(cfg.Route.Rules, IsQuicReject);
    }

    [Fact]
    public void HasUdpProxy_NoRejectRule()
    {
        // Mixed flow + no-flow servers → BuildOutbounds emits a proxy-udp
        // outbound → hasUdpProxy is true → the user's deliberate UDP routing is
        // honoured, QUIC is NOT rejected.
        var cfg = ConfigGenerator.Generate(Profile(), new[] { "Discord.exe" },
            Settings("full", mixed: true));

        Assert.DoesNotContain(cfg.Route.Rules, IsQuicReject);
    }

    [Fact]
    public void RejectSitsAfterPrivateIpRule()
    {
        var cfg = ConfigGenerator.Generate(Profile(), new[] { "Discord.exe" }, Settings("full"));

        var rules = cfg.Route.Rules;
        var privateIdx = rules.FindIndex(r => r.IpIsPrivate == true);
        var rejectIdx = rules.FindIndex(IsQuicReject);
        Assert.True(privateIdx >= 0 && rejectIdx > privateIdx,
            "QUIC reject must come after the private-IP direct rule so LAN QUIC is untouched");
    }

    [Fact]
    public void SplitInclude_RejectBeforePerAppProxyRoute()
    {
        var cfg = ConfigGenerator.Generate(Profile(), new[] { "Discord.exe" },
            Settings("split", appsMode: "include"));

        var rules = cfg.Route.Rules;
        var rejectIdx = rules.FindIndex(IsQuicReject);
        var proxyRouteIdx = rules.FindIndex(r =>
            r.Action == "route" && r.Outbound == "proxy" && r.ProcessName != null);
        Assert.True(rejectIdx >= 0 && proxyRouteIdx > rejectIdx,
            "QUIC reject must precede the per-app proxy route so QUIC is rejected, not proxied");
    }

    [Fact]
    public void SplitInclude_NoProcesses_NoRejectRule()
    {
        // Include mode with nothing routed → final=direct, nothing rides the
        // proxy, so there is no tunneled QUIC to block.
        var cfg = ConfigGenerator.Generate(Profile(), Array.Empty<string>(),
            Settings("split", appsMode: "include"));

        Assert.DoesNotContain(cfg.Route.Rules, IsQuicReject);
    }

    [Fact]
    public void DefaultSettings_BlockQuicIsOn()
    {
        // The fix is a strict improvement for the dominant config, so it must be
        // on without any user action.
        Assert.True(new AppConfig().BlockQuicOnTcpProxy);
    }

    /// <summary>
    /// Validates the generated full-tunnel config (with the QUIC reject rule)
    /// against the real sing-box 1.13 binary — proves
    /// <c>{ "protocol": "quic", "action": "reject" }</c> is accepted syntax and
    /// the config still passes LeakProtection. Skips when the binary is absent
    /// (CI without sing-box), mirroring the other *PassesSingBoxCheck tests.
    /// </summary>
    [Fact]
    public void FullTunnel_QuicReject_PassesSingBoxCheck()
    {
        var singBoxPath = @"C:\ProgramData\VPNRouter\bin\sing-box.exe";
        if (!File.Exists(singBoxPath))
            return; // sing-box.exe not installed locally — skip on CI without binary

        var cfg = ConfigGenerator.Generate(Profile(), new[] { "Discord.exe" }, Settings("full"));

        Assert.Contains(cfg.Route.Rules, IsQuicReject);
        var validation = LeakProtection.ValidateConfig(cfg);
        Assert.True(validation.IsValid,
            $"LeakProtection rejected QUIC-block config: {string.Join("; ", validation.Errors)}");

        var json = ConfigGenerator.Serialize(cfg);
        var tempPath = Path.Combine(Path.GetTempPath(), $"vpnrouter-quic-check-{Guid.NewGuid()}.json");
        try
        {
            File.WriteAllText(tempPath, json);

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = singBoxPath,
                Arguments = $"check -c \"{tempPath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = System.Diagnostics.Process.Start(psi)!;
            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(10000);

            Assert.True(proc.ExitCode == 0,
                $"sing-box check failed on QUIC-block config (exit {proc.ExitCode}):\n{stderr}\n\nConfig:\n{json}");
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }
}
