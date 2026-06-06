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

    // ── UDP pairing (r5) ──────────────────────────────────────────────────────

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
        // r5: naive can't carry UDP. With a co-located HY2 sharing pair=cdn,
        // config-gen must emit proxy=naive (TCP) + proxy-udp=hysteria2 (UDP) so
        // the full-tunnel hasUdpProxy machinery routes UDP → the paired HY2.
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
            Assert.Equal("naive", proxy!.Type);          // TCP via naive
            Assert.NotNull(proxyUdp);
            Assert.Equal("hysteria2", proxyUdp!.Type);   // UDP via the paired HY2 (same node)
            Assert.Equal("213.155.15.93", proxyUdp.Server);
            // full-tunnel UDP split → proxy-udp
            Assert.Contains(config.Route.Rules, r => r.Network == "udp" && r.Outbound == "proxy-udp");
        }
        finally { ServerUriParser.NaiveRuntimeAvailable = original; }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Generate_NaivePairedSameHost_TcpGroupExcludesHy2()
    {
        // r6 #2: when naive + its paired HY2 share ONE host, GetActiveServers()
        // returns BOTH. The TCP "proxy" group must still be naive-only — never a
        // urltest that includes HY2 (which sing-box could pick for TCP, defeating
        // naive's DPI-evasion). This is the case the r5 test (different hosts) did
        // NOT cover, so it passed while the bug was live.
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
            Assert.Equal("naive", proxy!.Type);          // TCP = naive ONLY (not a urltest with HY2)
            Assert.NotNull(proxyUdp);
            Assert.Equal("hysteria2", proxyUdp!.Type);   // UDP = the paired HY2
            Assert.Contains(config.Route.Rules, r => r.Network == "udp" && r.Outbound == "proxy-udp");
        }
        finally { ServerUriParser.NaiveRuntimeAvailable = original; }
    }

    [Fact]
    public void Generate_NaivePairedWithVless_KeepsNaiveTcpOnly()
    {
        // r6 #3: a VLESS sharing the naive's pair tag must NOT be auto-selected as
        // the UDP sibling (only Hy2/TUIC qualify). Result: no proxy-udp, naive
        // stays TCP-only, and the QUIC reject rule remains (not wrongly skipped).
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
        // r6 #3: naive carries a pair tag but the pool has NO Hy2/TUIC sibling
        // (cached sub before a refresh). No proxy-udp; QUIC reject remains.
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

    // r6 #2: naive + HY2 on the SAME host (pair=cdn) — GetActiveServers() returns both.
    private static AppSettings NaivePairedSameHostSettings()
    {
        var s = NaivePairedSettings();
        s.App.Subscriptions[0].Servers[1].Server = "cdn.example.com"; // HY2 onto the naive's host
        s.App.Subscriptions[0].Servers[1].Tls = new VlessTlsConfig { Enabled = true, ServerName = "cdn.example.com", Insecure = true };
        return s;
    }

    // r6 #3: naive (active) + a VLESS sharing pair=cdn on a different host.
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

    // r6 #3: naive carries a pair tag but no sibling exists in the pool.
    private static AppSettings NaiveAloneWithTagSettings()
    {
        var s = NaivePairedSettings();
        s.App.BlockQuicOnTcpProxy = true;
        s.App.Subscriptions[0].Servers.RemoveAt(1);              // drop the HY2 sibling
        s.App.Subscriptions[0].Servers[0].PairGroup = "cdn";    // naive keeps its (now-stale) tag
        return s;
    }

    [Fact]
    public void Naive_QuicScheme_SetsNaiveQuic()
    {
        // r7 #1: naive+quic:// → HTTP/3; naive+https:// / bare → HTTP/2.
        Assert.True(ServerUriParser.Parse("naive+quic://u:p@cdn.example.com:443#Q").NaiveQuic);
        Assert.False(ServerUriParser.Parse("naive+https://u:p@cdn.example.com:443#H").NaiveQuic);
        Assert.False(ServerUriParser.Parse("naive://u:p@cdn.example.com:443#B").NaiveQuic);
    }

    [Fact]
    public void Generate_NaiveQuic_EmitsQuicTrue()
    {
        // r7 #1: a naive server parsed from naive+quic:// emits quic=true on its outbound.
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
        // r7 #5: the deep verifier must emit a naive outbound (was falling through
        // to BuildVlessOutbound → guaranteed false-fail for a valid naive server).
        var entry = new VlessServerEntry
        {
            Name = "Latvia NAIVE", Protocol = "naive", Server = "cdn.example.com", Port = 443,
            Username = "u", Password = "p", NaiveQuic = true,
            Tls = new VlessTlsConfig { Enabled = true, ServerName = "cdn.example.com" },
        };
        var json = VlessDeepVerifier.BuildSingleOutboundConfig(entry, 11111, 22222);
        Assert.Contains("\"naive\"", json);   // dispatched to BuildNaiveOutbound
        Assert.Contains("\"quic\"", json);    // HTTP/3 carried into the verify config
    }

    [Fact]
    public void Hysteria2_AllowInsecureVariants_ParseInsecureTrue()
    {
        // r7 (smaller): HY2 now accepts the same insecure spellings as TUIC.
        Assert.True(ServerUriParser.Parse("hysteria2://pw@1.2.3.4:8444/?sni=x.com&insecure=1#A").Tls!.Insecure);
        Assert.True(ServerUriParser.Parse("hysteria2://pw@1.2.3.4:8444/?sni=x.com&allowInsecure=1#B").Tls!.Insecure);
        Assert.True(ServerUriParser.Parse("hysteria2://pw@1.2.3.4:8444/?sni=x.com&allow_insecure=true#C").Tls!.Insecure);
        Assert.False(ServerUriParser.Parse("hysteria2://pw@1.2.3.4:8444/?sni=x.com#D").Tls!.Insecure);
    }

    [Fact]
    public void ParseBody_NaiveSameUserDifferentPassword_NotCollapsed()
    {
        // r7 (smaller): dedup key now includes Password, so two naive creds that
        // differ only by password survive instead of collapsing to one.
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
    public void NaivePairing_SameEntry_MatchesValueEqualClone()
    {
        // r9 follow-up #2: stable identity (not ReferenceEquals) so the TCP-group
        // exclusion survives a future path that hands back a cloned sibling.
        var a = new VlessServerEntry { Protocol = "hysteria2", Server = "1.2.3.4", Port = 8444, Password = "hp", PairGroup = "cdn" };
        var clone = new VlessServerEntry { Protocol = "hysteria2", Server = "1.2.3.4", Port = 8444, Password = "hp", PairGroup = "cdn" };
        var diffPwd = new VlessServerEntry { Protocol = "hysteria2", Server = "1.2.3.4", Port = 8444, Password = "OTHER", PairGroup = "cdn" };
        Assert.True(NaivePairing.SameEntry(a, clone));
        Assert.True(NaivePairing.SameEntry(a, a));
        Assert.False(NaivePairing.SameEntry(a, diffPwd));
        Assert.False(NaivePairing.SameEntry(a, null));
    }

    [Fact]
    public void NaivePairing_BaseNameFallback_AmbiguousReturnsNull()
    {
        // r9 follow-up #3: two same-base-name HY2 with no pair= tag → ambiguous → no pairing.
        var naive = new VlessServerEntry { Protocol = "naive", Name = "Latvia NAIVE", Server = "cdn.example.com", Port = 443 };
        var hy2a = new VlessServerEntry { Protocol = "hysteria2", Name = "Latvia HY2", Server = "a.example.com", Port = 8444 };
        var hy2b = new VlessServerEntry { Protocol = "hysteria2", Name = "Latvia HY2", Server = "b.example.com", Port = 8444 };
        Assert.Null(NaivePairing.FindUdpSibling(naive, new[] { naive, hy2a, hy2b }));
    }

    [Fact]
    public void NaivePairing_BaseNameFallback_SingleCandidatePairs()
    {
        // r9 follow-up #3: exactly one same-base-name HY2 → fallback pairing allowed.
        var naive = new VlessServerEntry { Protocol = "naive", Name = "Latvia NAIVE", Server = "cdn.example.com", Port = 443 };
        var hy2 = new VlessServerEntry { Protocol = "hysteria2", Name = "Latvia HY2", Server = "a.example.com", Port = 8444 };
        Assert.Same(hy2, NaivePairing.FindUdpSibling(naive, new[] { naive, hy2 }));
    }

    [Fact]
    public void NaivePairing_PairTag_WinsOverAmbiguousBaseName()
    {
        // r9 follow-up #3: explicit pair= stays authoritative even when base-name is ambiguous.
        var naive = new VlessServerEntry { Protocol = "naive", Name = "Latvia NAIVE", Server = "cdn.example.com", Port = 443, PairGroup = "cdn" };
        var tagged = new VlessServerEntry { Protocol = "hysteria2", Name = "Latvia HY2", Server = "a.example.com", Port = 8444, PairGroup = "cdn" };
        var untagged = new VlessServerEntry { Protocol = "hysteria2", Name = "Latvia HY2", Server = "b.example.com", Port = 8444 };
        Assert.Same(tagged, NaivePairing.FindUdpSibling(naive, new[] { naive, tagged, untagged }));
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

    // naive (active) + co-located HY2, both pair=cdn, full tunnel.
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
