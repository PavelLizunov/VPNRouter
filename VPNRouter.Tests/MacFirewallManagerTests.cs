using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

    private MacFirewallManager Sut(FakeProcessRunner fake, string? pfconf = null) =>
        new MacFirewallManager(null, fake, _cfg, _marker, pfConfPath: pfconf ?? _pfconf);

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
            hostResolver: _ => Array.Empty<string>(), pfConfPath: _pfconf);

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
            pfConfPath: _pfconf);

        sut.CreateBlockRules(Array.Empty<string>());
        sut.EnableBlockRules();

        var load = fake.RunCalls.First(IsAnchorLoad);
        var rules = File.ReadAllText(load.Arguments.Last());
        Assert.Contains("203.0.113.10", rules);            // resolved IP allowed
        Assert.DoesNotContain("proxy.example.com", rules); // hostname never in pf
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
}
