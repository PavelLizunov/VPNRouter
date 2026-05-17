using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Core.Services.EmergencyChannel;

namespace VPNRouter.Tests;
// ═══════════════════════════════════════════════════════════════════════════════
// ConfigGenerator hard guard — v2.28.2
//
// If ConfigGenerator gets called without servers (caller forgot to resolve),
// it MUST throw rather than emit a JSON with route rules pointing at a missing
// "proxy" outbound. The original bug produced a silently-broken sing-box config
// that sing-box loaded without complaint, then drove urltest probes against
// the upstream server with no VLESS handshake (-> "flow mismatch" log spam).
// ═══════════════════════════════════════════════════════════════════════════════

public class ConfigGeneratorEmptyServersGuardTests
{
    [Fact]
    public void EmptyServers_ThrowsClearly()
    {
        var settings = new AppSettings
        {
            App = new AppConfig { LogLevel = "info", ConfigMode = "generated" },
            Tun = new TunSettings(),
            Dns = new DnsSettings { VpnDns = "https://1.1.1.1/dns-query" },
            SingBox = new SingBoxSettings(),
            Vless = new VlessConfig() // ← critical: no servers
        };
        var profile = new Profile
        {
            Name = "T",
            DnsMode = "vpn_only",
            Processes = new() { new ProcessRule { Name = "Discord.exe", ScanPatterns = new[] { "Discord.exe" } } }
        };

        var ex = Assert.Throws<InvalidOperationException>(
            () => ConfigGenerator.Generate(profile, new[] { "Discord.exe" }, settings));

        Assert.Contains("no active VLESS servers", ex.Message);
        Assert.Contains("VlessServersResolver", ex.Message);
    }

    [Fact]
    public void ResolverThenGenerate_ProducesProxyOutbound()
    {
        // End-to-end: subscribe mode w/ servers → Resolve → Generate → JSON with proxy.
        // This is the path that BROKE in v2.28.1 (Apply skipped Resolve step).
        var settings = new AppSettings
        {
            App = new AppConfig
            {
                LogLevel = "info",
                ConfigMode = "subscribe",
                ActiveSubscriptionServer = "main",
                Subscriptions = new List<SubscriptionEntry>
                {
                    new()
                    {
                        Name = "test-sub",
                        Url = "https://example.com",
                        Enabled = true,
                        Servers = new List<VlessServerEntry>
                        {
                            new()
                            {
                                Name = "main",
                                Server = "104.194.156.93",
                                Port = 443,
                                Uuid = "b25684c3-90d6-454a-a911-4e0abba568b0",
                                Flow = "xtls-rprx-vision",
                                Security = "reality",
                                Reality = new VlessRealityConfig
                                {
                                    Enabled = true,
                                    ServerName = "www.microsoft.com",
                                    Fingerprint = "chrome",
                                    PublicKey = "gDawCMB0X6iGXZkG8nZIFW5TaaW29x0DMzWijN-gc2A",
                                    ShortId = "d86e92a0c6dd2271"
                                }
                            }
                        }
                    }
                }
            },
            Tun = new TunSettings(),
            Dns = new DnsSettings { VpnDns = "https://1.1.1.1/dns-query" },
            SingBox = new SingBoxSettings(),
            Vless = new VlessConfig() // empty — must be populated by Resolve
        };
        var profile = new Profile
        {
            Name = "T",
            DnsMode = "vpn_only",
            Processes = new() { new ProcessRule { Name = "Discord.exe", ScanPatterns = new[] { "Discord.exe" } } }
        };

        // Step 1: Resolve (what VpnEngine.Apply now does, didn't before)
        var resolved = VlessServersResolver.Resolve(settings);
        Assert.Single(resolved);

        // Step 2: Generate
        var config = ConfigGenerator.Generate(profile, new[] { "Discord.exe" }, settings);

        // Verification: proxy outbound must exist with correct flow
        var proxy = config.Outbounds.FirstOrDefault(o => o.Tag == "proxy");
        Assert.NotNull(proxy);
        Assert.Equal("vless", proxy!.Type);
        Assert.Equal("104.194.156.93", proxy.Server);
        Assert.Equal(443, proxy.ServerPort);
        Assert.Equal("xtls-rprx-vision", proxy.Flow);
        Assert.NotNull(proxy.Tls);
        Assert.True(proxy.Tls!.Reality?.Enabled);
        Assert.Equal("gDawCMB0X6iGXZkG8nZIFW5TaaW29x0DMzWijN-gc2A", proxy.Tls.Reality.PublicKey);
    }

    /// <summary>
    /// End-to-end integration test: subscribe-mode AppSettings → VlessServersResolver
    /// → ConfigGenerator → sing-box check. Verifies the generated JSON is not just
    /// internally consistent but actually loadable by sing-box 1.13. This pins the
    /// fix at the binary level — if a future change breaks compatibility with
    /// upstream sing-box validator, this test fails immediately.
    /// </summary>
    [Fact]
    public void Generate_FromSubscribeMode_PassesSingBoxCheck()
    {
        var singBoxPath = @"C:\ProgramData\VPNRouter\bin\sing-box.exe";
        if (!File.Exists(singBoxPath))
            return; // sing-box.exe not installed locally — skip on CI without binary

        var settings = new AppSettings
        {
            App = new AppConfig
            {
                LogLevel = "info",
                ConfigMode = "subscribe",
                ActiveSubscriptionServer = "main",
                Subscriptions = new List<SubscriptionEntry>
                {
                    new()
                    {
                        Name = "field-test-subscription",
                        Url = "https://example.com",
                        Enabled = true,
                        Servers = new List<VlessServerEntry>
                        {
                            new()
                            {
                                Name = "main",
                                Server = "104.194.156.93",
                                Port = 443,
                                Uuid = "b25684c3-90d6-454a-a911-4e0abba568b0",
                                Flow = "xtls-rprx-vision",
                                Security = "reality",
                                Reality = new VlessRealityConfig
                                {
                                    Enabled = true,
                                    ServerName = "www.microsoft.com",
                                    Fingerprint = "chrome",
                                    PublicKey = "gDawCMB0X6iGXZkG8nZIFW5TaaW29x0DMzWijN-gc2A",
                                    ShortId = "d86e92a0c6dd2271"
                                }
                            }
                        }
                    }
                }
            },
            Tun = new TunSettings
            {
                InterfaceName = "VPNRouter-TUN",
                Ipv4Address = "172.19.0.1/30",
                Mtu = 9000,
                AutoRoute = true,
                StrictRoute = false
            },
            Dns = new DnsSettings
            {
                VpnDns = "https://1.1.1.1/dns-query",
                Strategy = "ipv4_only"
            },
            SingBox = new SingBoxSettings { ClashApi = "127.0.0.1:9090" },
            Vless = new VlessConfig() // empty — must be populated by Resolve
        };
        var profile = new Profile
        {
            Name = "TestProfile",
            DnsMode = "vpn_only",
            Processes = new() { new ProcessRule { Name = "Discord.exe", ScanPatterns = new[] { "Discord.exe" } } }
        };

        // Pipeline same as VpnEngine.Apply now does
        var resolved = VlessServersResolver.Resolve(settings);
        Assert.Single(resolved);
        var sbConfig = ConfigGenerator.Generate(profile, new[] { "Discord.exe" }, settings);
        var validation = LeakProtection.ValidateConfig(sbConfig);
        Assert.True(validation.IsValid,
            $"LeakProtection validation failed: {string.Join("; ", validation.Errors)}");
        var json = ConfigGenerator.Serialize(sbConfig);

        // Run sing-box check on the generated JSON
        var tempPath = Path.Combine(Path.GetTempPath(), $"vpnrouter-test-resolver-{Guid.NewGuid()}.json");
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
                $"sing-box check failed on resolver+generator output (exit {proc.ExitCode}):\n{stderr}\n\nConfig:\n{json}");
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }
}
