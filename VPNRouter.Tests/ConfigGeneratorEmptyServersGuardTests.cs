using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

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
            Vless = new VlessConfig()
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
            Vless = new VlessConfig()
        };
        var profile = new Profile
        {
            Name = "T",
            DnsMode = "vpn_only",
            Processes = new() { new ProcessRule { Name = "Discord.exe", ScanPatterns = new[] { "Discord.exe" } } }
        };

        var resolved = VlessServersResolver.Resolve(settings);
        Assert.Single(resolved);

        var config = ConfigGenerator.Generate(profile, new[] { "Discord.exe" }, settings);

        var proxy = config.Outbounds.FirstOrDefault(o => o.Tag == "proxy");
        Assert.NotNull(proxy);
        Assert.Equal("vless", proxy!.Type);
        Assert.Equal("104.194.156.93", proxy.Server);
        Assert.Equal(443, proxy.ServerPort);
        Assert.Equal("xtls-rprx-vision", proxy.Flow);
        Assert.NotNull(proxy.Tls);
        Assert.True(proxy.Tls!.Reality?.Enabled);
        Assert.Equal("gDawCMB0X6iGXZkG8nZIFW5TaaW29x0DMzWijN-gc2A", proxy.Tls.Reality!.PublicKey);
    }

    [Fact]
    public void Generate_FromSubscribeMode_PassesSingBoxCheck()
    {
        var singBoxPath = @"C:\ProgramData\VPNRouter\bin\sing-box.exe";
        if (!File.Exists(singBoxPath))
            return;

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
            Vless = new VlessConfig()
        };
        var profile = new Profile
        {
            Name = "TestProfile",
            DnsMode = "vpn_only",
            Processes = new() { new ProcessRule { Name = "Discord.exe", ScanPatterns = new[] { "Discord.exe" } } }
        };

        var resolved = VlessServersResolver.Resolve(settings);
        Assert.Single(resolved);
        var sbConfig = ConfigGenerator.Generate(profile, new[] { "Discord.exe" }, settings);
        var validation = LeakProtection.ValidateConfig(sbConfig);
        Assert.True(validation.IsValid,
            $"LeakProtection validation failed: {string.Join("; ", validation.Errors)}");
        var json = ConfigGenerator.Serialize(sbConfig);

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
