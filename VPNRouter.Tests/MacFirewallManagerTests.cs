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
/// Wire-shape + safety coverage for the macOS pf kill-switch (r6,
/// plans/phase3-macos-pf-killswitch-r6-design-2026-06-04.md). MacFirewallManager
/// is pure IProcessRunner orchestration, so the exact pfctl command shapes — the
/// part where a wrong token / missing flush bricks the user's network — are
/// pinned here on the Windows build. The live block / reconnect / no-brick
/// behaviour is verified on the Mac host via the kill-9 SSH gate.
/// </summary>
public class MacFirewallManagerTests : IDisposable
{
    private readonly string _cfg =
        Path.Combine(Path.GetTempPath(), "vpnrouter-fw-cfg-" + Guid.NewGuid().ToString("N") + ".json");
    private readonly string _marker =
        Path.Combine(Path.GetTempPath(), "vpnrouter-fw-marker-" + Guid.NewGuid().ToString("N") + ".marker");

    public void Dispose()
    {
        try { if (File.Exists(_cfg)) File.Delete(_cfg); } catch { }
        try { if (File.Exists(_marker)) File.Delete(_marker); } catch { }
    }

    private static ProcessResult Ok(string stdout = "", string stderr = "") =>
        new ProcessResult(0, stdout, stderr, TimeSpan.Zero, false);
    private static ProcessResult Fail(string stderr = "pfctl: permission denied") =>
        new ProcessResult(1, "", stderr, TimeSpan.Zero, false);

    private void WriteConfig(string serverIp) =>
        File.WriteAllText(_cfg, $@"{{ ""outbounds"": [
            {{ ""type"": ""vless"", ""tag"": ""proxy"", ""server"": ""{serverIp}"" }},
            {{ ""type"": ""direct"", ""tag"": ""direct"" }} ] }}");

    /// <summary>sudo -E returns a token on stderr; everything else (-f, -X) ok.</summary>
    private static FakeProcessRunner OkRunner()
    {
        var f = new FakeProcessRunner();
        f.OnRun(r => r.ExecutablePath == "/usr/bin/sudo" && r.Arguments.Contains("-E"),
            Ok(stderr: "Token : 12345678"));
        f.OnRun(r => r.ExecutablePath == "/usr/bin/sudo", Ok());
        return f;
    }

    [Fact]
    public void SplitTunnel_nonEmpty_list_disarms_and_Enable_is_noop()
    {
        WriteConfig("9.9.9.9");
        var fake = OkRunner();
        var sut = new MacFirewallManager(null, fake, _cfg, _marker);

        sut.CreateBlockRules(new[] { "Discord", "chrome" });
        sut.EnableBlockRules();

        Assert.Empty(fake.RunCalls); // never armed → no pfctl at all (full-tunnel-only)
    }

    [Fact]
    public void FullTunnel_emptyList_arms_and_Enable_loads_ruleset_with_server_ip()
    {
        WriteConfig("104.194.156.93");
        var fake = OkRunner();
        var sut = new MacFirewallManager(null, fake, _cfg, _marker);

        sut.CreateBlockRules(Array.Empty<string>());
        sut.EnableBlockRules();

        Assert.Contains(fake.RunCalls, c => c.ExecutablePath == "/usr/bin/sudo" && c.Arguments.Contains("-E"));
        var load = fake.RunCalls.FirstOrDefault(c =>
            c.ExecutablePath == "/usr/bin/sudo" && c.Arguments.Contains("-f") &&
            !c.Arguments.Contains("/etc/pf.conf"));
        Assert.NotNull(load);
        var rules = File.ReadAllText(load!.Arguments.Last());
        Assert.Contains("block drop out all", rules);
        Assert.Contains("104.194.156.93", rules); // server pass → sing-box can reconnect
    }

    [Fact]
    public void Enable_when_load_fails_releases_enable_and_stays_unloaded()
    {
        WriteConfig("9.9.9.9");
        var fake = new FakeProcessRunner();
        fake.OnRun(r => r.ExecutablePath == "/usr/bin/sudo" && r.Arguments.Contains("-E"), Ok(stderr: "Token : 999"));
        fake.OnRun(r => r.ExecutablePath == "/usr/bin/sudo" && r.Arguments.Contains("-f")
            && !r.Arguments.Contains("/etc/pf.conf"), Fail());
        fake.OnRun(r => r.ExecutablePath == "/usr/bin/sudo", Ok());
        var sut = new MacFirewallManager(null, fake, _cfg, _marker);

        sut.CreateBlockRules(Array.Empty<string>());
        sut.EnableBlockRules();

        // Released our pf-enable ref after the failed load (don't leave pf
        // enabled-by-us with no blocking ruleset).
        Assert.Contains(fake.RunCalls, c => c.Arguments.Contains("-X") && c.Arguments.Contains("999"));

        // Not loaded → a subsequent Disable is a no-op (no default-ruleset restore).
        var before = fake.RunCalls.Count;
        sut.DisableBlockRules();
        Assert.Equal(before, fake.RunCalls.Count);
    }

    [Fact]
    public void Disable_after_load_restores_default_and_releases_token()
    {
        WriteConfig("9.9.9.9");
        var fake = OkRunner();
        var sut = new MacFirewallManager(null, fake, _cfg, _marker);
        sut.CreateBlockRules(Array.Empty<string>());
        sut.EnableBlockRules();

        sut.DisableBlockRules();

        Assert.Contains(fake.RunCalls, c => c.Arguments.Contains("-f") && c.Arguments.Contains("/etc/pf.conf"));
        Assert.Contains(fake.RunCalls, c => c.Arguments.Contains("-X") && c.Arguments.Contains("12345678"));
    }

    [Fact]
    public void Dispose_after_load_restores_default_antibrick()
    {
        WriteConfig("9.9.9.9");
        var fake = OkRunner();
        var sut = new MacFirewallManager(null, fake, _cfg, _marker);
        sut.CreateBlockRules(Array.Empty<string>());
        sut.EnableBlockRules();

        sut.Dispose();

        Assert.Contains(fake.RunCalls, c => c.Arguments.Contains("-f") && c.Arguments.Contains("/etc/pf.conf"));
    }

    [Fact]
    public void Disable_without_load_is_noop()
    {
        WriteConfig("9.9.9.9");
        var fake = OkRunner();
        var sut = new MacFirewallManager(null, fake, _cfg, _marker);
        sut.CreateBlockRules(Array.Empty<string>()); // armed but never enabled

        sut.DisableBlockRules();

        Assert.DoesNotContain(fake.RunCalls, c => c.Arguments.Contains("/etc/pf.conf"));
    }

    [Fact]
    public void BuildRules_blocks_all_then_passes_loopback_lan_and_servers()
    {
        var rules = MacFirewallManager.BuildRules(new List<string> { "1.2.3.4" });
        Assert.Contains("block drop out all", rules);
        Assert.Contains("pass out quick on lo0", rules);
        Assert.Contains("10.0.0.0/8", rules);
        Assert.Contains("192.168.0.0/16", rules);
        Assert.Contains("pass out quick inet from any to 1.2.3.4", rules);
    }

    [Theory]
    [InlineData("pf enabled\nToken : 12345678", "12345678")]
    [InlineData("Token : 42", "42")]
    [InlineData("no token here", null)]
    [InlineData("", null)]
    public void ParsePfToken_extracts_numeric_token(string stderr, string? expected)
        => Assert.Equal(expected, MacFirewallManager.ParsePfToken(stderr));

    [Fact]
    public void ReadServerIps_skips_hostnames_keeps_ips()
    {
        // A hostname can't be a pf rule target → skipped; only the literal IP passes.
        File.WriteAllText(_cfg, @"{ ""outbounds"": [
            { ""type"": ""vless"", ""server"": ""example.com"" },
            { ""type"": ""vless"", ""server"": ""5.6.7.8"" } ] }");
        var fake = OkRunner();
        var sut = new MacFirewallManager(null, fake, _cfg, _marker);

        sut.CreateBlockRules(Array.Empty<string>());
        sut.EnableBlockRules();

        var load = fake.RunCalls.First(c => c.Arguments.Contains("-f") && !c.Arguments.Contains("/etc/pf.conf"));
        var rules = File.ReadAllText(load.Arguments.Last());
        Assert.Contains("5.6.7.8", rules);
        Assert.DoesNotContain("example.com", rules);
    }

    // ── r9 (claude-code audit): hostname allowlist + orphan cleanup ──────────

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
                : (IReadOnlyList<string>)System.Array.Empty<string>());

        sut.CreateBlockRules(System.Array.Empty<string>());
        sut.EnableBlockRules();

        var load = fake.RunCalls.First(c => c.Arguments.Contains("-f") && !c.Arguments.Contains("/etc/pf.conf"));
        var rules = File.ReadAllText(load.Arguments.Last());
        Assert.Contains("203.0.113.10", rules);            // resolved IP allowed
        Assert.DoesNotContain("proxy.example.com", rules); // hostname never in pf
    }

    [Fact]
    public void Enable_writes_engaged_marker_Disable_clears_it()
    {
        WriteConfig("9.9.9.9");
        var sut = new MacFirewallManager(null, OkRunner(), _cfg, _marker);
        sut.CreateBlockRules(System.Array.Empty<string>());

        sut.EnableBlockRules();
        Assert.True(File.Exists(_marker));   // engaged → crash-recovery sentinel present

        sut.DisableBlockRules();
        Assert.False(File.Exists(_marker));  // clean teardown → sentinel gone
    }

    [Fact]
    public void CleanupOrphanedRules_with_marker_restores_default_and_clears_marker()
    {
        File.WriteAllText(_marker, "engaged"); // simulate a prior hard kill while engaged
        var fake = OkRunner();
        var sut = new MacFirewallManager(null, fake, _cfg, _marker);

        sut.CleanupOrphanedRules(null);

        Assert.Contains(fake.RunCalls, c => c.Arguments.Contains("-f") && c.Arguments.Contains("/etc/pf.conf"));
        Assert.False(File.Exists(_marker)); // sentinel cleared after recovery
    }

    [Fact]
    public void CleanupOrphanedRules_without_marker_is_noop()
    {
        var fake = OkRunner();
        var sut = new MacFirewallManager(null, fake, _cfg, _marker); // no marker file

        sut.CleanupOrphanedRules(null);

        Assert.Empty(fake.RunCalls); // a normal launch must never touch pf
    }
}
