using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;
// ═══════════════════════════════════════════════════════════════════════════════
// NaiveProxy support — v2.41.1
//
// sing-box's `naive` outbound (HTTP/2 CONNECT or HTTP/3 over QUIC via Chromium
// Cronet) is usable from a subscription. Coverage:
//   • ServerUriParser parses naive:// / naive+https:// / naive+quic:// into a
//     VlessServerEntry { Protocol="naive", Username, Password, Tls.ServerName }.
//   • The platform gate (ServerUriParser.NaiveRuntimeAvailable) refuses naive at
//     intake where libcronet is absent (macOS / Android) — silent drop for
//     subscriptions, clear throw for manual paste.
//   • ConfigGenerator.BuildNaiveOutbound emits the minimal outbound sing-box
//     accepts (username/password + tls{enabled,server_name}; NO insecure-true /
//     uTLS / alpn) and the macOS/Android backstop drops naive before generation.
// ═══════════════════════════════════════════════════════════════════════════════

public class NaiveProxySupportTests
{
    // ── Parser ────────────────────────────────────────────────────────────────

    [Fact]
    public void Naive_HttpsForm_ParsesCorrectly()
    {
        var e = ServerUriParser.Parse("naive+https://alice:s3cret@naive.example.com:443?sni=cdn.example.com#Home");
        Assert.Equal("naive", e.Protocol);
        Assert.Equal("naive.example.com", e.Server);
        Assert.Equal(443, e.Port);
        Assert.Equal("alice", e.Username);
        Assert.Equal("s3cret", e.Password);
        Assert.Equal("cdn.example.com", e.Tls.ServerName);
        Assert.Equal("Home", e.Name);
    }

    [Fact]
    public void Naive_QuicForm_ParsesAsNaive()
    {
        var e = ServerUriParser.Parse("naive+quic://bob:pw@h.example.org:8443#Q");
        Assert.Equal("naive", e.Protocol);
        Assert.Equal("h.example.org", e.Server);
        Assert.Equal(8443, e.Port);
        Assert.Equal("bob", e.Username);
        Assert.Equal("pw", e.Password);
    }

    [Fact]
    public void Naive_BareForm_ParsesAsNaive()
    {
        var e = ServerUriParser.Parse("naive://carol:cpw@1.2.3.4:443#bare");
        Assert.Equal("naive", e.Protocol);
        Assert.Equal("1.2.3.4", e.Server);
        Assert.Equal("carol", e.Username);
        Assert.Equal("cpw", e.Password);
    }

    [Fact]
    public void Naive_SniDefaultsToHost_WhenNoSniParam()
    {
        var e = ServerUriParser.Parse("naive+https://u:p@host.example.net:443#x");
        Assert.Equal("host.example.net", e.Tls.ServerName);
    }

    [Fact]
    public void Naive_PasswordlessUserinfo_Tolerated()
    {
        var e = ServerUriParser.Parse("naive+https://justuser@host.example:443#nopass");
        Assert.Equal("justuser", e.Username);
        Assert.Equal(string.Empty, e.Password);
    }

    [Fact]
    public void Naive_DefaultsPort443_WhenOmitted()
    {
        var e = ServerUriParser.Parse("naive+https://u:p@host.example#noport");
        Assert.Equal(443, e.Port);
    }

    // ── Platform gate ───────────────────────────────────────────────────────────

    [Fact]
    public void Naive_WhenRuntimeUnavailable_IsSupportedSchemeFalse_DroppedFromSubscription()
    {
        var original = ServerUriParser.NaiveRuntimeAvailable;
        try
        {
            ServerUriParser.NaiveRuntimeAvailable = false; // simulate macOS / Android
            Assert.False(ServerUriParser.IsSupportedScheme("naive+https://u:p@h:443#x"));

            // A mixed subscription blob: the naive line is silently dropped, the
            // VLESS line survives (ParseMultiple pre-filters via IsSupportedScheme).
            var blob = "naive+https://u:p@h.example:443#drop\n" +
                       "vless://uuid@1.2.3.4:443?security=reality&pbk=PUB&sid=ID&flow=xtls-rprx-vision#keep";
            var parsed = ServerUriParser.ParseMultiple(blob);
            Assert.Single(parsed);
            Assert.Equal("vless", parsed[0].Protocol);
        }
        finally { ServerUriParser.NaiveRuntimeAvailable = original; }
    }

    [Fact]
    public void Naive_WhenRuntimeUnavailable_ManualParseThrows()
    {
        var original = ServerUriParser.NaiveRuntimeAvailable;
        try
        {
            ServerUriParser.NaiveRuntimeAvailable = false;
            var ex = Assert.Throws<FormatException>(
                () => ServerUriParser.Parse("naive+https://u:p@h.example:443#x"));
            Assert.Contains("Windows and Linux", ex.Message);
        }
        finally { ServerUriParser.NaiveRuntimeAvailable = original; }
    }

    [Fact]
    public void Naive_WhenRuntimeAvailable_IsSupportedSchemeTrue()
    {
        var original = ServerUriParser.NaiveRuntimeAvailable;
        try
        {
            ServerUriParser.NaiveRuntimeAvailable = true; // simulate Windows / Linux
            Assert.True(ServerUriParser.IsSupportedScheme("naive+https://u:p@h:443#x"));
            Assert.True(ServerUriParser.IsSupportedScheme("naive+quic://u:p@h:443#x"));
            Assert.True(ServerUriParser.IsSupportedScheme("naive://u:p@h:443#x"));
        }
        finally { ServerUriParser.NaiveRuntimeAvailable = original; }
    }

    // ── ConfigGenerator ─────────────────────────────────────────────────────────

    [Fact]
    public void Generate_NaiveServer_ProducesMinimalNaiveOutbound()
    {
        var original = ServerUriParser.NaiveRuntimeAvailable;
        try
        {
            ServerUriParser.NaiveRuntimeAvailable = true; // ensure not filtered
            var settings = NaiveSettings();
            Assert.Single(VlessServersResolver.Resolve(settings)); // subscribe → aggregate naive into Vless.Servers
            var config = ConfigGenerator.Generate(NaiveProfile(), new[] { "Discord.exe" }, settings);

            var proxy = config.Outbounds.FirstOrDefault(o => o.Tag == "proxy");
            Assert.NotNull(proxy);
            Assert.Equal("naive", proxy!.Type);
            Assert.Equal("naive.example.com", proxy.Server);
            Assert.Equal(443, proxy.ServerPort);
            Assert.Equal("user1", proxy.Username);   // survives Resolve (by-reference)
            Assert.Equal("pass1", proxy.Password);
            Assert.NotNull(proxy.Tls);
            Assert.True(proxy.Tls!.Enabled);
            Assert.Equal("naive.example.com", proxy.Tls.ServerName);
            // naive rejects these at outbound init — they must be omitted.
            Assert.Null(proxy.Tls.Reality);
            Assert.Null(proxy.Tls.Utls);
            Assert.Null(proxy.Tls.Alpn);
        }
        finally { ServerUriParser.NaiveRuntimeAvailable = original; }
    }

    [Fact]
    public void Generate_NaiveServer_OnUnsupportedPlatform_DroppedByBackstop()
    {
        var original = ServerUriParser.NaiveRuntimeAvailable;
        try
        {
            var settings = NaiveSettings();
            // Resolve aggregates the naive server regardless of platform...
            Assert.Single(VlessServersResolver.Resolve(settings));
            ServerUriParser.NaiveRuntimeAvailable = false; // ...but on macOS / Android
            // the backstop filters it before generation → empty pool → the v2.28.2
            // hard guard fires (fail-closed, no FATAL sing-box config).
            var ex = Assert.Throws<InvalidOperationException>(
                () => ConfigGenerator.Generate(NaiveProfile(), new[] { "Discord.exe" }, settings));
            Assert.Contains("no active VLESS servers", ex.Message);
        }
        finally { ServerUriParser.NaiveRuntimeAvailable = original; }
    }

    [Fact]
    public void Generate_NaiveServer_PassesSingBoxCheck()
    {
        var singBox = FindSingBoxWithCronet();
        if (singBox == null)
            return; // no sing-box + libcronet pair available — skip (CI / pre-2.41.1 install)

        var original = ServerUriParser.NaiveRuntimeAvailable;
        try
        {
            ServerUriParser.NaiveRuntimeAvailable = true;
            var settings = NaiveSettings();
            VlessServersResolver.Resolve(settings);
            var config = ConfigGenerator.Generate(NaiveProfile(), new[] { "Discord.exe" }, settings);
            var json = ConfigGenerator.Serialize(config);

            var tempPath = Path.Combine(Path.GetTempPath(), $"vpnrouter-naive-{Guid.NewGuid()}.json");
            try
            {
                File.WriteAllText(tempPath, json);
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = singBox,
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
                    $"sing-box check failed on generated naive config (exit {proc.ExitCode}):\n{stderr}\n\nConfig:\n{json}");
            }
            finally { if (File.Exists(tempPath)) File.Delete(tempPath); }
        }
        finally { ServerUriParser.NaiveRuntimeAvailable = original; }
    }

    [Fact]
    public void Generate_NaiveServer_PassesDeadConfigGuard()
    {
        // Regression for v2.41.1-r1 (brat Win, cdn.ninitux.top): the F-E
        // pre-start dead-config guard's proxy-outbound allowlist
        // (PlaceholderDefense.FindFirstProxyOutbound) omitted "naive", so a
        // valid naive config was flagged "no proxy outbound found → dead",
        // AutoFailover bounced naive → VLESS, settings reverted to naive, and
        // the reconnect retried forever — surfacing as an "infinite process
        // scan" (sing-box never even started with naive).
        var original = ServerUriParser.NaiveRuntimeAvailable;
        try
        {
            ServerUriParser.NaiveRuntimeAvailable = true;
            var settings = NaiveSettings();
            VlessServersResolver.Resolve(settings);
            var config = ConfigGenerator.Generate(NaiveProfile(), new[] { "Discord.exe" }, settings);
            var json = ConfigGenerator.Serialize(config);
            var node = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();

            var result = new ConfigSanityCheck().CheckBeforeStart(node);
            Assert.False(result.IsDead,
                $"naive config wrongly flagged dead by F-E guard: {result.Reason}");
        }
        finally { ServerUriParser.NaiveRuntimeAvailable = original; }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static Profile NaiveProfile() => new()
    {
        Name = "T",
        DnsMode = "vpn_only",
        Processes = new() { new ProcessRule { Name = "Discord.exe", ScanPatterns = new[] { "Discord.exe" } } }
    };

    private static AppSettings NaiveSettings() => new()
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
                    Name = "naive-sub",
                    Url = "https://example.com",
                    Enabled = true,
                    Servers = new List<VlessServerEntry>
                    {
                        new()
                        {
                            Name = "main",
                            Protocol = "naive",
                            Server = "naive.example.com",
                            Port = 443,
                            Username = "user1",
                            Password = "pass1",
                            Tls = new VlessTlsConfig { Enabled = true, ServerName = "naive.example.com" }
                        }
                    }
                }
            }
        },
        Tun = new TunSettings { InterfaceName = "VPNRouter-TUN", Ipv4Address = "172.19.0.1/30", Mtu = 9000, AutoRoute = true, StrictRoute = false },
        Dns = new DnsSettings { VpnDns = "https://1.1.1.1/dns-query", Strategy = "ipv4_only" },
        SingBox = new SingBoxSettings { ClashApi = "127.0.0.1:9090" },
        Vless = new VlessConfig()
    };

    /// <summary>
    /// Locate a sing-box binary that has libcronet beside it (naive's `check`
    /// FATALs without it). Tries the installed ProgramData bin first, then walks
    /// up to the repo's tools/singbox-cache. Returns null → the integration test
    /// skips (CI without the binary, or a pre-2.41.1 install missing libcronet).
    /// </summary>
    private static string? FindSingBoxWithCronet()
    {
        var prog = @"C:\ProgramData\VPNRouter\bin\sing-box.exe";
        if (File.Exists(prog) && File.Exists(Path.Combine(Path.GetDirectoryName(prog)!, "libcronet.dll")))
            return prog;

        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && !string.IsNullOrEmpty(dir); i++)
        {
            var cache = Path.Combine(dir, "tools", "singbox-cache");
            if (Directory.Exists(cache))
            {
                foreach (var sb in Directory.GetFiles(cache, "sing-box.exe", SearchOption.AllDirectories))
                    if (File.Exists(Path.Combine(Path.GetDirectoryName(sb)!, "libcronet.dll")))
                        return sb;
            }
            dir = Path.GetDirectoryName(dir.TrimEnd('\\', '/'));
        }
        return null;
    }
}
