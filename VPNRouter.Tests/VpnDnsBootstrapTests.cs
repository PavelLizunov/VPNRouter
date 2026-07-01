#nullable enable

using System.Diagnostics;
using System.Text.Json;
using VPNRouter.Core.Json;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

public sealed class VpnDnsBootstrapTests : IDisposable
{
    private const string RealityPublicKey = "gDawCMB0X6iGXZkG8nZIFW5TaaW29x0DMzWijN-gc2A";
    private const string RealityShortId = "d86e92a0c6dd2271";

    private readonly bool? _previousAwgOverride;

    public VpnDnsBootstrapTests()
    {
        _previousAwgOverride = SingBoxFeatures.OverrideAwg;
        SingBoxFeatures.OverrideAwg = true;
    }

    public void Dispose() => SingBoxFeatures.OverrideAwg = _previousAwgOverride;

    [Fact]
    public void VlessFullTunnel_VpnDnsHostnameBootstrapsViaLocalDns()
    {
        var config = ConfigGenerator.Generate(
            new Profile { Name = "vless", DnsMode = "vpn_only" },
            Array.Empty<string>(),
            VlessSettings());

        AssertVpnDnsBootstrapsViaLocalDns(config, expectedServer: "dns.google");
        Assert.Null(config.Endpoints);
    }

    [Fact]
    public void VlessFullTunnel_VpnDnsBootstrap_PassesSingBoxCheck()
    {
        var singBoxPath = FindSingBox();
        if (singBoxPath == null)
            return;

        var config = ConfigGenerator.Generate(
            new Profile { Name = "vless", DnsMode = "vpn_only" },
            Array.Empty<string>(),
            VlessSettings());

        AssertSingBoxCheckPasses(singBoxPath, config, "vless-vpn-dns-bootstrap");
    }

    [Fact]
    public void AwgFullTunnel_BlockAdsVpnDnsHostnameBootstrapsViaLocalDns()
    {
        var config = ConfigGenerator.Generate(
            new Profile { Name = "awg", DnsMode = "vpn_only" },
            Array.Empty<string>(),
            AwgSettings());

        AssertVpnDnsBootstrapsViaLocalDns(config, expectedServer: "dns.adguard-dns.com");
        Assert.NotNull(config.Endpoints);
        Assert.Equal("proxy", Assert.Single(config.Endpoints!).Tag);
    }

    [Fact]
    public void AwgFullTunnel_VpnDnsBootstrap_PassesSingBoxLxCheck()
    {
        var singBoxLxPath = FindSingBoxLx();
        if (singBoxLxPath == null)
            return;

        var config = ConfigGenerator.Generate(
            new Profile { Name = "awg", DnsMode = "vpn_only" },
            Array.Empty<string>(),
            AwgSettings());

        AssertSingBoxCheckPasses(singBoxLxPath, config, "awg-vpn-dns-bootstrap");
    }

    private static void AssertVpnDnsBootstrapsViaLocalDns(SingBoxConfig config, string expectedServer)
    {
        var vpnDns = Assert.Single(config.Dns.Servers, s => s.Tag == "vpn-dns");
        Assert.Equal("https", vpnDns.Type);
        Assert.Equal(expectedServer, vpnDns.Server);
        Assert.Equal("proxy", vpnDns.Detour);
        Assert.NotNull(vpnDns.DomainResolver);
        Assert.Equal("local-dns", vpnDns.DomainResolver!.Server);
        Assert.Null(vpnDns.DomainResolver.Strategy);
        Assert.Equal("local-dns", config.Route.DefaultDomainResolver);

        var json = JsonSerializer.Serialize(config, AppJsonContext.Default.SingBoxConfig);
        using var doc = JsonDocument.Parse(json);
        var servers = doc.RootElement.GetProperty("dns").GetProperty("servers");
        var serializedVpnDns = servers.EnumerateArray().Single(s => s.GetProperty("tag").GetString() == "vpn-dns");
        Assert.Equal("local-dns", serializedVpnDns.GetProperty("domain_resolver").GetString());
    }

    private static void AssertSingBoxCheckPasses(string singBoxPath, SingBoxConfig config, string label)
    {
        var json = ConfigGenerator.Serialize(config);
        var tempPath = Path.Combine(Path.GetTempPath(), $"vpnrouter-{label}-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(tempPath, json);
            var psi = new ProcessStartInfo
            {
                FileName = singBoxPath,
                Arguments = $"check -c \"{tempPath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi)!;
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();
            if (!proc.WaitForExit(10000))
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* best-effort */ }
                Assert.Fail($"sing-box check timed out for {label} after 10 seconds.");
            }

            var stdout = stdoutTask.GetAwaiter().GetResult();
            var stderr = stderrTask.GetAwaiter().GetResult();

            Assert.True(proc.ExitCode == 0,
                $"sing-box check failed for {label} (exit {proc.ExitCode}):\n{stdout}\n{stderr}\n\nConfig:\n{json}");
        }
        finally
        {
            try { File.Delete(tempPath); } catch { /* best-effort */ }
        }
    }

    private static string? FindSingBox()
    {
        var root = FindRepoRoot();
        var candidates = new[]
        {
            @"C:\ProgramData\VPNRouter\bin\sing-box.exe",
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "sing-box.exe")),
            root == null ? "" : Path.Combine(root, "publish", "dist", "sing-box.exe"),
            root == null ? "" : Path.Combine(root, "tools", "singbox-cache", "sing-box-1.13.14-windows-amd64", "sing-box.exe"),
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? FindSingBoxLx()
    {
        var root = FindRepoRoot();
        var candidates = new[]
        {
            root == null ? "" : Path.Combine(root, "publish", "sing-box-lx.exe"),
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "VPNRouter.sln")))
                return dir.FullName;
        }

        return null;
    }

    private static AppSettings VlessSettings() => new()
    {
        App = new AppConfig
        {
            LogLevel = "info",
            RoutingMode = "full",
        },
        Dns = new DnsSettings { VpnDns = "https://dns.google/dns-query" },
        SingBox = new SingBoxSettings(),
        Tun = new TunSettings(),
        Vless = new VlessConfig
        {
            ActiveServer = "main-vless",
            Servers = new List<VlessServerEntry>
            {
                new()
                {
                    Name = "main-vless",
                    Protocol = "vless",
                    Server = "vless.example.com",
                    Port = 443,
                    Uuid = "11111111-1111-1111-1111-111111111111",
                    Flow = "xtls-rprx-vision",
                    Security = "reality",
                    Reality = new VlessRealityConfig
                    {
                        PublicKey = RealityPublicKey,
                        ShortId = RealityShortId,
                    },
                },
            },
        },
    };

    private static AppSettings AwgSettings() => new()
    {
        App = new AppConfig
        {
            LogLevel = "info",
            RoutingMode = "full",
            BlockAds = true,
        },
        Dns = new DnsSettings { VpnDns = "https://1.1.1.1/dns-query" },
        SingBox = new SingBoxSettings(),
        Tun = new TunSettings(),
        Vless = new VlessConfig
        {
            ActiveServer = "awg",
            Servers = new List<VlessServerEntry>
            {
                new()
                {
                    Name = "awg",
                    Protocol = "amneziawg",
                    Server = "1.2.3.4",
                    Port = 51820,
                    Awg = new AwgConfig
                    {
                        PrivateKey = "XJRWW/WbfydGk7/7Kn3LLn+70XoT6se7SX9zUztOuKU=",
                        Address = new() { "10.13.13.2/32" },
                        PeerPublicKey = "iLtvwNI8UxIFHB9wNjyMud7/nofHJ5IBZaMC/knnWT0=",
                        Jc = 4,
                        Jmin = 40,
                        Jmax = 70,
                        S1 = 86,
                        S2 = 574,
                        H1 = "1234567890",
                    },
                },
            },
        },
    };
}
