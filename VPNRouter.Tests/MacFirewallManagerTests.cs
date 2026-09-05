using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Tasks;
using VPNRouter.Core.Interfaces;
using VPNRouter.Core.Platform.macOS;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// Wire-shape + safety coverage for the macOS pf kill-switch (r6 design +
/// P0.3 anchor migration 2026-07-10). MacFirewallManager is pure IProcessRunner
/// orchestration, so the exact pfctl command shapes — the part where a wrong
/// token / missing carrier / missing flush bricks the user's network OR ships a
/// silently-dead kill-switch — are pinned here on the Windows build.
///
/// <para><b>The P0.3 carrier invariant (D2):</b> pf evaluates anchor rules ONLY
/// when the main ruleset contains an <c>anchor "com.vpnrouter/killswitch"</c>
/// call. Stock macOS references only <c>com.apple/*</c>, so loading rules into
/// the anchor WITHOUT ensuring the carrier is an inert no-op — proven live on
/// the Mac host 2026-07-10 (curl to a public IP kept working with the anchor
/// loaded and no carrier). Any refactor that drops the carrier-ensure step
/// reintroduces a dead kill-switch that command-shape tests alone would miss —
/// which is why D2 pins the carrier CONTENT, not just the pfctl invocation.</para>
///
/// <para>The live block / reconnect / no-brick behaviour is verified on the Mac
/// host via the kill-9 SSH gate
/// (plans/macos-p0.3-pf-anchor-corrected-design-2026-07-10.md).</para>
/// </summary>
public class MacFirewallManagerTests : IDisposable
{
    private const string Anchor = "com.vpnrouter/killswitch";

    private readonly string _cfg =
        Path.Combine(Path.GetTempPath(), "vpnrouter-fw-cfg-" + Guid.NewGuid().ToString("N") + ".json");
    private readonly string _marker =
        Path.Combine(Path.GetTempPath(), "vpnrouter-fw-marker-" + Guid.NewGuid().ToString("N") + ".marker");
    private readonly string _pfconf =
        Path.Combine(Path.GetTempPath(), "vpnrouter-fw-pfconf-" + Guid.NewGuid().ToString("N") + ".conf");
    private readonly string _rules =
        Path.Combine(Path.GetTempPath(), "vpnrouter-fw-rules-" + Guid.NewGuid().ToString("N") + ".conf");
    private readonly string _mainConf =
        Path.Combine(Path.GetTempPath(), "vpnrouter-fw-main-" + Guid.NewGuid().ToString("N") + ".conf");

    private const string StockPfConf =
        "scrub-anchor \"com.apple/*\"\n" +
        "nat-anchor \"com.apple/*\"\n" +
        "rdr-anchor \"com.apple/*\"\n" +
        "dummynet-anchor \"com.apple/*\"\n" +
        "anchor \"com.apple/*\"\n" +
        "load anchor \"com.apple\" from \"/etc/pf.anchors/com.apple\"\n";

    public MacFirewallManagerTests() => File.WriteAllText(_pfconf, StockPfConf);

    public void Dispose()
    {
        try { if (File.Exists(_cfg)) File.Delete(_cfg); } catch { }
        try { if (File.Exists(_marker)) File.Delete(_marker); } catch { }
        try { if (File.Exists(_pfconf)) File.Delete(_pfconf); } catch { }
        try { if (File.Exists(_rules)) File.Delete(_rules); } catch { }
        try { if (File.Exists(_mainConf)) File.Delete(_mainConf); } catch { }
    }

    private static ProcessResult Ok(string stdout = "", string stderr = "") =>
        new ProcessResult(0, stdout, stderr, TimeSpan.Zero, false);
    private static ProcessResult Fail(string stderr = "pfctl: permission denied") =>
        new ProcessResult(1, "", stderr, TimeSpan.Zero, false);

    private void WriteConfig(string serverIp) =>
        File.WriteAllText(_cfg, $@"{{ ""outbounds"": [
            {{ ""type"": ""vless"", ""tag"": ""proxy"", ""server"": ""{serverIp}"" }},
            {{ ""type"": ""direct"", ""tag"": ""direct"" }} ] }}");

    /// <summary>sudo -E returns a token; -sr shows a STOCK main ruleset (no
    /// vpnrouter carrier — the fresh-boot case); everything else ok.</summary>
    private static FakeProcessRunner OkRunner(bool carrierPresent = false)
    {
        var f = new FakeProcessRunner();
        f.OnRun(r => r.ExecutablePath == "/usr/bin/sudo" && r.Arguments.Contains("-E"),
            Ok(stderr: "Token : 12345678"));
        f.OnRun(r => r.ExecutablePath == "/usr/bin/sudo" && r.Arguments.Contains("-sr"),
            Ok(stdout: carrierPresent
                ? "anchor \"com.apple/*\" all\nanchor \"" + Anchor + "\" all\n"
                : "scrub-anchor \"com.apple/*\" all fragment reassemble\nanchor \"com.apple/*\" all\n"));
        f.OnRun(r => r.ExecutablePath == "/usr/bin/sudo", Ok());
        return f;
    }

    private MacFirewallManager Sut(FakeProcessRunner fake, string? pfconf = null, string? rulesPath = null, string? mainConfPath = null, Func<string, IReadOnlyList<string>>? hostResolver = null) =>
        new MacFirewallManager(null, fake, _cfg, _marker, hostResolver: hostResolver, pfConfPath: pfconf ?? _pfconf, rulesPath: rulesPath ?? _rules, mainConfPath: mainConfPath ?? _mainConf);

    private static bool GetArmed(MacFirewallManager sut) =>
        (bool)typeof(MacFirewallManager).GetField("_armed", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(sut)!;

    private static bool GetLoaded(MacFirewallManager sut) =>
        (bool)typeof(MacFirewallManager).GetField("_loaded", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(sut)!;

    // Helpers to classify pfctl calls.
    private static bool IsAnchorLoad(ProcessRequest c) =>
        c.Arguments.Contains("-a") && c.Arguments.Contains(Anchor) && c.Arguments.Contains("-f");
    private static bool IsAnchorFlush(ProcessRequest c) =>
        c.Arguments.Contains("-a") && c.Arguments.Contains(Anchor) && c.Arguments.Contains("-F");
    private static bool IsMainLoad(ProcessRequest c) =>
        !c.Arguments.Contains("-a") && c.Arguments.Contains("-f");

    // ── D1: split tunnel stays a no-op ──────────────────────────────────────

    [Fact]
    public void SplitTunnel_disarms_and_Enable_is_noop()
    {
        WriteConfig("9.9.9.9");
        var fake = OkRunner();
        var sut = Sut(fake);

        sut.CreateBlockRules(new[] { "Discord", "chrome" }, isFullTunnel: false);
        sut.EnableBlockRules();

        Assert.Empty(fake.RunCalls); // never armed → no pfctl at all (full-tunnel-only)
    }

    // P1 regression (2026-07-10): a SPLIT-tunnel process scan that TIMED OUT
    // returns an empty list. Pre-fix an empty list meant "full tunnel" → the
    // whole host's egress was dropped on a crash. Arming is now by the explicit
    // routing intent, so split-with-empty-list must STILL disarm.
    [Fact]
    public void SplitTunnel_emptyList_scanTimeout_still_disarms()
    {
        WriteConfig("9.9.9.9");
        var fake = OkRunner();
        var sut = Sut(fake);

        sut.CreateBlockRules(Array.Empty<string>(), isFullTunnel: false); // split scan returned nothing
        sut.EnableBlockRules();

        Assert.Empty(fake.RunCalls); // must NOT global-block a split-tunnel user
    }

    // ── D2: full tunnel loads the ANCHOR + ensures the CARRIER ─────────────

    [Fact]
    public void FullTunnel_Enable_loads_anchor_and_carrier_not_bare_ruleset()
    {
        WriteConfig("104.194.156.93");
        var fake = OkRunner();
        var sut = Sut(fake);

        sut.CreateBlockRules(Array.Empty<string>());
        sut.EnableBlockRules();

        // pf enable token captured.
        Assert.Contains(fake.RunCalls, c => c.ExecutablePath == "/usr/bin/sudo" && c.Arguments.Contains("-E"));

        // The block rules are loaded INTO the anchor, not as the main ruleset.
        var anchorLoad = fake.RunCalls.FirstOrDefault(IsAnchorLoad);
        Assert.NotNull(anchorLoad);
        var rules = File.ReadAllText(anchorLoad!.Arguments.Last());
        Assert.Contains("block drop out all", rules);
        Assert.Contains("104.194.156.93", rules); // server pass → sing-box can reconnect
        Assert.DoesNotContain("set block-policy", rules); // `set` is main-ruleset-only → would fail the anchor load

        // THE P0.3 INVARIANT: the main ruleset was reloaded WITH the carrier —
        // pf.conf content preserved + our anchor call appended. Without this the
        // anchor rules above are inert (dead kill-switch, proven live).
        var mainLoad = fake.RunCalls.FirstOrDefault(IsMainLoad);
        Assert.NotNull(mainLoad);
        var merged = File.ReadAllText(mainLoad!.Arguments.Last());
        Assert.Contains($"anchor \"{Anchor}\"", merged);          // carrier present
        Assert.Contains("anchor \"com.apple/*\"", merged);        // Apple defaults preserved
        Assert.NotEqual("/etc/pf.conf", mainLoad.Arguments.Last()); // not a bare stock restore
    }

    [Fact]
    public void SecondEnable_with_carrier_already_present_touches_only_the_anchor()
    {
        WriteConfig("9.9.9.9");
        var fake = OkRunner(carrierPresent: true); // e.g. re-engage after a Disable this boot
        var sut = Sut(fake);

        sut.CreateBlockRules(Array.Empty<string>());
        sut.EnableBlockRules();

        Assert.Contains(fake.RunCalls, IsAnchorLoad);
        // No main-ruleset reload at all: repeat engages have anchor-only blast radius.
        Assert.DoesNotContain(fake.RunCalls, IsMainLoad);
    }

    // ── D3: disable flushes the anchor, never reloads /etc/pf.conf ─────────

    [Fact]
    public void Disable_after_load_flushes_anchor_and_releases_token_no_pfconf_reload()
    {
        WriteConfig("9.9.9.9");
        var fake = OkRunner();
        var sut = Sut(fake);
        sut.CreateBlockRules(Array.Empty<string>());
        sut.EnableBlockRules();

        sut.DisableBlockRules();

        Assert.Contains(fake.RunCalls, IsAnchorFlush);
        Assert.Contains(fake.RunCalls, c => c.Arguments.Contains("-X") && c.Arguments.Contains("12345678"));
        // Normal anchor-mode disable must NOT touch the stock ruleset — that
        // broad restore is what used to wipe other tools' runtime pf state.
        Assert.DoesNotContain(fake.RunCalls, c => c.Arguments.Contains("/etc/pf.conf"));
    }

    // ── D4: DeleteAllRules flushes the anchor + releases the token ─────────

    [Fact]
    public void DeleteAllRules_flushes_anchor_and_releases_token()
    {
        WriteConfig("9.9.9.9");
        var fake = OkRunner();
        var sut = Sut(fake);
        sut.CreateBlockRules(Array.Empty<string>());
        sut.EnableBlockRules();

        sut.DeleteAllRules();

        Assert.Contains(fake.RunCalls, IsAnchorFlush);
        Assert.Contains(fake.RunCalls, c => c.Arguments.Contains("-X") && c.Arguments.Contains("12345678"));
        Assert.DoesNotContain(fake.RunCalls, c => c.Arguments.Contains("/etc/pf.conf"));
    }

    [Fact]
    public void DeleteAllRules_when_nothing_loaded_never_touches_main_ruleset()
    {
        WriteConfig("9.9.9.9");
        var fake = OkRunner();
        var sut = Sut(fake); // never armed, never enabled

        sut.DeleteAllRules();

        // Pre-P0.3 this unconditionally reloaded /etc/pf.conf on EVERY shutdown,
        // stomping other pf users' runtime carriers. Now: harmless anchor flush only.
        Assert.DoesNotContain(fake.RunCalls, c => c.Arguments.Contains("/etc/pf.conf"));
        Assert.DoesNotContain(fake.RunCalls, IsMainLoad);
    }

    [Fact]
    public void Dispose_after_load_flushes_anchor_antibrick()
    {
        WriteConfig("9.9.9.9");
        var fake = OkRunner();
        var sut = Sut(fake);
        sut.CreateBlockRules(Array.Empty<string>());
        sut.EnableBlockRules();

        sut.Dispose();

        Assert.Contains(fake.RunCalls, IsAnchorFlush);
        Assert.DoesNotContain(fake.RunCalls, c => c.Arguments.Contains("/etc/pf.conf"));
    }

    // ── failure paths ───────────────────────────────────────────────────────

    [Fact]
    public void Enable_when_all_loads_fail_releases_enable_and_stays_unloaded()
    {
        WriteConfig("9.9.9.9");
        var fake = new FakeProcessRunner();
        fake.OnRun(r => r.Arguments.Contains("-E"), Ok(stderr: "Token : 999"));
        fake.OnRun(r => r.Arguments.Contains("-sr"), Fail());   // can't inspect ruleset
        fake.OnRun(r => r.Arguments.Contains("-f"), Fail());    // every load refused (no sudoers grant)
        fake.OnRun(r => r.ExecutablePath == "/usr/bin/sudo", Ok());
        var sut = Sut(fake);

        sut.CreateBlockRules(Array.Empty<string>());
        sut.EnableBlockRules();

        // Released our pf-enable ref after the failed load (don't leave pf
        // enabled-by-us with no blocking ruleset), and no engaged marker.
        Assert.Contains(fake.RunCalls, c => c.Arguments.Contains("-X") && c.Arguments.Contains("999"));
        Assert.False(File.Exists(_marker));

        // Not loaded → a subsequent Disable is a no-op.
        var before = fake.RunCalls.Count;
        sut.DisableBlockRules();
        Assert.Equal(before, fake.RunCalls.Count);
    }

    [Fact]
    public void Enable_when_anchor_body_load_fails_releases_and_does_not_claim_block()
    {
        WriteConfig("9.9.9.9");
        var fake = new FakeProcessRunner();
        fake.OnRun(r => r.Arguments.Contains("-E"), Ok(stderr: "Token : 777"));
        fake.OnRun(r => r.Arguments.Contains("-sr"), Ok(stdout: "anchor \"com.apple/*\" all\n"));
        fake.OnRun(r => r.Arguments.Contains("-a") && r.Arguments.Contains("-f"), Fail()); // anchor body refused
        fake.OnRun(r => r.ExecutablePath == "/usr/bin/sudo", Ok()); // carrier main load ok
        var sut = Sut(fake);

        sut.CreateBlockRules(Array.Empty<string>());
        sut.EnableBlockRules();

        // Carrier landed but the BODY didn't → an empty anchor is inert, so we
        // must not claim engaged: token released, no marker, Disable no-op.
        Assert.Contains(fake.RunCalls, c => c.Arguments.Contains("-X") && c.Arguments.Contains("777"));
        Assert.False(File.Exists(_marker));
    }

    // ── legacy fallback (pf.conf unreadable) ────────────────────────────────

    [Fact]
    public void Enable_with_unreadable_pfconf_falls_back_to_legacy_broad_load()
    {
        WriteConfig("9.9.9.9");
        var fake = OkRunner();
        var missingPfConf = Path.Combine(Path.GetTempPath(), "missing-" + Guid.NewGuid().ToString("N") + ".conf");
        var sut = Sut(fake, pfconf: missingPfConf);

        sut.CreateBlockRules(Array.Empty<string>());
        sut.EnableBlockRules();

        // Broad main-ruleset load of the BLOCK rules (pre-P0.3 shape) so the
        // kill-switch still blocks — correctness over blast-radius hygiene.
        var broad = fake.RunCalls.FirstOrDefault(IsMainLoad);
        Assert.NotNull(broad);
        Assert.Contains("block drop out all", File.ReadAllText(broad!.Arguments.Last()));
        Assert.Equal("engaged", File.ReadAllText(_marker)); // legacy marker → legacy cleanup

        sut.DisableBlockRules();
        // Legacy engage → legacy restore.
        Assert.Contains(fake.RunCalls, c => c.Arguments.Contains("-f") && c.Arguments.Contains("/etc/pf.conf"));
    }

    // ── rules content (D8) ──────────────────────────────────────────────────

    [Fact]
    public void BuildRules_blocks_all_then_passes_loopback_lan_and_servers()
    {
        var rules = MacFirewallManager.BuildRules(new List<string> { "1.2.3.4" });
        Assert.Contains("block drop out all", rules);
        Assert.Contains("pass out quick on lo0", rules);
        Assert.Contains("10.0.0.0/8", rules);
        Assert.Contains("172.16.0.0/12", rules);
        Assert.Contains("192.168.0.0/16", rules);
        Assert.Contains("169.254.0.0/16", rules);
        Assert.Contains("pass out quick inet from any to 1.2.3.4", rules);
        Assert.DoesNotContain("set block-policy", rules); // would fail `pfctl -a … -f`
    }

    [Fact]
    public void BuildRules_MixedFamily_BothFamilies()
    {
        var rules = MacFirewallManager.BuildRules(new List<string> { "1.2.3.4", "2001:db8::1" });

        // Per-IP family: IPv4 → inet, IPv6 → inet6 (a malformed `inet` rule for
        // the IPv6 literal would make pfctl reject the whole atomic load).
        Assert.Contains("pass out quick inet from any to 1.2.3.4", rules);
        Assert.Contains("pass out quick inet6 from any to 2001:db8::1", rules);
        Assert.DoesNotContain("pass out quick inet from any to 2001:db8::1", rules);
    }

    [Theory]
    [InlineData("pf enabled\nToken : 12345678", "12345678")]
    [InlineData("Token : 42", "42")]
    [InlineData("no token here", null)]
    [InlineData("", null)]
    public void ParsePfToken_extracts_numeric_token(string stderr, string? expected)
        => Assert.Equal(expected, MacFirewallManager.ParsePfToken(stderr));

    // ── server allow-list (D9 rename + hostname resolve) ───────────────────

    [Fact]
    public void ReadServerIps_uses_literal_ips_and_omits_unresolved_hostnames()
    {
        // A hostname that does not resolve can't be a pf rule target → omitted;
        // only the literal IP passes.
        File.WriteAllText(_cfg, @"{ ""outbounds"": [
            { ""type"": ""vless"", ""server"": ""example.com"" },
            { ""type"": ""vless"", ""server"": ""5.6.7.8"" } ] }");
        var fake = OkRunner();
        var sut = new MacFirewallManager(null, fake, _cfg, _marker,
            hostResolver: _ => Array.Empty<string>(), pfConfPath: _pfconf,
            rulesPath: _rules, mainConfPath: _mainConf);

        sut.CreateBlockRules(Array.Empty<string>());
        sut.EnableBlockRules();

        var load = fake.RunCalls.First(IsAnchorLoad);
        var rules = File.ReadAllText(load.Arguments.Last());
        Assert.Contains("5.6.7.8", rules);
        Assert.DoesNotContain("example.com", rules);
    }

    [Fact]
    public void ReadServerIps_resolves_hostname_server_to_ip()
    {
        // Hostname server → must be RESOLVED (while VPN healthy) into the pf
        // pass-list, else the kill-switch blocks crash-reconnect → bricked Mac.
        File.WriteAllText(_cfg, @"{ ""outbounds"": [
            { ""type"": ""vless"", ""server"": ""proxy.example.com"" } ] }");
        var fake = OkRunner();
        var sut = new MacFirewallManager(null, fake, _cfg, _marker,
            hostResolver: h => h == "proxy.example.com"
                ? new[] { "203.0.113.10" }
                : (IReadOnlyList<string>)Array.Empty<string>(),
            pfConfPath: _pfconf, rulesPath: _rules, mainConfPath: _mainConf);

        sut.CreateBlockRules(Array.Empty<string>());
        sut.EnableBlockRules();

        var load = fake.RunCalls.First(IsAnchorLoad);
        var rules = File.ReadAllText(load.Arguments.Last());
        Assert.Contains("203.0.113.10", rules);            // resolved IP allowed
        Assert.DoesNotContain("proxy.example.com", rules); // hostname never in pf
    }

    [Fact]
    public void ReadServerIps_WireGuardEndpoint_PeerLiteralIPv4AndIPv6_PresentInGeneratedRules()
    {
        // WireGuard endpoint with literal IPv4 and IPv6 peer addresses (including AmneziaWG obfuscation fields)
        File.WriteAllText(_cfg, @"{
            ""endpoints"": [
                {
                    ""type"": ""wireguard"",
                    ""tag"": ""proxy"",
                    ""jc"": 4,
                    ""jmin"": 10,
                    ""jmax"": 20,
                    ""s1"": 50,
                    ""s2"": 100,
                    ""h1"": ""123456"",
                    ""address"": [ ""10.0.0.2/32"" ],
                    ""peers"": [
                        { ""address"": ""198.51.100.1"", ""port"": 51820, ""allowed_ips"": [ ""0.0.0.0/0"" ] },
                        { ""address"": ""2001:db8::cafe"", ""port"": 51820, ""allowed_ips"": [ ""::/0"" ] }
                    ]
                }
            ]
        }");
        var fake = OkRunner();
        var sut = Sut(fake);

        sut.CreateBlockRules(Array.Empty<string>());
        sut.EnableBlockRules();

        var load = fake.RunCalls.First(IsAnchorLoad);
        var rules = File.ReadAllText(load.Arguments.Last());
        Assert.Contains("pass out quick inet from any to 198.51.100.1", rules);
        Assert.Contains("pass out quick inet6 from any to 2001:db8::cafe", rules);

        var extracted = sut.ReadServerIps();
        Assert.Contains("198.51.100.1", extracted);
        Assert.Contains("2001:db8::cafe", extracted);
    }

    [Fact]
    public void ReadServerIps_HostnameResolver_GivesAAndAaaaCanonicalDedupe()
    {
        File.WriteAllText(_cfg, @"{
            ""endpoints"": [
                {
                    ""type"": ""wireguard"",
                    ""peers"": [
                        { ""address"": ""wg.example.com"" }
                    ]
                }
            ]
        }");
        var fake = OkRunner();
        var sut = new MacFirewallManager(null, fake, _cfg, _marker,
            hostResolver: h => h == "wg.example.com"
                ? new[] { "198.51.100.20", "2001:0db8:0000:0000:0000:0000:0000:0001", "2001:db8::1", "198.51.100.20" }
                : (IReadOnlyList<string>)Array.Empty<string>(),
            pfConfPath: _pfconf, rulesPath: _rules, mainConfPath: _mainConf);

        sut.CreateBlockRules(Array.Empty<string>());
        sut.EnableBlockRules();

        var load = fake.RunCalls.First(IsAnchorLoad);
        var rules = File.ReadAllText(load.Arguments.Last());
        Assert.Contains("pass out quick inet from any to 198.51.100.20", rules);
        Assert.Contains("pass out quick inet6 from any to 2001:db8::1", rules);
        Assert.DoesNotContain("2001:0db8", rules);

        var lines = rules.Split('\n').Select(l => l.Trim()).ToList();
        Assert.Equal(1, lines.Count(l => l == "pass out quick inet from any to 198.51.100.20"));
        Assert.Equal(1, lines.Count(l => l == "pass out quick inet6 from any to 2001:db8::1"));

        var extracted = sut.ReadServerIps();
        Assert.Equal(new[] { "198.51.100.20", "2001:db8::1" }, extracted);
    }

    [Fact]
    public void ReadServerIps_WireGuardEndpoint_LocalInterfaceCidrAndAllowedIps_Absent()
    {
        File.WriteAllText(_cfg, @"{
            ""endpoints"": [
                {
                    ""type"": ""wireguard"",
                    ""address"": [ ""172.31.254.2/32"", ""fd00:abcd::2/128"" ],
                    ""peers"": [
                        {
                            ""address"": ""198.51.100.55"",
                            ""port"": 51820,
                            ""allowed_ips"": [ ""0.0.0.0/0"", ""198.18.0.0/15"", ""2001:db8:ffff::/48"" ]
                        }
                    ]
                }
            ]
        }");
        var fake = OkRunner();
        var sut = Sut(fake);

        sut.CreateBlockRules(Array.Empty<string>());
        sut.EnableBlockRules();

        var load = fake.RunCalls.First(IsAnchorLoad);
        var rules = File.ReadAllText(load.Arguments.Last());
        Assert.Contains("pass out quick inet from any to 198.51.100.55", rules);

        // Local interface CIDR and tunnel addresses must never be allowlisted
        Assert.DoesNotContain("172.31.254.2", rules);
        Assert.DoesNotContain("fd00:abcd::2", rules);
        Assert.DoesNotContain("/32", rules);
        Assert.DoesNotContain("/128", rules);

        // Allowed IPs CIDR ranges must never be allowlisted
        Assert.DoesNotContain("198.18.0.0", rules);
        Assert.DoesNotContain("2001:db8:ffff", rules);
        Assert.DoesNotContain("0.0.0.0/0", rules);
    }

    [Fact]
    public void ReadServerIps_UnknownEndpointType_Excluded()
    {
        File.WriteAllText(_cfg, @"{
            ""endpoints"": [
                {
                    ""type"": ""unknown-proto"",
                    ""peers"": [ { ""address"": ""198.51.100.99"" } ]
                },
                {
                    ""type"": ""tun"",
                    ""peers"": [ { ""address"": ""198.51.100.88"" } ]
                },
                {
                    ""type"": ""wireguard"",
                    ""peers"": [ { ""address"": ""198.51.100.77"" } ]
                }
            ]
        }");
        var fake = OkRunner();
        var sut = Sut(fake);

        sut.CreateBlockRules(Array.Empty<string>());
        sut.EnableBlockRules();

        var load = fake.RunCalls.First(IsAnchorLoad);
        var rules = File.ReadAllText(load.Arguments.Last());
        Assert.Contains("pass out quick inet from any to 198.51.100.77", rules);
        Assert.DoesNotContain("198.51.100.99", rules);
        Assert.DoesNotContain("198.51.100.88", rules);

        var extracted = sut.ReadServerIps();
        Assert.Equal(new[] { "198.51.100.77" }, extracted);
    }

    [Fact]
    public void ReadServerIps_MalformedSiblingsAndThrowingResolver_DoNotLoseValidLaterPeers()
    {
        File.WriteAllText(_cfg, @"{
            ""endpoints"": [
                null,
                123,
                { ""type"": null },
                {
                    ""type"": ""wireguard"",
                    ""peers"": [
                        { ""address"": 12345 },
                        {},
                        { ""address"": ""throwing.example.com"" },
                        { ""address"": null },
                        { ""address"": ""198.51.100.66"" },
                        { ""address"": ""valid.example.com"" }
                    ]
                }
            ]
        }");
        var fake = OkRunner();
        var sut = new MacFirewallManager(null, fake, _cfg, _marker,
            hostResolver: h =>
            {
                if (h == "throwing.example.com") throw new SocketException(11001);
                if (h == "valid.example.com") return new[] { "198.51.100.77" };
                return Array.Empty<string>();
            },
            pfConfPath: _pfconf, rulesPath: _rules, mainConfPath: _mainConf);

        sut.CreateBlockRules(Array.Empty<string>());
        sut.EnableBlockRules();

        var load = fake.RunCalls.First(IsAnchorLoad);
        var rules = File.ReadAllText(load.Arguments.Last());
        Assert.Contains("pass out quick inet from any to 198.51.100.66", rules);
        Assert.Contains("pass out quick inet from any to 198.51.100.77", rules);

        var extracted = sut.ReadServerIps();
        Assert.Equal(new[] { "198.51.100.66", "198.51.100.77" }, extracted);
    }

    [Fact]
    public void ReadServerIps_InvalidInjectedResolverResult_AbsentFromGeneratedRules()
    {
        File.WriteAllText(_cfg, @"{
            ""outbounds"": [
                { ""type"": ""vless"", ""server"": ""injected.example.com"" }
            ]
        }");
        var fake = OkRunner();
        var sut = new MacFirewallManager(null, fake, _cfg, _marker,
            hostResolver: h => new[]
            {
                "1.2.3.4\npass out quick any",
                "not-an-ip-address",
                "10.0.0.1; rm -rf",
                "2001:db8::1 } accept",
                "93.184.216.34"
            },
            pfConfPath: _pfconf, rulesPath: _rules, mainConfPath: _mainConf);

        sut.CreateBlockRules(Array.Empty<string>());
        sut.EnableBlockRules();

        var load = fake.RunCalls.First(IsAnchorLoad);
        var rules = File.ReadAllText(load.Arguments.Last());
        Assert.Contains("pass out quick inet from any to 93.184.216.34", rules);
        Assert.DoesNotContain("pass out quick any", rules);
        Assert.DoesNotContain("rm -rf", rules);
        Assert.DoesNotContain("accept", rules);
        Assert.DoesNotContain("not-an-ip-address", rules);

        var extracted = sut.ReadServerIps();
        Assert.Equal(new[] { "93.184.216.34" }, extracted);
    }

    // ── marker + orphan cleanup (D5/D6/D7) ──────────────────────────────────

    [Fact]
    public void Enable_writes_anchorV1_marker_Disable_clears_it()
    {
        WriteConfig("9.9.9.9");
        var sut = Sut(OkRunner());
        sut.CreateBlockRules(Array.Empty<string>());

        sut.EnableBlockRules();
        Assert.True(File.Exists(_marker));                       // engaged → crash-recovery sentinel present
        Assert.Equal("anchor-v1", File.ReadAllText(_marker));    // content encodes the engage MODE

        sut.DisableBlockRules();
        Assert.False(File.Exists(_marker));  // clean teardown → sentinel gone
    }

    [Fact]
    public void CleanupOrphanedRules_with_anchorV1_marker_flushes_anchor_only()
    {
        File.WriteAllText(_marker, "anchor-v1"); // prior hard kill while engaged (anchor mode)
        var fake = OkRunner();
        var sut = Sut(fake);

        sut.CleanupOrphanedRules(null);

        Assert.Contains(fake.RunCalls, IsAnchorFlush);
        // Anchor-mode recovery must NOT reload the stock ruleset — the main
        // ruleset was never ours, and reloading it wipes other tools' carriers.
        Assert.DoesNotContain(fake.RunCalls, c => c.Arguments.Contains("/etc/pf.conf"));
        Assert.False(File.Exists(_marker)); // sentinel cleared after recovery
    }

    [Fact]
    public void CleanupOrphanedRules_with_legacy_marker_restores_default_and_clears_marker()
    {
        // Backward compat: an install upgraded mid-engage (or a pre-P0.3 crash)
        // left the legacy marker — its broad main-ruleset load can only be
        // undone by the stock restore.
        File.WriteAllText(_marker, "engaged");
        var fake = OkRunner();
        var sut = Sut(fake);

        sut.CleanupOrphanedRules(null);

        Assert.Contains(fake.RunCalls, c => c.Arguments.Contains("-f") && c.Arguments.Contains("/etc/pf.conf"));
        Assert.False(File.Exists(_marker)); // sentinel cleared after recovery
    }

    [Fact]
    public void CleanupOrphanedRules_without_marker_is_noop()
    {
        var fake = OkRunner();
        var sut = Sut(fake); // no marker file

        sut.CleanupOrphanedRules(null);

        Assert.Empty(fake.RunCalls); // a normal launch must never touch pf
    }

    [Fact]
    public void Enable_WritesRulesToConfiguredPath_NotSharedTemp()
    {
        // FW-02: verify that MacFirewallManager writes rulesets into private AppPaths.DataDir
        // or explicitly configured paths, never world-writable /tmp.
        WriteConfig("9.9.9.9");
        var customRules = Path.Combine(Path.GetTempPath(), "custom-mac-rules-" + Guid.NewGuid().ToString("N") + ".conf");
        var customMain = Path.Combine(Path.GetTempPath(), "custom-mac-main-" + Guid.NewGuid().ToString("N") + ".conf");
        var fake = OkRunner();
        var sut = new MacFirewallManager(null, fake, _cfg, _marker, null, _pfconf, rulesPath: customRules, mainConfPath: customMain);

        try
        {
            sut.CreateBlockRules(Array.Empty<string>());
            sut.EnableBlockRules();

            Assert.True(File.Exists(customRules));
            Assert.True(File.Exists(customMain));
            Assert.Contains(fake.RunCalls, c => c.Arguments.Contains(customRules));
            Assert.Contains(fake.RunCalls, c => c.Arguments.Contains(customMain));
            Assert.DoesNotContain(fake.RunCalls, c => c.Arguments.Any(a => a.Contains("/tmp/vpnrouter-pf-killswitch.conf")));
            Assert.DoesNotContain(fake.RunCalls, c => c.Arguments.Any(a => a.Contains("/tmp/vpnrouter-pf-main.conf")));
        }
        finally
        {
            try { if (File.Exists(customRules)) File.Delete(customRules); } catch { }
            try { if (File.Exists(customMain)) File.Delete(customMain); } catch { }
        }
    }

    // ── NIGHT04: failure retention, retry, and token preservation ──────────

    [Fact]
    public void Disable_WhenFlushAnchorFails_RetainsLoadedStateAndMarker_AndSubsequentRetryClears()
    {
        WriteConfig("9.9.9.9");
        var flushFails = false;
        var fake = new FakeProcessRunner();
        fake.OnRun(r => r.Arguments.Contains("-E"), Ok(stderr: "Token : 12345678"));
        fake.OnRun(r => r.Arguments.Contains("-sr"), Ok(stdout: "anchor \"com.apple/*\" all\n"));
        fake.OnRun(IsAnchorFlush, _ => Task.FromResult(flushFails ? Fail("flush failed") : Ok()));
        fake.OnRun(r => r.ExecutablePath == "/usr/bin/sudo", Ok());
        var sut = Sut(fake);

        sut.CreateBlockRules(Array.Empty<string>());
        sut.EnableBlockRules();
        Assert.True(File.Exists(_marker));

        // Flush fails: marker and loaded state must be preserved for retry.
        flushFails = true;
        sut.DisableBlockRules();
        Assert.True(File.Exists(_marker));
        Assert.DoesNotContain(fake.RunCalls, c => c.Arguments.Contains("-X"));

        // Retry succeeds: rules cleared, marker deleted, token released.
        flushFails = false;
        sut.DisableBlockRules();
        Assert.False(File.Exists(_marker));
        Assert.Contains(fake.RunCalls, c => c.Arguments.Contains("-X") && c.Arguments.Contains("12345678"));
    }

    [Fact]
    public void DeleteAllRules_WhenFlushAnchorFails_RetainsLoadedStateAndMarker_AndSubsequentRetryClears()
    {
        WriteConfig("9.9.9.9");
        var flushFails = false;
        var fake = new FakeProcessRunner();
        fake.OnRun(r => r.Arguments.Contains("-E"), Ok(stderr: "Token : 12345678"));
        fake.OnRun(r => r.Arguments.Contains("-sr"), Ok(stdout: "anchor \"com.apple/*\" all\n"));
        fake.OnRun(IsAnchorFlush, _ => Task.FromResult(flushFails ? Fail("flush failed") : Ok()));
        fake.OnRun(r => r.ExecutablePath == "/usr/bin/sudo", Ok());
        var sut = Sut(fake);

        sut.CreateBlockRules(Array.Empty<string>());
        sut.EnableBlockRules();
        Assert.True(File.Exists(_marker));

        flushFails = true;
        sut.DeleteAllRules();
        Assert.True(File.Exists(_marker));
        Assert.DoesNotContain(fake.RunCalls, c => c.Arguments.Contains("-X"));

        flushFails = false;
        sut.DeleteAllRules();
        Assert.False(File.Exists(_marker));
        Assert.Contains(fake.RunCalls, c => c.Arguments.Contains("-X") && c.Arguments.Contains("12345678"));
    }

    [Fact]
    public void DeleteAllRules_NewInstance_WithPersistedAnchorMarker_WhenFlushAnchorFails_RetainsMarker_AndSubsequentRetryClears()
    {
        File.WriteAllText(_marker, MacFirewallManager.AnchorMarker);
        var flushFails = false;
        var fake = new FakeProcessRunner();
        fake.OnRun(IsAnchorFlush, _ => Task.FromResult(flushFails ? Fail("anchor flush failed") : Ok()));
        fake.OnRun(r => r.ExecutablePath == "/usr/bin/sudo", Ok());

        // Fresh instance: never armed, never enabled (_loaded == false)
        var sut = Sut(fake);
        Assert.False(GetLoaded(sut));
        Assert.False(GetArmed(sut));

        // Attempt 1: anchor flush fails -> marker must be retained
        flushFails = true;
        sut.DeleteAllRules();
        Assert.True(File.Exists(_marker));
        Assert.Equal(MacFirewallManager.AnchorMarker, File.ReadAllText(_marker));
        Assert.Contains(fake.RunCalls, IsAnchorFlush);
        Assert.DoesNotContain(fake.RunCalls, c => c.Arguments.Contains("/etc/pf.conf"));

        // Attempt 2: retry succeeds -> marker deleted
        flushFails = false;
        sut.DeleteAllRules();
        Assert.False(File.Exists(_marker));
    }

    [Fact]
    public void DeleteAllRules_NewInstance_WithPersistedLegacyMarker_RoutesToDefaultRulesetRestore_AndRetainsMarkerOnFailure_AndClearsOnRetry()
    {
        File.WriteAllText(_marker, MacFirewallManager.LegacyMarker);
        var restoreFails = false;
        var fake = new FakeProcessRunner();
        fake.OnRun(c => c.Arguments.Contains("-f") && c.Arguments.Contains("/etc/pf.conf"),
            _ => Task.FromResult(restoreFails ? Fail("legacy restore failed") : Ok()));
        fake.OnRun(r => r.ExecutablePath == "/usr/bin/sudo", Ok());

        // Fresh instance: never armed, never enabled (_loaded == false)
        var sut = Sut(fake);
        Assert.False(GetLoaded(sut));
        Assert.False(GetArmed(sut));

        // Attempt 1: restore fails -> marker must be retained, anchor must NOT be flushed
        restoreFails = true;
        sut.DeleteAllRules();
        Assert.True(File.Exists(_marker));
        Assert.Equal(MacFirewallManager.LegacyMarker, File.ReadAllText(_marker));
        Assert.Contains(fake.RunCalls, c => c.Arguments.Contains("-f") && c.Arguments.Contains("/etc/pf.conf"));
        Assert.DoesNotContain(fake.RunCalls, IsAnchorFlush);

        // Attempt 2: retry succeeds -> legacy marker cleared
        restoreFails = false;
        sut.DeleteAllRules();
        Assert.False(File.Exists(_marker));
    }

    [Fact]
    public void DeleteAllRules_NewInstance_WithPersistedLegacyMarker_DoesNotClearLegacyMarkerAfterOnlyFlushingAnchor()
    {
        File.WriteAllText(_marker, MacFirewallManager.LegacyMarker);
        var fake = new FakeProcessRunner();
        // Allow anchor flush to succeed, but fail default ruleset restore
        fake.OnRun(IsAnchorFlush, Ok());
        fake.OnRun(c => c.Arguments.Contains("-f") && c.Arguments.Contains("/etc/pf.conf"), Fail("restore failed"));
        fake.OnRun(r => r.ExecutablePath == "/usr/bin/sudo", Ok());

        var sut = Sut(fake);

        sut.DeleteAllRules();

        // Fresh instance must route to legacy cleanup, NOT just flush anchor and clear marker
        Assert.True(File.Exists(_marker));
        Assert.Equal(MacFirewallManager.LegacyMarker, File.ReadAllText(_marker));
        Assert.DoesNotContain(fake.RunCalls, IsAnchorFlush);
        Assert.Contains(fake.RunCalls, c => c.Arguments.Contains("-f") && c.Arguments.Contains("/etc/pf.conf"));
    }

    [Fact]
    public void DeleteAllRules_WhenArmedButUnloaded_FailedFlushAnchorRetainsArmedState_AndRetryClears()
    {
        WriteConfig("9.9.9.9");
        var flushFails = false;
        var fake = new FakeProcessRunner();
        fake.OnRun(IsAnchorFlush, _ => Task.FromResult(flushFails ? Fail("flush failed") : Ok()));
        fake.OnRun(r => r.ExecutablePath == "/usr/bin/sudo", Ok());

        var sut = Sut(fake);
        sut.CreateBlockRules(Array.Empty<string>(), isFullTunnel: true);
        Assert.True(GetArmed(sut));
        Assert.False(GetLoaded(sut));

        // Attempt 1: Flush fails -> armed state must be retained!
        flushFails = true;
        sut.DeleteAllRules();
        Assert.True(GetArmed(sut));

        // Attempt 2: Flush succeeds -> armed state cleared
        flushFails = false;
        sut.DeleteAllRules();
        Assert.False(GetArmed(sut));

        // Disarmed -> EnableBlockRules is a no-op (no pfctl -E)
        sut.EnableBlockRules();
        Assert.DoesNotContain(fake.RunCalls, c => c.Arguments.Contains("-E"));
    }

    [Fact]
    public void DeleteAllRules_WhenLegacyLoaded_RestoresDefaultRuleset_AndReleasesToken()
    {
        WriteConfig("9.9.9.9");
        var fake = OkRunner();
        var missingPfConf = Path.Combine(Path.GetTempPath(), "missing-" + Guid.NewGuid().ToString("N") + ".conf");
        var sut = Sut(fake, pfconf: missingPfConf);

        sut.CreateBlockRules(Array.Empty<string>());
        sut.EnableBlockRules();
        Assert.True(GetLoaded(sut));
        Assert.True(File.Exists(_marker));
        Assert.Equal(MacFirewallManager.LegacyMarker, File.ReadAllText(_marker));

        sut.DeleteAllRules();

        Assert.False(GetLoaded(sut));
        Assert.False(File.Exists(_marker));
        Assert.Contains(fake.RunCalls, c => c.Arguments.Contains("-f") && c.Arguments.Contains("/etc/pf.conf"));
        Assert.DoesNotContain(fake.RunCalls, IsAnchorFlush);
        Assert.Contains(fake.RunCalls, c => c.Arguments.Contains("-X") && c.Arguments.Contains("12345678"));
    }

    [Fact]
    public void CleanupOrphanedRules_WhenAnchorFlushFails_PreservesMarker_AndSubsequentRetryClears()
    {
        File.WriteAllText(_marker, "anchor-v1");
        var flushFails = true;
        var fake = new FakeProcessRunner();
        fake.OnRun(IsAnchorFlush, _ => Task.FromResult(flushFails ? Fail() : Ok()));
        fake.OnRun(r => r.ExecutablePath == "/usr/bin/sudo", Ok());
        var sut = Sut(fake);

        // Attempt 1: anchor flush fails -> marker MUST NOT be deleted.
        sut.CleanupOrphanedRules(null);
        Assert.True(File.Exists(_marker));

        // Attempt 2: retry succeeds -> marker deleted.
        flushFails = false;
        sut.CleanupOrphanedRules(null);
        Assert.False(File.Exists(_marker));
    }

    [Fact]
    public void CleanupOrphanedRules_WhenLegacyRestoreFails_PreservesMarker_AndSubsequentRetryClears()
    {
        File.WriteAllText(_marker, "engaged");
        var restoreFails = true;
        var fake = new FakeProcessRunner();
        fake.OnRun(c => c.Arguments.Contains("-f") && c.Arguments.Contains("/etc/pf.conf"),
            _ => Task.FromResult(restoreFails ? Fail() : Ok()));
        fake.OnRun(r => r.ExecutablePath == "/usr/bin/sudo", Ok());
        var sut = Sut(fake);

        // Attempt 1: stock restore fails -> marker preserved.
        sut.CleanupOrphanedRules(null);
        Assert.True(File.Exists(_marker));

        // Attempt 2: retry succeeds -> marker cleared.
        restoreFails = false;
        sut.CleanupOrphanedRules(null);
        Assert.False(File.Exists(_marker));
    }

    [Fact]
    public void Disable_WhenLegacyRestoreFails_RetainsLoadedStateAndMarker_AndSubsequentRetryClears()
    {
        WriteConfig("9.9.9.9");
        var restoreFails = false;
        var fake = new FakeProcessRunner();
        fake.OnRun(r => r.Arguments.Contains("-E"), Ok(stderr: "Token : 12345678"));
        fake.OnRun(c => c.Arguments.Contains("-f") && c.Arguments.Contains("/etc/pf.conf"),
            _ => Task.FromResult(restoreFails ? Fail() : Ok()));
        fake.OnRun(r => r.ExecutablePath == "/usr/bin/sudo", Ok());

        var missingPfConf = Path.Combine(Path.GetTempPath(), "missing-" + Guid.NewGuid().ToString("N") + ".conf");
        var sut = Sut(fake, pfconf: missingPfConf);

        sut.CreateBlockRules(Array.Empty<string>());
        sut.EnableBlockRules();
        Assert.True(File.Exists(_marker));
        Assert.Equal("engaged", File.ReadAllText(_marker));

        // Disable with failing restore
        restoreFails = true;
        sut.DisableBlockRules();
        Assert.True(File.Exists(_marker));
        Assert.DoesNotContain(fake.RunCalls, c => c.Arguments.Contains("-X"));

        // Retry with succeeding restore
        restoreFails = false;
        sut.DisableBlockRules();
        Assert.False(File.Exists(_marker));
        Assert.Contains(fake.RunCalls, c => c.Arguments.Contains("-X") && c.Arguments.Contains("12345678"));
    }

    [Fact]
    public void TokenRelease_WhenReleaseFails_PreservesTokenForRetry_AndDoesNotAcquireDoubleEnable()
    {
        WriteConfig("9.9.9.9");
        var releaseFails = false;
        var fake = new FakeProcessRunner();
        fake.OnRun(r => r.Arguments.Contains("-E"), Ok(stderr: "Token : 12345678"));
        fake.OnRun(r => r.Arguments.Contains("-sr"), Ok(stdout: "anchor \"com.apple/*\" all\n"));
        fake.OnRun(r => r.Arguments.Contains("-X"), _ => Task.FromResult(releaseFails ? Fail("token release failed") : Ok()));
        fake.OnRun(r => r.ExecutablePath == "/usr/bin/sudo", Ok());
        var sut = Sut(fake);

        sut.CreateBlockRules(Array.Empty<string>());
        sut.EnableBlockRules();
        Assert.True(File.Exists(_marker));

        // Flush rules succeeds, but token release fails:
        // marker is cleared (no block rules remaining), but token is preserved.
        releaseFails = true;
        sut.DisableBlockRules();
        Assert.False(File.Exists(_marker));

        // Re-enable must reuse retained token and NOT acquire a second -E.
        sut.EnableBlockRules();
        Assert.True(File.Exists(_marker));
        var enableCalls = fake.RunCalls.Count(c => c.Arguments.Contains("-E"));
        Assert.Equal(1, enableCalls); // only the initial -E, no second -E

        // Disable with token release now succeeding.
        releaseFails = false;
        sut.DisableBlockRules();
        Assert.False(File.Exists(_marker));

        // Subsequent disable is a no-op because token was successfully released.
        var countBefore = fake.RunCalls.Count;
        sut.DisableBlockRules();
        Assert.Equal(countBefore, fake.RunCalls.Count);
    }

    [Fact]
    public void Dispose_WhenFlushOrTokenReleaseFails_RetriesOnSubsequentDispose()
    {
        WriteConfig("9.9.9.9");
        var flushFails = false;
        var releaseFails = false;
        var fake = new FakeProcessRunner();
        fake.OnRun(r => r.Arguments.Contains("-E"), Ok(stderr: "Token : 12345678"));
        fake.OnRun(r => r.Arguments.Contains("-sr"), Ok(stdout: "anchor \"com.apple/*\" all\n"));
        fake.OnRun(IsAnchorFlush, _ => Task.FromResult(flushFails ? Fail() : Ok()));
        fake.OnRun(r => r.Arguments.Contains("-X"), _ => Task.FromResult(releaseFails ? Fail() : Ok()));
        fake.OnRun(r => r.ExecutablePath == "/usr/bin/sudo", Ok());
        var sut = Sut(fake);

        sut.CreateBlockRules(Array.Empty<string>());
        sut.EnableBlockRules();
        Assert.True(File.Exists(_marker));

        // Call 1: flush fails -> marker kept, token kept.
        flushFails = true;
        releaseFails = true;
        sut.Dispose();
        Assert.True(File.Exists(_marker));

        // Call 2: flush succeeds, but release fails -> marker deleted, token kept.
        flushFails = false;
        sut.Dispose();
        Assert.False(File.Exists(_marker));

        // Call 3: release succeeds -> token released, disposed flag finalized.
        releaseFails = false;
        sut.Dispose();
        Assert.Contains(fake.RunCalls, c => c.Arguments.Contains("-X") && c.Arguments.Contains("12345678"));

        // Call 4: already clean -> no-op.
        var countBefore = fake.RunCalls.Count;
        sut.Dispose();
        Assert.Equal(countBefore, fake.RunCalls.Count);
    }

    [Fact]
    public void RunSudo_WhenExit0ButTimedOut_IsTreatedAsFailure()
    {
        WriteConfig("9.9.9.9");
        var fake = new FakeProcessRunner();
        fake.OnRun(r => r.Arguments.Contains("-E"), Ok(stderr: "Token : 12345678"));
        fake.OnRun(r => r.Arguments.Contains("-sr"), Ok(stdout: "anchor \"com.apple/*\" all\n"));
        // Anchor load returns ExitCode 0 but TimedOut = true:
        fake.OnRun(IsAnchorLoad, new ProcessResult(0, "", "timed out", TimeSpan.FromSeconds(10), TimedOut: true));
        fake.OnRun(r => r.ExecutablePath == "/usr/bin/sudo", Ok());
        var sut = Sut(fake);

        sut.CreateBlockRules(Array.Empty<string>());
        sut.EnableBlockRules();

        // Must NOT treat timed out command as success: rules not loaded, marker not written, token released.
        Assert.False(File.Exists(_marker));
        Assert.Contains(fake.RunCalls, c => c.Arguments.Contains("-X") && c.Arguments.Contains("12345678"));

        // Also test orphan cleanup:
        File.WriteAllText(_marker, "anchor-v1");
        var orphanFake = new FakeProcessRunner();
        orphanFake.OnRun(IsAnchorFlush, new ProcessResult(0, "", "", TimeSpan.FromSeconds(10), TimedOut: true));
        orphanFake.OnRun(r => r.ExecutablePath == "/usr/bin/sudo", Ok());
        var orphanSut = Sut(orphanFake);

        orphanSut.CleanupOrphanedRules(null);
        Assert.True(File.Exists(_marker)); // marker retained because flush timed out!
    }

    [Fact]
    public void Disable_WhenTokenReleaseFails_DirectRetryReleasesTokenWithoutTouchingRules()
    {
        WriteConfig("9.9.9.9");
        var releaseFails = false;
        var fake = new FakeProcessRunner();
        fake.OnRun(r => r.Arguments.Contains("-E"), Ok(stderr: "Token : 12345678"));
        fake.OnRun(r => r.Arguments.Contains("-sr"), Ok(stdout: "anchor \"com.apple/*\" all\n"));
        fake.OnRun(r => r.Arguments.Contains("-X"), _ => Task.FromResult(releaseFails ? Fail("token release failed") : Ok()));
        fake.OnRun(r => r.ExecutablePath == "/usr/bin/sudo", Ok());
        var sut = Sut(fake);

        sut.CreateBlockRules(Array.Empty<string>());
        sut.EnableBlockRules();
        Assert.True(File.Exists(_marker));

        // Call 1: rules flush succeeds, but token release fails -> marker deleted, token retained
        releaseFails = true;
        sut.DisableBlockRules();
        Assert.False(File.Exists(_marker));
        Assert.Contains(fake.RunCalls, c => c.Arguments.Contains("-X") && c.Arguments.Contains("12345678"));

        // Call 2: direct Disable retry without intermediate re-enable -> releases token, rules untouched
        var flushCountBefore = fake.RunCalls.Count(IsAnchorFlush);
        releaseFails = false;
        sut.DisableBlockRules();
        var flushCountAfter = fake.RunCalls.Count(IsAnchorFlush);
        Assert.Equal(flushCountBefore, flushCountAfter);

        // Call 3: subsequent Disable is a complete no-op
        var totalCallsBefore = fake.RunCalls.Count;
        sut.DisableBlockRules();
        Assert.Equal(totalCallsBefore, fake.RunCalls.Count);
    }

    [Theory]
    [InlineData("anchor-v1", "Anchor")]
    [InlineData("  anchor-v1 \n", "Anchor")]
    [InlineData("engaged", "Legacy")]
    [InlineData(" engaged \r\n", "Legacy")]
    [InlineData("unknown", "Unknown")]
    [InlineData("engaged-v2", "Unknown")]
    [InlineData("", "Unknown")]
    public void InspectMarker_ClassifiesKnownAndUnknownMarkers(string content, string expected)
    {
        File.WriteAllText(_marker, content);
        var sut = Sut(OkRunner());
        Assert.Equal(expected, sut.InspectMarker().ToString());
    }

    [Fact]
    public void InspectMarker_WhenMarkerMissing_ReturnsMissing()
    {
        var sut = Sut(OkRunner());
        Assert.False(File.Exists(_marker));
        Assert.Equal(MacFirewallManager.MarkerState.Missing, sut.InspectMarker());
    }

    [Fact]
    public void CleanupOrphanedRules_WithUnknownMarker_RetainsMarker_AndDoesNotBroadRestoreOrFlushAsSuccess()
    {
        File.WriteAllText(_marker, "arbitrary-unknown-content");
        var fake = OkRunner();
        var sut = Sut(fake);

        sut.CleanupOrphanedRules(null);

        // Marker must be retained and no broad restore or flush-as-success executed
        Assert.True(File.Exists(_marker));
        Assert.Equal("arbitrary-unknown-content", File.ReadAllText(_marker));
        Assert.Empty(fake.RunCalls);
    }

    [Fact]
    public void DeleteAllRules_NewInstance_WithPersistedUnknownMarker_RetainsMarker_AndDoesNotBroadRestoreOrFlushAsSuccess()
    {
        File.WriteAllText(_marker, "arbitrary-unknown-content");
        var fake = OkRunner();
        var sut = Sut(fake);

        sut.DeleteAllRules();

        // Marker must be retained, no broad restore (/etc/pf.conf), and no token release
        Assert.True(File.Exists(_marker));
        Assert.Equal("arbitrary-unknown-content", File.ReadAllText(_marker));
        Assert.Empty(fake.RunCalls);
    }

    [Fact]
    public void CleanupOrphanedRules_WhenMarkerUnreadable_RetainsMarker_AndDoesNotBroadRestore()
    {
        File.WriteAllText(_marker, "engaged");
        var fake = OkRunner();
        var sut = Sut(fake);

        using (new FileStream(_marker, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            sut.CleanupOrphanedRules(null);
        }

        Assert.True(File.Exists(_marker));
        Assert.Empty(fake.RunCalls);
    }

    [Fact]
    public void DeleteAllRules_NewInstance_WhenMarkerUnreadable_RetainsMarker_AndDoesNotBroadRestore()
    {
        File.WriteAllText(_marker, "engaged");
        var fake = OkRunner();
        var sut = Sut(fake);

        using (new FileStream(_marker, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            sut.DeleteAllRules();
        }

        Assert.True(File.Exists(_marker));
        Assert.Empty(fake.RunCalls);
    }

    [Fact]
    public void UpdateCommittedConfig_StaleFileOrNoFile_EmitsOnlyCommittedPeersV4V6()
    {
        WriteConfig("198.51.100.1");
        var fake = OkRunner();
        var sut = Sut(fake);

        var committedJsonB = """
        {
          "outbounds": [
            { "type": "vless", "tag": "proxy", "server": "203.0.113.10" }
          ],
          "endpoints": [
            {
              "type": "wireguard",
              "peers": [
                { "address": "2001:db8::10" }
              ]
            }
          ]
        }
        """;

        ((ICommittedFirewallConfig)sut).UpdateCommittedConfig(committedJsonB, enabledForFullTunnel: true);

        Assert.True(sut.IsArmed);
        sut.EnableBlockRules();

        var load = fake.RunCalls.FirstOrDefault(IsAnchorLoad);
        Assert.NotNull(load);
        var rules = File.ReadAllText(load!.Arguments.Last());

        Assert.Contains("pass out quick inet from any to 203.0.113.10", rules);
        Assert.Contains("pass out quick inet6 from any to 2001:db8::10", rules);
        Assert.DoesNotContain("198.51.100.1", rules);

        Assert.Equal(new[] { "203.0.113.10", "2001:db8::10" }, sut.ServerIps);
    }

    [Fact]
    public void UpdateCommittedConfig_NoConfigFileAtAll_EmitsOnlyCommittedPeers()
    {
        if (File.Exists(_cfg)) File.Delete(_cfg);
        var fake = OkRunner();
        var sut = Sut(fake);

        var committedJsonB = """
        {
          "outbounds": [
            { "type": "vless", "tag": "proxy", "server": "203.0.113.11" }
          ]
        }
        """;

        sut.UpdateCommittedConfig(committedJsonB, enabledForFullTunnel: true);
        sut.EnableBlockRules();

        var load = fake.RunCalls.FirstOrDefault(IsAnchorLoad);
        Assert.NotNull(load);
        var rules = File.ReadAllText(load!.Arguments.Last());

        Assert.Contains("203.0.113.11", rules);
        Assert.Equal(new[] { "203.0.113.11" }, sut.ServerIps);
    }

    [Fact]
    public void UpdateCommittedConfig_ActiveAnchorMode_RefreshesAnchorWithB_WithoutCarrierOrEnableOrFlushUnblock()
    {
        WriteConfig("198.51.100.1");
        var fake = OkRunner();
        var sut = Sut(fake);

        sut.CreateBlockRules(Array.Empty<string>(), isFullTunnel: true);
        sut.EnableBlockRules();
        Assert.True(sut.IsLoaded);
        Assert.True(sut.IsAnchorMode);

        int callsBefore = fake.RunCalls.Count;

        var committedJsonB = """
        {
          "outbounds": [
            { "type": "vless", "tag": "proxy", "server": "203.0.113.20" }
          ],
          "endpoints": [
            {
              "type": "wireguard",
              "peers": [
                { "address": "2001:db8::20" }
              ]
            }
          ]
        }
        """;

        sut.UpdateCommittedConfig(committedJsonB, enabledForFullTunnel: true);

        // MUST NOT call EnsureCarrier (pfctl -sr), pfctl -E, pfctl -F rules, or DisableBlockRules
        Assert.DoesNotContain(fake.RunCalls.Skip(callsBefore), c => c.Arguments.Contains("-sr"));
        Assert.DoesNotContain(fake.RunCalls.Skip(callsBefore), c => c.Arguments.Contains("-E"));
        Assert.DoesNotContain(fake.RunCalls.Skip(callsBefore), c => c.Arguments.Contains("-F"));
        Assert.DoesNotContain(fake.RunCalls.Skip(callsBefore), c => c.Arguments.Contains("-X"));

        // Exactly one anchor reload call: pfctl -a Anchor -f <rulesPath>
        var refreshCall = Assert.Single(fake.RunCalls.Skip(callsBefore), IsAnchorLoad);

        var refreshedRules = File.ReadAllText(refreshCall.Arguments.Last());
        Assert.Contains("pass out quick inet from any to 203.0.113.20", refreshedRules);
        Assert.Contains("pass out quick inet6 from any to 2001:db8::20", refreshedRules);
        Assert.DoesNotContain("198.51.100.1", refreshedRules);

        Assert.True(sut.IsLoaded);
        Assert.True(sut.IsAnchorMode);
        Assert.Equal(new[] { "203.0.113.20", "2001:db8::20" }, sut.ServerIps);
    }

    [Fact]
    public void UpdateCommittedConfig_ActiveLegacyMode_RefreshesMainRuleset_WithoutCarrierOrEnable()
    {
        WriteConfig("198.51.100.1");
        // Make /etc/pf.conf missing so EnsureCarrier fails and it falls back to legacy broad load
        if (File.Exists(_pfconf)) File.Delete(_pfconf);

        var fake = OkRunner();
        var sut = Sut(fake);

        sut.CreateBlockRules(Array.Empty<string>(), isFullTunnel: true);
        sut.EnableBlockRules();
        Assert.True(sut.IsLoaded);
        Assert.False(sut.IsAnchorMode);

        int callsBefore = fake.RunCalls.Count;

        var committedJsonB = """
        {
          "outbounds": [
            { "type": "vless", "tag": "proxy", "server": "203.0.113.30" }
          ]
        }
        """;

        sut.UpdateCommittedConfig(committedJsonB, enabledForFullTunnel: true);

        Assert.DoesNotContain(fake.RunCalls.Skip(callsBefore), c => c.Arguments.Contains("-a"));
        Assert.DoesNotContain(fake.RunCalls.Skip(callsBefore), c => c.Arguments.Contains("-E"));

        var refreshCall = Assert.Single(fake.RunCalls.Skip(callsBefore), IsMainLoad);

        var refreshedRules = File.ReadAllText(refreshCall.Arguments.Last());
        Assert.Contains("pass out quick inet from any to 203.0.113.30", refreshedRules);
        Assert.DoesNotContain("198.51.100.1", refreshedRules);

        Assert.True(sut.IsLoaded);
        Assert.False(sut.IsAnchorMode);
        Assert.Equal(new[] { "203.0.113.30" }, sut.ServerIps);
    }

    [Fact]
    public void UpdateCommittedConfig_FailedRefresh_RetainsAForRetry()
    {
        WriteConfig("198.51.100.1");
        var fake = OkRunner();
        var sut = Sut(fake);

        sut.CreateBlockRules(Array.Empty<string>(), isFullTunnel: true);
        sut.EnableBlockRules();
        Assert.True(sut.IsLoaded);
        Assert.Equal(new[] { "198.51.100.1" }, sut.ServerIps);

        // Fail subsequent pfctl load
        fake.OnRun(IsAnchorLoad, Fail("pfctl error"));

        var committedJsonB = """
        {
          "outbounds": [
            { "type": "vless", "tag": "proxy", "server": "203.0.113.99" }
          ]
        }
        """;

        sut.UpdateCommittedConfig(committedJsonB, enabledForFullTunnel: true);

        // Failed refresh keeps old cache/loaded/marker
        Assert.Equal(new[] { "198.51.100.1" }, sut.ServerIps);
        Assert.True(sut.IsLoaded);
        Assert.True(File.Exists(_marker));
    }

    [Fact]
    public void UpdateCommittedConfig_MalformedJson_RetainsPriorList()
    {
        WriteConfig("198.51.100.1");
        var fake = OkRunner();
        var sut = Sut(fake);

        sut.CreateBlockRules(Array.Empty<string>(), isFullTunnel: true);
        Assert.Equal(new[] { "198.51.100.1" }, sut.ServerIps);

        // Malformed committed JSON
        sut.UpdateCommittedConfig("{ invalid json content", enabledForFullTunnel: true);

        // Retains prior list, does not turn parse exception into empty cache
        Assert.Equal(new[] { "198.51.100.1" }, sut.ServerIps);
    }

    [Fact]
    public void UpdateCommittedConfig_Disabled_LiftsRulesAndDisarms()
    {
        WriteConfig("198.51.100.1");
        var fake = OkRunner();
        var sut = Sut(fake);

        sut.CreateBlockRules(Array.Empty<string>(), isFullTunnel: true);
        sut.EnableBlockRules();
        Assert.True(sut.IsLoaded);
        Assert.True(sut.IsArmed);

        int callsBefore = fake.RunCalls.Count;

        var committedJsonB = """
        {
          "outbounds": [
            { "type": "vless", "tag": "proxy", "server": "203.0.113.50" }
          ]
        }
        """;

        sut.UpdateCommittedConfig(committedJsonB, enabledForFullTunnel: false);

        // Disabled mode disarms, flushes anchor, deletes marker, retains prior unused cache
        Assert.False(sut.IsArmed);
        Assert.False(sut.IsLoaded);
        Assert.False(File.Exists(_marker));
        Assert.Contains(fake.RunCalls.Skip(callsBefore), IsAnchorFlush);
        Assert.Equal(new[] { "198.51.100.1" }, sut.ServerIps);
    }

    [Fact]
    public void UpdateCommittedConfig_MalformedJson_Disabled_RemovesRuleAndDisarms()
    {
        WriteConfig("198.51.100.1");
        var fake = OkRunner();
        var sut = Sut(fake);

        sut.CreateBlockRules(Array.Empty<string>(), isFullTunnel: true);
        sut.EnableBlockRules();
        Assert.True(sut.IsLoaded);
        Assert.True(sut.IsArmed);

        int callsBefore = fake.RunCalls.Count;

        // Malformed JSON with disabled branch must still lift rules and disarm without throwing
        sut.UpdateCommittedConfig("{ not valid json content", enabledForFullTunnel: false);

        Assert.False(sut.IsArmed);
        Assert.False(sut.IsLoaded);
        Assert.False(File.Exists(_marker));
        Assert.Contains(fake.RunCalls.Skip(callsBefore), IsAnchorFlush);
        Assert.Equal(new[] { "198.51.100.1" }, sut.ServerIps);
    }

    [Fact]
    public void UpdateCommittedConfig_Disabled_HostnameResolverThrows_InvokesZeroResolverAndDisarms()
    {
        WriteConfig("198.51.100.1");
        var fake = OkRunner();
        var sut = Sut(fake, hostResolver: _ => throw new InvalidOperationException("Hostname resolver must not be invoked when disabled"));

        sut.CreateBlockRules(Array.Empty<string>(), isFullTunnel: true);
        sut.EnableBlockRules();
        Assert.True(sut.IsLoaded);
        Assert.True(sut.IsArmed);

        int callsBefore = fake.RunCalls.Count;

        var committedJsonWithHost = """
        {
          "outbounds": [
            { "type": "vless", "tag": "proxy", "server": "dns-lookup-will-throw.example.com" }
          ]
        }
        """;

        // Must not throw, zero DNS queries invoked when disabled
        sut.UpdateCommittedConfig(committedJsonWithHost, enabledForFullTunnel: false);

        Assert.False(sut.IsArmed);
        Assert.False(sut.IsLoaded);
        Assert.False(File.Exists(_marker));
        Assert.Contains(fake.RunCalls.Skip(callsBefore), IsAnchorFlush);
        Assert.Equal(new[] { "198.51.100.1" }, sut.ServerIps);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("\"string\"")]
    [InlineData("123")]
    public void ParseServerIps_NonObjectRoot_ThrowsJsonException(string malformedRoot)
    {
        var fake = OkRunner();
        var sut = Sut(fake);

        Assert.Throws<JsonException>(() => sut.ParseServerIps(malformedRoot));
    }

    [Fact]
    public void ParseServerIps_EmptyObject_ReturnsEmptyList()
    {
        var fake = OkRunner();
        var sut = Sut(fake);

        var ips = sut.ParseServerIps("{}");
        Assert.Empty(ips);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    public void UpdateCommittedConfig_MalformedRootShape_RetainsPriorList(string malformedRoot)
    {
        WriteConfig("198.51.100.1");
        var fake = OkRunner();
        var sut = Sut(fake);

        sut.CreateBlockRules(Array.Empty<string>(), isFullTunnel: true);
        Assert.Equal(new[] { "198.51.100.1" }, sut.ServerIps);

        sut.UpdateCommittedConfig(malformedRoot, enabledForFullTunnel: true);

        // Retains prior list on non-object root shape
        Assert.Equal(new[] { "198.51.100.1" }, sut.ServerIps);
    }

    [Fact]
    public void UpdateCommittedConfig_EmptyObject_EmptiesList()
    {
        WriteConfig("198.51.100.1");
        var fake = OkRunner();
        var sut = Sut(fake);

        sut.CreateBlockRules(Array.Empty<string>(), isFullTunnel: true);
        Assert.Equal(new[] { "198.51.100.1" }, sut.ServerIps);

        // Empty JSON object is valid committed config, clears server IPs without leak policy regression
        sut.UpdateCommittedConfig("{}", enabledForFullTunnel: true);

        Assert.Empty(sut.ServerIps);
    }
}
