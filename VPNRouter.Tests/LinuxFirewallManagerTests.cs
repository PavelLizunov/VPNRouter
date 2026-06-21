using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using VPNRouter.Core.Platform.Linux;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// Wire-shape + safety coverage for the Linux nft kill-switch (counterpart to
/// <see cref="MacFirewallManagerTests"/>). LinuxFirewallManager is pure
/// IProcessRunner orchestration, so the exact nft command shapes — the part where
/// a wrong ruleset / missing teardown bricks the user's network — are pinned here
/// on the Windows build. Live block / reconnect / no-brick behaviour is verified
/// on a real Linux host (the kill-9 gate).
/// </summary>
public class LinuxFirewallManagerTests : IDisposable
{
    private readonly string _cfg =
        Path.Combine(Path.GetTempPath(), "vpnrouter-lfw-cfg-" + Guid.NewGuid().ToString("N") + ".json");
    private readonly string _marker =
        Path.Combine(Path.GetTempPath(), "vpnrouter-lfw-marker-" + Guid.NewGuid().ToString("N") + ".marker");

    public void Dispose()
    {
        try { if (File.Exists(_cfg)) File.Delete(_cfg); } catch { }
        try { if (File.Exists(_marker)) File.Delete(_marker); } catch { }
    }

    private static ProcessResult Ok(string stdout = "", string stderr = "") =>
        new ProcessResult(0, stdout, stderr, TimeSpan.Zero, false);
    private static ProcessResult Fail(string stderr = "sudo: a password is required") =>
        new ProcessResult(1, "", stderr, TimeSpan.Zero, false);

    private void WriteConfig(string serverIp) =>
        File.WriteAllText(_cfg, $@"{{ ""outbounds"": [
            {{ ""type"": ""vless"", ""tag"": ""proxy"", ""server"": ""{serverIp}"" }},
            {{ ""type"": ""direct"", ""tag"": ""direct"" }} ] }}");

    private static FakeProcessRunner OkRunner()
    {
        var f = new FakeProcessRunner();
        f.OnRun(r => r.ExecutablePath == "/usr/bin/sudo", Ok());
        return f;
    }

    private static string LoadedRulesetFile(FakeProcessRunner f) =>
        f.RunCalls.First(c => c.ExecutablePath == "/usr/bin/sudo" && c.Arguments.Contains("-f")).Arguments.Last();

    [Fact]
    public void SplitTunnel_nonEmpty_list_disarms_and_Enable_is_noop()
    {
        WriteConfig("9.9.9.9");
        var fake = OkRunner();
        var sut = new LinuxFirewallManager(null, fake, _cfg, _marker);

        sut.CreateBlockRules(new[] { "Discord", "chrome" });
        sut.EnableBlockRules();

        Assert.Empty(fake.RunCalls); // never armed → no nft at all (full-tunnel-only)
    }

    [Fact]
    public void FullTunnel_emptyList_arms_and_Enable_loads_ruleset_with_server_ip()
    {
        WriteConfig("104.194.156.93");
        var fake = OkRunner();
        var sut = new LinuxFirewallManager(null, fake, _cfg, _marker);

        sut.CreateBlockRules(Array.Empty<string>());
        sut.EnableBlockRules();

        var load = fake.RunCalls.FirstOrDefault(c =>
            c.ExecutablePath == "/usr/bin/sudo" && c.Arguments.Contains("nft") && c.Arguments.Contains("-f"));
        Assert.NotNull(load);
        var rules = File.ReadAllText(load!.Arguments.Last());
        Assert.Contains("policy drop", rules);
        Assert.Contains("104.194.156.93", rules); // server pass → sing-box can reconnect
    }

    [Fact]
    public void Enable_when_load_fails_stays_unloaded_and_Disable_is_noop()
    {
        WriteConfig("9.9.9.9");
        var fake = new FakeProcessRunner();
        fake.OnRun(r => r.ExecutablePath == "/usr/bin/sudo" && r.Arguments.Contains("-f"), Fail());
        fake.OnRun(r => r.ExecutablePath == "/usr/bin/sudo", Ok());
        var sut = new LinuxFirewallManager(null, fake, _cfg, _marker);

        sut.CreateBlockRules(Array.Empty<string>());
        sut.EnableBlockRules(); // fail-safe: load failed → NOT blocking, no brick

        Assert.False(File.Exists(_marker)); // never engaged → no sentinel

        var before = fake.RunCalls.Count;
        sut.DisableBlockRules();
        Assert.Equal(before, fake.RunCalls.Count); // not loaded → Disable no-op
    }

    [Fact]
    public void Disable_after_load_deletes_table()
    {
        WriteConfig("9.9.9.9");
        var fake = OkRunner();
        var sut = new LinuxFirewallManager(null, fake, _cfg, _marker);
        sut.CreateBlockRules(Array.Empty<string>());
        sut.EnableBlockRules();

        sut.DisableBlockRules();

        Assert.Contains(fake.RunCalls, c =>
            c.Arguments.Contains("delete") && c.Arguments.Contains("table") && c.Arguments.Contains("vpnrouter_ks"));
    }

    [Fact]
    public void Dispose_after_load_deletes_table_antibrick()
    {
        WriteConfig("9.9.9.9");
        var fake = OkRunner();
        var sut = new LinuxFirewallManager(null, fake, _cfg, _marker);
        sut.CreateBlockRules(Array.Empty<string>());
        sut.EnableBlockRules();

        sut.Dispose();

        Assert.Contains(fake.RunCalls, c =>
            c.Arguments.Contains("delete") && c.Arguments.Contains("table") && c.Arguments.Contains("vpnrouter_ks"));
    }

    [Fact]
    public void Disable_without_load_is_noop()
    {
        WriteConfig("9.9.9.9");
        var fake = OkRunner();
        var sut = new LinuxFirewallManager(null, fake, _cfg, _marker);
        sut.CreateBlockRules(Array.Empty<string>()); // armed but never enabled

        sut.DisableBlockRules();

        Assert.DoesNotContain(fake.RunCalls, c => c.Arguments.Contains("delete"));
    }

    [Fact]
    public void BuildRuleset_drops_then_passes_loopback_lan_and_servers()
    {
        var rules = LinuxFirewallManager.BuildRuleset(new List<string> { "1.2.3.4" });
        Assert.Contains("add table inet vpnrouter_ks", rules);
        Assert.Contains("flush table inet vpnrouter_ks", rules);
        Assert.Contains("policy drop", rules);
        Assert.Contains("oif \"lo\" accept", rules);
        Assert.Contains("10.0.0.0/8", rules);
        Assert.Contains("192.168.0.0/16", rules);
        Assert.Contains("1.2.3.4", rules);
    }

    [Fact]
    public void BuildRuleset_omits_server_accept_line_when_no_ipv4_servers()
    {
        var rules = LinuxFirewallManager.BuildRuleset(new List<string>());
        Assert.Contains("policy drop", rules);
        // No "ip daddr { } accept" with an empty server set (would be invalid nft).
        Assert.DoesNotContain("ip daddr {  }", rules);
    }

    [Fact]
    public void ReadServerIps_skips_hostnames_keeps_ips_when_no_resolver_hit()
    {
        File.WriteAllText(_cfg, @"{ ""outbounds"": [
            { ""type"": ""vless"", ""server"": ""example.com"" },
            { ""type"": ""vless"", ""server"": ""5.6.7.8"" } ] }");
        var fake = OkRunner();
        // resolver returns nothing for the hostname → only the literal IP survives
        var sut = new LinuxFirewallManager(null, fake, _cfg, _marker,
            hostResolver: _ => Array.Empty<string>());

        sut.CreateBlockRules(Array.Empty<string>());
        sut.EnableBlockRules();

        var rules = File.ReadAllText(LoadedRulesetFile(fake));
        Assert.Contains("5.6.7.8", rules);
        Assert.DoesNotContain("example.com", rules);
    }

    [Fact]
    public void ReadServerIps_resolves_hostname_server_to_ip()
    {
        // Hostname server → must be RESOLVED into the pass-list, else the
        // kill-switch blocks crash-reconnect → bricked host.
        File.WriteAllText(_cfg, @"{ ""outbounds"": [
            { ""type"": ""vless"", ""server"": ""proxy.example.com"" } ] }");
        var fake = OkRunner();
        var sut = new LinuxFirewallManager(null, fake, _cfg, _marker,
            hostResolver: h => h == "proxy.example.com"
                ? new[] { "203.0.113.10" }
                : (IReadOnlyList<string>)Array.Empty<string>());

        sut.CreateBlockRules(Array.Empty<string>());
        sut.EnableBlockRules();

        var rules = File.ReadAllText(LoadedRulesetFile(fake));
        Assert.Contains("203.0.113.10", rules);
        Assert.DoesNotContain("proxy.example.com", rules);
    }

    [Fact]
    public void Enable_writes_engaged_marker_Disable_clears_it()
    {
        WriteConfig("9.9.9.9");
        var sut = new LinuxFirewallManager(null, OkRunner(), _cfg, _marker);
        sut.CreateBlockRules(Array.Empty<string>());

        sut.EnableBlockRules();
        Assert.True(File.Exists(_marker));   // engaged → crash-recovery sentinel present

        sut.DisableBlockRules();
        Assert.False(File.Exists(_marker));  // clean teardown → sentinel gone
    }

    [Fact]
    public void CleanupOrphanedRules_with_marker_deletes_table_and_clears_marker()
    {
        File.WriteAllText(_marker, "engaged"); // simulate a prior hard kill while engaged
        var fake = OkRunner();
        var sut = new LinuxFirewallManager(null, fake, _cfg, _marker);

        sut.CleanupOrphanedRules(null);

        Assert.Contains(fake.RunCalls, c =>
            c.Arguments.Contains("delete") && c.Arguments.Contains("table") && c.Arguments.Contains("vpnrouter_ks"));
        Assert.False(File.Exists(_marker));
    }

    [Fact]
    public void CleanupOrphanedRules_without_marker_is_noop()
    {
        var fake = OkRunner();
        var sut = new LinuxFirewallManager(null, fake, _cfg, _marker); // no marker file

        sut.CleanupOrphanedRules(null);

        Assert.Empty(fake.RunCalls); // a normal launch must never touch nft
    }
}
