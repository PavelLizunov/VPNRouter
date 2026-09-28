using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

public class NaiveProxySupportTests
{
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

    [Fact]
    public void Naive_WhenRuntimeUnavailable_IsSupportedSchemeFalse_DroppedFromSubscription()
    {
        var original = ServerUriParser.NaiveRuntimeAvailable;
        try
        {
            ServerUriParser.NaiveRuntimeAvailable = false;
            Assert.False(ServerUriParser.IsSupportedScheme("naive+https://u:p@h:443#x"));

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
            ServerUriParser.NaiveRuntimeAvailable = true;
            Assert.True(ServerUriParser.IsSupportedScheme("naive+https://u:p@h:443#x"));
            Assert.True(ServerUriParser.IsSupportedScheme("naive+quic://u:p@h:443#x"));
            Assert.True(ServerUriParser.IsSupportedScheme("naive://u:p@h:443#x"));
        }
        finally { ServerUriParser.NaiveRuntimeAvailable = original; }
    }

    [Fact]
    public void Generate_NaiveServer_ProducesMinimalNaiveOutbound()
    {
        var original = ServerUriParser.NaiveRuntimeAvailable;
        try
        {
            ServerUriParser.NaiveRuntimeAvailable = true;
            var settings = NaiveSettings();
            Assert.Single(VlessServersResolver.Resolve(settings));
            var config = ConfigGenerator.Generate(NaiveProfile(), new[] { "Discord.exe" }, settings);

            var proxy = config.Outbounds.FirstOrDefault(o => o.Tag == "proxy");
            Assert.NotNull(proxy);
            Assert.Equal("naive", proxy!.Type);
            Assert.Equal("naive.example.com", proxy.Server);
            Assert.Equal(443, proxy.ServerPort);
            Assert.Equal("user1", proxy.Username);
            Assert.Equal("pass1", proxy.Password);
            Assert.NotNull(proxy.Tls);
            Assert.True(proxy.Tls!.Enabled);
            Assert.Equal("naive.example.com", proxy.Tls.ServerName);
            Assert.Null(proxy.Tls.Reality);
            Assert.Null(proxy.Tls.Utls);
            Assert.Null(proxy.Tls.Alpn);
            Assert.NotNull(proxy.DomainResolver);
            Assert.Equal("local-dns", proxy.DomainResolver!.Server);
            Assert.Equal("prefer_ipv4", proxy.DomainResolver.Strategy);
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
            Assert.Single(VlessServersResolver.Resolve(settings));
            ServerUriParser.NaiveRuntimeAvailable = false;
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
            return;

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

    [Fact]
    public void Naive_PairTag_ParsedIntoPairGroup()
    {
        var e = ServerUriParser.Parse("naive+https://u:p@cdn.example.com:443?pair=cdn#Latvia NAIVE");
        Assert.Equal("naive", e.Protocol);
        Assert.Equal("cdn", e.PairGroup);
    }

    [Fact]
    public void Hysteria2_PairTag_ParsedIntoPairGroup()
    {
        var e = ServerUriParser.Parse("hysteria2://pw@1.2.3.4:8444/?sni=x.com&pair=cdn#Latvia HY2");
        Assert.Equal("hysteria2", e.Protocol);
        Assert.Equal("cdn", e.PairGroup);
    }

    [Fact]
    public void Generate_NaiveWithPairedHy2_RoutesUdpThroughHy2()
    {
        var original = ServerUriParser.NaiveRuntimeAvailable;
        try
        {
            ServerUriParser.NaiveRuntimeAvailable = true;
            var settings = NaivePairedSettings();
            Assert.Equal(2, VlessServersResolver.Resolve(settings).Count);
            var config = ConfigGenerator.Generate(NaiveProfile(), System.Array.Empty<string>(), settings);

            var proxy = config.Outbounds.FirstOrDefault(o => o.Tag == "proxy");
            var proxyUdp = config.Outbounds.FirstOrDefault(o => o.Tag == "proxy-udp");
            Assert.NotNull(proxy);
            Assert.Equal("naive", proxy!.Type);
            Assert.NotNull(proxyUdp);
            Assert.Equal("hysteria2", proxyUdp!.Type);
            Assert.Equal("213.155.15.93", proxyUdp.Server);
            Assert.Contains(config.Route.Rules, r => r.Network == "udp" && r.Outbound == "proxy-udp");
        }
        finally { ServerUriParser.NaiveRuntimeAvailable = original; }
    }

    [Fact]
    public void Generate_NaivePairedSameHost_TcpGroupExcludesHy2()
    {
        var original = ServerUriParser.NaiveRuntimeAvailable;
        try
        {
            ServerUriParser.NaiveRuntimeAvailable = true;
            var settings = NaivePairedSameHostSettings();
            Assert.Equal(2, VlessServersResolver.Resolve(settings).Count);
            var config = ConfigGenerator.Generate(NaiveProfile(), System.Array.Empty<string>(), settings);

            var proxy = config.Outbounds.FirstOrDefault(o => o.Tag == "proxy");
            var proxyUdp = config.Outbounds.FirstOrDefault(o => o.Tag == "proxy-udp");
            Assert.NotNull(proxy);
            Assert.Equal("naive", proxy!.Type);
            Assert.NotNull(proxyUdp);
            Assert.Equal("hysteria2", proxyUdp!.Type);
            Assert.Contains(config.Route.Rules, r => r.Network == "udp" && r.Outbound == "proxy-udp");
        }
        finally { ServerUriParser.NaiveRuntimeAvailable = original; }
    }

    [Fact]
    public void Generate_NaivePairedWithVless_KeepsNaiveTcpOnly()
    {
        var original = ServerUriParser.NaiveRuntimeAvailable;
        try
        {
            ServerUriParser.NaiveRuntimeAvailable = true;
            var settings = NaivePairedWithVlessSettings();
            VlessServersResolver.Resolve(settings);
            var config = ConfigGenerator.Generate(NaiveProfile(), System.Array.Empty<string>(), settings);

            var proxy = config.Outbounds.FirstOrDefault(o => o.Tag == "proxy");
            Assert.NotNull(proxy);
            Assert.Equal("naive", proxy!.Type);
            Assert.DoesNotContain(config.Outbounds, o => o.Tag == "proxy-udp");
            Assert.Contains(config.Route.Rules, r => r.Protocol == "quic" && r.Action == "reject");
        }
        finally { ServerUriParser.NaiveRuntimeAvailable = original; }
    }

    [Fact]
    public void Generate_NaiveStaleTagNoSibling_KeepsNaiveTcpOnly()
    {
        var original = ServerUriParser.NaiveRuntimeAvailable;
        try
        {
            ServerUriParser.NaiveRuntimeAvailable = true;
            var settings = NaiveAloneWithTagSettings();
            VlessServersResolver.Resolve(settings);
            var config = ConfigGenerator.Generate(NaiveProfile(), System.Array.Empty<string>(), settings);

            Assert.DoesNotContain(config.Outbounds, o => o.Tag == "proxy-udp");
            Assert.Contains(config.Route.Rules, r => r.Protocol == "quic" && r.Action == "reject");
        }
        finally { ServerUriParser.NaiveRuntimeAvailable = original; }
    }

    private static AppSettings NaivePairedSameHostSettings()
    {
        var s = NaivePairedSettings();
        s.App.Subscriptions[0].Servers[1].Server = "cdn.example.com";
        s.App.Subscriptions[0].Servers[1].Tls = new VlessTlsConfig { Enabled = true, ServerName = "cdn.example.com", Insecure = true };
        return s;
    }

    private static AppSettings NaivePairedWithVlessSettings()
    {
        var s = NaivePairedSettings();
        s.App.BlockQuicOnTcpProxy = true;
        var sib = s.App.Subscriptions[0].Servers[1];
        sib.Name = "Latvia VLESS";
        sib.Protocol = "vless";
        sib.Server = "9.9.9.9";
        sib.Flow = "xtls-rprx-vision";
        sib.PairGroup = "cdn";
        return s;
    }

    private static AppSettings NaiveAloneWithTagSettings()
    {
        var s = NaivePairedSettings();
        s.App.BlockQuicOnTcpProxy = true;
        s.App.Subscriptions[0].Servers.RemoveAt(1);
        s.App.Subscriptions[0].Servers[0].PairGroup = "cdn";
        return s;
    }

    [Fact]
    public void Naive_QuicScheme_SetsNaiveQuic()
    {
        Assert.True(ServerUriParser.Parse("naive+quic://u:p@cdn.example.com:443#Q").NaiveQuic);
        Assert.False(ServerUriParser.Parse("naive+https://u:p@cdn.example.com:443#H").NaiveQuic);
        Assert.False(ServerUriParser.Parse("naive://u:p@cdn.example.com:443#B").NaiveQuic);
    }

    [Fact]
    public void Generate_NaiveQuic_EmitsQuicTrue()
    {
        var original = ServerUriParser.NaiveRuntimeAvailable;
        try
        {
            ServerUriParser.NaiveRuntimeAvailable = true;
            var settings = NaiveSettings();
            settings.App.Subscriptions[0].Servers[0].NaiveQuic = true;
            VlessServersResolver.Resolve(settings);
            var config = ConfigGenerator.Generate(NaiveProfile(), System.Array.Empty<string>(), settings);
            var proxy = config.Outbounds.FirstOrDefault(o => o.Tag == "proxy");
            Assert.NotNull(proxy);
            Assert.Equal("naive", proxy!.Type);
            Assert.True(proxy.Quic);
        }
        finally { ServerUriParser.NaiveRuntimeAvailable = original; }
    }

    [Fact]
    public void DeepVerify_NaiveEntry_BuildsNaiveOutbound_NotVless()
    {
        var entry = new VlessServerEntry
        {
            Name = "Latvia NAIVE", Protocol = "naive", Server = "cdn.example.com", Port = 443,
            Username = "u", Password = "p", NaiveQuic = true,
            Tls = new VlessTlsConfig { Enabled = true, ServerName = "cdn.example.com" },
        };
        var json = VlessDeepVerifier.BuildSingleOutboundConfig(entry, 11111, 22222);
        Assert.Contains("\"naive\"", json);
        Assert.Contains("\"quic\"", json);
    }

    [Fact]
    public void Hysteria2_AllowInsecureVariants_ParseInsecureTrue()
    {
        Assert.True(ServerUriParser.Parse("hysteria2://pw@1.2.3.4:8444/?sni=x.com&insecure=1#A").Tls!.Insecure);
        Assert.True(ServerUriParser.Parse("hysteria2://pw@1.2.3.4:8444/?sni=x.com&allowInsecure=1#B").Tls!.Insecure);
        Assert.True(ServerUriParser.Parse("hysteria2://pw@1.2.3.4:8444/?sni=x.com&allow_insecure=true#C").Tls!.Insecure);
        Assert.False(ServerUriParser.Parse("hysteria2://pw@1.2.3.4:8444/?sni=x.com#D").Tls!.Insecure);
    }

    [Fact]
    public void ParseBody_NaiveSameUserDifferentPassword_NotCollapsed()
    {
        var original = ServerUriParser.NaiveRuntimeAvailable;
        try
        {
            ServerUriParser.NaiveRuntimeAvailable = true;
            var body = "naive+https://u:p1@h.example.com:443#A\nnaive+https://u:p2@h.example.com:443#B\n";
            var list = SubscriptionFetcher.ParseBody(body, out _, null);
            Assert.Equal(2, list.Count);
        }
        finally { ServerUriParser.NaiveRuntimeAvailable = original; }
    }

    [Fact]
    public void Generate_NaiveSameHostWithExtraVless_TcpGroupIsNaiveOnly()
    {
        var original = ServerUriParser.NaiveRuntimeAvailable;
        try
        {
            ServerUriParser.NaiveRuntimeAvailable = true;
            var settings = NaiveSameHostTripleSettings();
            VlessServersResolver.Resolve(settings);
            var config = ConfigGenerator.Generate(NaiveProfile(), System.Array.Empty<string>(), settings);
            var proxy = config.Outbounds.FirstOrDefault(o => o.Tag == "proxy");
            var proxyUdp = config.Outbounds.FirstOrDefault(o => o.Tag == "proxy-udp");
            Assert.NotNull(proxy);
            Assert.Equal("naive", proxy!.Type);
            Assert.NotNull(proxyUdp);
            Assert.Equal("hysteria2", proxyUdp!.Type);
        }
        finally { ServerUriParser.NaiveRuntimeAvailable = original; }
    }

    [Fact]
    public void Generate_Hy2AliasProtocol_BuildsHysteria2Outbound()
    {
        var settings = NaiveSettings();
        var s = settings.App.Subscriptions[0].Servers[0];
        s.Protocol = "hy2";
        s.Server = "1.2.3.4";
        s.Port = 8444;
        s.Password = "hp";
        s.Tls = new VlessTlsConfig { Enabled = true, ServerName = "1.2.3.4", Insecure = true };
        VlessServersResolver.Resolve(settings);
        var config = ConfigGenerator.Generate(NaiveProfile(), System.Array.Empty<string>(), settings);
        var proxy = config.Outbounds.FirstOrDefault(o => o.Tag == "proxy");
        Assert.NotNull(proxy);
        Assert.Equal("hysteria2", proxy!.Type);
    }

    [Fact]
    public void NaivePairing_BaseNameFallback_AmbiguousReturnsNull()
    {
        var naive = new VlessServerEntry { Protocol = "naive", Name = "Latvia NAIVE", Server = "cdn.example.com", Port = 443 };
        var hy2a = new VlessServerEntry { Protocol = "hysteria2", Name = "Latvia HY2", Server = "a.example.com", Port = 8444 };
        var hy2b = new VlessServerEntry { Protocol = "hysteria2", Name = "Latvia HY2", Server = "b.example.com", Port = 8444 };
        Assert.Null(NaivePairing.FindUdpSibling(naive, new[] { naive, hy2a, hy2b }));
    }

    [Fact]
    public void NaivePairing_BaseNameFallback_SingleCandidatePairs()
    {
        var naive = new VlessServerEntry { Protocol = "naive", Name = "Latvia NAIVE", Server = "cdn.example.com", Port = 443 };
        var hy2 = new VlessServerEntry { Protocol = "hysteria2", Name = "Latvia HY2", Server = "a.example.com", Port = 8444 };
        Assert.Same(hy2, NaivePairing.FindUdpSibling(naive, new[] { naive, hy2 }));
    }

    [Fact]
    public void NaivePairing_PairTag_WinsOverAmbiguousBaseName()
    {
        var naive = new VlessServerEntry { Protocol = "naive", Name = "Latvia NAIVE", Server = "cdn.example.com", Port = 443, PairGroup = "cdn" };
        var tagged = new VlessServerEntry { Protocol = "hysteria2", Name = "Latvia HY2", Server = "a.example.com", Port = 8444, PairGroup = "cdn" };
        var untagged = new VlessServerEntry { Protocol = "hysteria2", Name = "Latvia HY2", Server = "b.example.com", Port = 8444 };
        Assert.Same(tagged, NaivePairing.FindUdpSibling(naive, new[] { naive, tagged, untagged }));
    }

    private static AppSettings NaiveSameHostTripleSettings()
    {
        var s = NaivePairedSameHostSettings();
        s.App.Subscriptions[0].Servers.Add(new VlessServerEntry
        {
            Name = "Latvia VLESS", Protocol = "vless", Server = "cdn.example.com", Port = 8443,
            Uuid = "u", Flow = "xtls-rprx-vision", Security = "reality",
            Reality = new VlessRealityConfig { Enabled = true, PublicKey = "k", ServerName = "www.microsoft.com" },
        });
        return s;
    }

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

    private static AppSettings NaivePairedSettings() => new()
    {
        App = new AppConfig
        {
            LogLevel = "info",
            ConfigMode = "subscribe",
            RoutingMode = "full",
            ActiveSubscriptionServer = "Latvia NAIVE",
            Subscriptions = new List<SubscriptionEntry>
            {
                new()
                {
                    Name = "paired-sub",
                    Url = "https://example.com",
                    Enabled = true,
                    Servers = new List<VlessServerEntry>
                    {
                        new()
                        {
                            Name = "Latvia NAIVE", Protocol = "naive", Server = "cdn.example.com", Port = 443,
                            Username = "u", Password = "p", PairGroup = "cdn",
                            Tls = new VlessTlsConfig { Enabled = true, ServerName = "cdn.example.com" }
                        },
                        new()
                        {
                            Name = "Latvia HY2", Protocol = "hysteria2", Server = "213.155.15.93", Port = 8444,
                            Password = "hp", PairGroup = "cdn",
                            Tls = new VlessTlsConfig { Enabled = true, ServerName = "213.155.15.93", Insecure = true }
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
