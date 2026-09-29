using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

public class ConfigGeneratorQuicBlockTests
{
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
        s.Vless.Servers = mixed
            ? new List<VlessServerEntry> { VisionServer(), NoFlowServer(host: "1.2.3.4") }
            : new List<VlessServerEntry> { VisionServer() };
        return s;
    }

    private static Profile Profile() => new() { Name = "P", DnsMode = "vpn_only" };

    private static bool IsQuicReject(RouteRule r) => r.Protocol == "quic" && r.Action == "reject";
    private static bool IsUdp443Reject(RouteRule r) =>
        r.Network == "udp" && r.Port != null && r.Port.Contains(443) && r.Action == "reject";

    [Fact]
    public void FullTunnel_VlessOnly_RejectsUdp443Globally()
    {
        var cfg = ConfigGenerator.Generate(Profile(), new[] { "Discord.exe" }, Settings("full"));

        var reject = cfg.Route.Rules.Single(IsUdp443Reject);
        Assert.Null(reject.ProcessName);
        Assert.Null(reject.Outbound);
    }

    [Fact]
    public void SplitInclude_VlessOnly_RejectsUdp443ScopedToRoutedApps()
    {
        var cfg = ConfigGenerator.Generate(Profile(), new[] { "Discord.exe", "chrome.exe" },
            Settings("split", appsMode: "include"));

        var reject = cfg.Route.Rules.Single(IsUdp443Reject);
        Assert.NotNull(reject.ProcessName);
        Assert.Contains("Discord.exe", reject.ProcessName!);
        Assert.Contains("chrome.exe", reject.ProcessName!);
    }

    [Fact]
    public void FullTunnel_QuicReject_PassesSingBoxCheck()
    {
        var singBoxPath = @"C:\ProgramData\VPNRouter\bin\sing-box.exe";
        if (!File.Exists(singBoxPath))
            return;

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
