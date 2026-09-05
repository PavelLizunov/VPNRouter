using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
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
    private readonly string _testDir =
        Path.Combine(Path.GetTempPath(), "vpnrouter-lfw-test-" + Guid.NewGuid().ToString("N"));
    private readonly string _cfg;
    private readonly string _marker;
    private readonly string _ruleset;

    public LinuxFirewallManagerTests()
    {
        Directory.CreateDirectory(_testDir);
        _cfg = Path.Combine(_testDir, "current.json");
        _marker = Path.Combine(_testDir, "engaged.marker");
        _ruleset = Path.Combine(_testDir, "ruleset.conf");
    }

    public void Dispose()
    {
        try { if (File.Exists(_cfg)) File.Delete(_cfg); } catch { }
        try { if (File.Exists(_marker)) File.Delete(_marker); } catch { }
        try { if (File.Exists(_ruleset)) File.Delete(_ruleset); } catch { }
        try { if (Directory.Exists(_testDir)) Directory.Delete(_testDir, true); } catch { }
    }

    private static ProcessResult Ok(string stdout = "", string stderr = "") =>
        new ProcessResult(0, stdout, stderr, TimeSpan.Zero, false);
    private static ProcessResult Fail(string stderr = "sudo: a password is required") =>
        new ProcessResult(1, "", stderr, TimeSpan.Zero, false);

    private static ProcessResult NftTablesPresent() =>
        Ok(@"{""nftables"":[{""metainfo"":{""version"":""1.0.2""}},{""table"":{""family"":""inet"",""name"":""vpnrouter_ks""}}]}");

    private static ProcessResult NftTablesAbsent() =>
        Ok(@"{""nftables"":[{""metainfo"":{""version"":""1.0.2""}},{""table"":{""family"":""ip"",""name"":""filter""}}]}");

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

    private LinuxFirewallManager CreateSut(
        FakeProcessRunner runner,
        Func<string, IReadOnlyList<string>>? hostResolver = null,
        string? rulesetPath = null) =>
        new(null, runner, _cfg, _marker, hostResolver, rulesetPath ?? _ruleset);

    private static string LoadedRulesetFile(FakeProcessRunner f) =>
        f.RunCalls.First(c => c.ExecutablePath == "/usr/bin/sudo" && c.Arguments.Contains("-f")).Arguments.Last();

    [Fact]
    public void SplitTunnel_disarms_and_Enable_is_noop()
    {
        WriteConfig("9.9.9.9");
        var fake = OkRunner();
        var sut = CreateSut(fake);

        sut.CreateBlockRules(new[] { "Discord", "chrome" }, isFullTunnel: false);
        sut.EnableBlockRules();

        Assert.Empty(fake.RunCalls); // never armed → no nft at all (full-tunnel-only)
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
        var sut = CreateSut(fake);

        sut.CreateBlockRules(Array.Empty<string>(), isFullTunnel: false); // split scan returned nothing
        sut.EnableBlockRules();

        Assert.Empty(fake.RunCalls); // must NOT global-block a split-tunnel user
    }

    [Fact]
    public void FullTunnel_emptyList_arms_and_Enable_loads_ruleset_with_server_ip()
    {
        WriteConfig("104.194.156.93");
        var fake = OkRunner();
        var sut = CreateSut(fake);

        sut.CreateBlockRules(Array.Empty<string>(), isFullTunnel: true);
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
        var sut = CreateSut(fake);

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
        var sut = CreateSut(fake);
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
        var sut = CreateSut(fake);
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
        var sut = CreateSut(fake);
        sut.CreateBlockRules(Array.Empty<string>()); // armed but never enabled

        sut.DisableBlockRules();

        Assert.DoesNotContain(fake.RunCalls, c => c.Arguments.Contains("delete"));
    }

    [Fact]
    public void Dispose_without_load_does_not_delete_and_is_noop()
    {
        WriteConfig("9.9.9.9");
        var fake = OkRunner();
        var sut = CreateSut(fake);
        sut.CreateBlockRules(Array.Empty<string>()); // armed but never enabled

        sut.Dispose();

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
    public void BuildRuleset_MixedFamily_EmitsBoth()
    {
        var rules = LinuxFirewallManager.BuildRuleset(new List<string> { "1.2.3.4", "2001:db8::1" });

        // Unchanged IPv4 rule + new IPv6 ip6-daddr rule; the IPv6 literal must
        // not ride a malformed IPv4-family rule.
        Assert.Contains("add rule inet vpnrouter_ks output ip daddr { 1.2.3.4 } accept", rules);
        Assert.Contains("add rule inet vpnrouter_ks output ip6 daddr { 2001:db8::1 } accept", rules);
        Assert.DoesNotContain("ip daddr { 2001:db8::1 }", rules);
    }

    [Fact]
    public void ReadServerIps_skips_hostnames_keeps_ips_when_no_resolver_hit()
    {
        File.WriteAllText(_cfg, @"{ ""outbounds"": [
            { ""type"": ""vless"", ""server"": ""example.com"" },
            { ""type"": ""vless"", ""server"": ""5.6.7.8"" } ] }");
        var fake = OkRunner();
        // resolver returns nothing for the hostname → only the literal IP survives
        var sut = CreateSut(fake, hostResolver: _ => Array.Empty<string>());

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
        var sut = CreateSut(fake,
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
        var sut = CreateSut(OkRunner());
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
        var sut = CreateSut(fake);

        sut.CleanupOrphanedRules(null);

        Assert.Contains(fake.RunCalls, c =>
            c.Arguments.Contains("delete") && c.Arguments.Contains("table") && c.Arguments.Contains("vpnrouter_ks"));
        Assert.False(File.Exists(_marker));
    }

    [Fact]
    public void CleanupOrphanedRules_without_marker_is_noop()
    {
        var fake = OkRunner();
        var sut = CreateSut(fake); // no marker file

        sut.CleanupOrphanedRules(null);

        Assert.Empty(fake.RunCalls); // a normal launch must never touch nft
    }

    [Fact]
    public void Enable_WritesRulesetToConfiguredPath_NotSharedTemp()
    {
        // FW-02: verify that LinuxFirewallManager writes rulesets into private AppPaths.DataDir
        // or the explicitly configured ruleset path, never world-writable /tmp.
        WriteConfig("9.9.9.9");
        var customRuleset = Path.Combine(_testDir, "custom-ruleset-" + Guid.NewGuid().ToString("N") + ".conf");
        var fake = OkRunner();
        var sut = CreateSut(fake, rulesetPath: customRuleset);

        try
        {
            sut.CreateBlockRules(Array.Empty<string>());
            sut.EnableBlockRules();

            Assert.True(File.Exists(customRuleset));
            Assert.Contains(fake.RunCalls, c => c.Arguments.Contains(customRuleset));
            Assert.DoesNotContain(fake.RunCalls, c => c.Arguments.Any(a => a.Contains("/tmp/vpnrouter-nft-killswitch.conf")));
        }
        finally
        {
            try { if (File.Exists(customRuleset)) File.Delete(customRuleset); } catch { }
        }
    }

    [Fact]
    public void Successful_engage_failed_Disable_keeps_marker_and_allows_retry()
    {
        WriteConfig("9.9.9.9");
        var fake = new FakeProcessRunner();
        var allowDelete = false;
        fake.OnRun(r => r.ExecutablePath == "/usr/bin/sudo" && r.Arguments.Contains("-f"), Ok());
        fake.OnRun(r => r.ExecutablePath == "/usr/bin/sudo" && r.Arguments.Contains("delete"),
            _ => Task.FromResult(allowDelete ? Ok() : Fail("sudoers denied")));
        fake.OnRun(r => r.ExecutablePath == "/usr/bin/sudo" && r.Arguments.Contains("list"), Fail("sudoers denied"));
        var sut = CreateSut(fake);

        sut.CreateBlockRules(Array.Empty<string>());
        sut.EnableBlockRules();
        Assert.True(File.Exists(_marker));

        // Failed Disable must retain marker and loaded recovery state
        sut.DisableBlockRules();
        Assert.True(File.Exists(_marker));

        // Retry after recovery successfully removes table and marker
        allowDelete = true;
        sut.DisableBlockRules();
        Assert.False(File.Exists(_marker));
        Assert.Equal(2, fake.RunCalls.Count(c => c.Arguments.Contains("delete")));
    }

    [Fact]
    public void Orphan_hard_crash_marker_failed_delete_keeps_marker_and_later_same_instance_recovered_command_removes()
    {
        File.WriteAllText(_marker, "engaged");
        var fake = new FakeProcessRunner();
        var allowDelete = false;
        fake.OnRun(r => r.ExecutablePath == "/usr/bin/sudo" && r.Arguments.Contains("delete"),
            _ => Task.FromResult(allowDelete ? Ok() : Fail("sudoers denied")));
        fake.OnRun(r => r.ExecutablePath == "/usr/bin/sudo" && r.Arguments.Contains("list"), Fail("sudoers denied"));
        var sut = CreateSut(fake);

        // Failed delete during orphan cleanup must not delete marker
        sut.CleanupOrphanedRules(null);
        Assert.True(File.Exists(_marker));

        // Later recovered command on the same instance succeeds and clears marker
        allowDelete = true;
        sut.CleanupOrphanedRules(null);
        Assert.False(File.Exists(_marker));
        Assert.Equal(2, fake.RunCalls.Count(c => c.Arguments.Contains("delete")));
    }

    [Fact]
    public void DeleteAll_failure_retains_marker_and_state()
    {
        WriteConfig("9.9.9.9");
        var fake = new FakeProcessRunner();
        var allowDelete = false;
        fake.OnRun(r => r.ExecutablePath == "/usr/bin/sudo" && r.Arguments.Contains("-f"), Ok());
        fake.OnRun(r => r.ExecutablePath == "/usr/bin/sudo" && r.Arguments.Contains("delete"),
            _ => Task.FromResult(allowDelete ? Ok() : Fail("permission denied")));
        fake.OnRun(r => r.ExecutablePath == "/usr/bin/sudo" && r.Arguments.Contains("list"), Fail("permission denied"));
        var sut = CreateSut(fake);

        sut.CreateBlockRules(Array.Empty<string>());
        sut.EnableBlockRules();
        Assert.True(File.Exists(_marker));

        // Failed DeleteAllRules keeps marker for future recovery
        sut.DeleteAllRules();
        Assert.True(File.Exists(_marker));

        // Repeat DeleteAllRules succeeds and clears marker
        allowDelete = true;
        sut.DeleteAllRules();
        Assert.False(File.Exists(_marker));
        Assert.Equal(2, fake.RunCalls.Count(c => c.Arguments.Contains("delete")));
    }

    [Fact]
    public void Dispose_retry_on_prior_failure_cleans_up_when_recovered()
    {
        WriteConfig("9.9.9.9");
        var fake = new FakeProcessRunner();
        var allowDelete = false;
        fake.OnRun(r => r.ExecutablePath == "/usr/bin/sudo" && r.Arguments.Contains("-f"), Ok());
        fake.OnRun(r => r.ExecutablePath == "/usr/bin/sudo" && r.Arguments.Contains("delete"),
            _ => Task.FromResult(allowDelete ? Ok() : Fail("permission denied")));
        fake.OnRun(r => r.ExecutablePath == "/usr/bin/sudo" && r.Arguments.Contains("list"), Fail("permission denied"));
        var sut = CreateSut(fake);

        sut.CreateBlockRules(Array.Empty<string>());
        sut.EnableBlockRules();
        Assert.True(File.Exists(_marker));

        // First Dispose fails delete → retains marker and _loaded
        sut.Dispose();
        Assert.True(File.Exists(_marker));

        // Repeat Dispose with recovered command cleans up and clears marker
        allowDelete = true;
        sut.Dispose();
        Assert.False(File.Exists(_marker));
        Assert.Equal(2, fake.RunCalls.Count(c => c.Arguments.Contains("delete")));

        // Subsequent Dispose is idempotent no-op
        sut.Dispose();
        Assert.Equal(2, fake.RunCalls.Count(c => c.Arguments.Contains("delete")));
    }

    [Fact]
    public void TimedOut_exit0_not_success()
    {
        WriteConfig("9.9.9.9");
        var timedOutOk = new ProcessResult(0, "", "", TimeSpan.FromSeconds(10), TimedOut: true);
        var fake = new FakeProcessRunner();
        var loadShouldTimeout = true;
        fake.OnRun(r => r.ExecutablePath == "/usr/bin/sudo" && r.Arguments.Contains("-f"),
            _ => Task.FromResult(loadShouldTimeout ? timedOutOk : Ok()));
        fake.OnRun(r => r.ExecutablePath == "/usr/bin/sudo" && r.Arguments.Contains("delete"), timedOutOk);
        fake.OnRun(r => r.ExecutablePath == "/usr/bin/sudo" && r.Arguments.Contains("list"), timedOutOk);
        var sut = CreateSut(fake);

        sut.CreateBlockRules(Array.Empty<string>());

        // 1. Enable with timed-out exit 0 must not engage or write marker
        sut.EnableBlockRules();
        Assert.False(File.Exists(_marker));

        // 2. Successful Enable followed by timed-out exit 0 on Disable must retain marker
        loadShouldTimeout = false;
        sut.EnableBlockRules();
        Assert.True(File.Exists(_marker));

        sut.DisableBlockRules();
        Assert.True(File.Exists(_marker));

        // 3. Orphan cleanup with timed-out exit 0 must retain marker
        sut.CleanupOrphanedRules(null);
        Assert.True(File.Exists(_marker));
    }

    [Fact]
    public void Delete_fails_but_inventory_absent_proves_absent_and_succeeds()
    {
        WriteConfig("9.9.9.9");
        var fake = new FakeProcessRunner();
        fake.OnRun(r => r.ExecutablePath == "/usr/bin/sudo" && r.Arguments.Contains("-f"), Ok());
        fake.OnRun(r => r.ExecutablePath == "/usr/bin/sudo" && r.Arguments.Contains("delete"), Fail("table not found"));
        fake.OnRun(r => r.ExecutablePath == "/usr/bin/sudo" && r.Arguments.Contains("list"), NftTablesAbsent());
        var sut = CreateSut(fake);

        sut.CreateBlockRules(Array.Empty<string>());
        sut.EnableBlockRules();
        Assert.True(File.Exists(_marker));

        sut.DisableBlockRules();

        // Proved absent via inventory -> success, marker deleted
        Assert.False(File.Exists(_marker));
        Assert.Contains(fake.RunCalls, c => c.Arguments.Contains("delete"));
        Assert.Contains(fake.RunCalls, c => c.Arguments.Contains("-j") && c.Arguments.Contains("list") && c.Arguments.Contains("tables"));
    }

    [Fact]
    public void DeleteAll_delete_fails_but_inventory_absent_clears_marker()
    {
        WriteConfig("9.9.9.9");
        var fake = new FakeProcessRunner();
        fake.OnRun(r => r.ExecutablePath == "/usr/bin/sudo" && r.Arguments.Contains("-f"), Ok());
        fake.OnRun(r => r.ExecutablePath == "/usr/bin/sudo" && r.Arguments.Contains("delete"), Fail("table not found"));
        fake.OnRun(r => r.ExecutablePath == "/usr/bin/sudo" && r.Arguments.Contains("list"), NftTablesAbsent());
        var sut = CreateSut(fake);

        sut.CreateBlockRules(Array.Empty<string>());
        sut.EnableBlockRules();
        Assert.True(File.Exists(_marker));

        sut.DeleteAllRules();

        Assert.False(File.Exists(_marker));
    }

    [Fact]
    public void OrphanCleanup_delete_fails_but_inventory_absent_clears_marker()
    {
        File.WriteAllText(_marker, "engaged");
        var fake = new FakeProcessRunner();
        fake.OnRun(r => r.ExecutablePath == "/usr/bin/sudo" && r.Arguments.Contains("delete"), Fail("table not found"));
        fake.OnRun(r => r.ExecutablePath == "/usr/bin/sudo" && r.Arguments.Contains("list"), NftTablesAbsent());
        var sut = CreateSut(fake);

        sut.CleanupOrphanedRules(null);

        Assert.False(File.Exists(_marker));
    }

    [Fact]
    public void Delete_fails_but_inventory_empty_nftables_array_proves_absent_and_succeeds()
    {
        WriteConfig("9.9.9.9");
        var fake = new FakeProcessRunner();
        fake.OnRun(r => r.ExecutablePath == "/usr/bin/sudo" && r.Arguments.Contains("-f"), Ok());
        fake.OnRun(r => r.ExecutablePath == "/usr/bin/sudo" && r.Arguments.Contains("delete"), Fail("table not found"));
        fake.OnRun(r => r.ExecutablePath == "/usr/bin/sudo" && r.Arguments.Contains("list"), Ok(@"{""nftables"":[]}"));
        var sut = CreateSut(fake);

        sut.CreateBlockRules(Array.Empty<string>());
        sut.EnableBlockRules();
        Assert.True(File.Exists(_marker));

        sut.DisableBlockRules();

        Assert.False(File.Exists(_marker));
    }

    [Fact]
    public void Delete_fails_and_inventory_target_present_retains_failure()
    {
        WriteConfig("9.9.9.9");
        var fake = new FakeProcessRunner();
        fake.OnRun(r => r.ExecutablePath == "/usr/bin/sudo" && r.Arguments.Contains("-f"), Ok());
        fake.OnRun(r => r.ExecutablePath == "/usr/bin/sudo" && r.Arguments.Contains("delete"), Fail("delete failed"));
        fake.OnRun(r => r.ExecutablePath == "/usr/bin/sudo" && r.Arguments.Contains("list"), NftTablesPresent());
        var sut = CreateSut(fake);

        sut.CreateBlockRules(Array.Empty<string>());
        sut.EnableBlockRules();
        Assert.True(File.Exists(_marker));

        sut.DisableBlockRules();

        // Target table is present -> failure retained, marker kept
        Assert.True(File.Exists(_marker));
    }

    [Fact]
    public void Delete_fails_and_inventory_error_retains_failure()
    {
        WriteConfig("9.9.9.9");
        var fake = new FakeProcessRunner();
        fake.OnRun(r => r.ExecutablePath == "/usr/bin/sudo" && r.Arguments.Contains("-f"), Ok());
        fake.OnRun(r => r.ExecutablePath == "/usr/bin/sudo" && r.Arguments.Contains("delete"), Fail("delete failed"));
        fake.OnRun(r => r.ExecutablePath == "/usr/bin/sudo" && r.Arguments.Contains("list"), Fail("sudo: command failed"));
        var sut = CreateSut(fake);

        sut.CreateBlockRules(Array.Empty<string>());
        sut.EnableBlockRules();
        Assert.True(File.Exists(_marker));

        sut.DisableBlockRules();

        // Inventory command error -> failure retained, marker kept
        Assert.True(File.Exists(_marker));
    }

    [Fact]
    public void Delete_fails_and_inventory_malformed_json_retains_failure()
    {
        WriteConfig("9.9.9.9");
        var fake = new FakeProcessRunner();
        fake.OnRun(r => r.ExecutablePath == "/usr/bin/sudo" && r.Arguments.Contains("-f"), Ok());
        fake.OnRun(r => r.ExecutablePath == "/usr/bin/sudo" && r.Arguments.Contains("delete"), Fail("delete failed"));
        fake.OnRun(r => r.ExecutablePath == "/usr/bin/sudo" && r.Arguments.Contains("list"), Ok("{ invalid json"));
        var sut = CreateSut(fake);

        sut.CreateBlockRules(Array.Empty<string>());
        sut.EnableBlockRules();
        Assert.True(File.Exists(_marker));

        sut.DisableBlockRules();

        // Malformed JSON -> failure retained, marker kept
        Assert.True(File.Exists(_marker));
    }

    [Fact]
    public void Delete_fails_and_inventory_missing_nftables_array_retains_failure()
    {
        WriteConfig("9.9.9.9");
        var fake = new FakeProcessRunner();
        fake.OnRun(r => r.ExecutablePath == "/usr/bin/sudo" && r.Arguments.Contains("-f"), Ok());
        fake.OnRun(r => r.ExecutablePath == "/usr/bin/sudo" && r.Arguments.Contains("delete"), Fail("delete failed"));
        fake.OnRun(r => r.ExecutablePath == "/usr/bin/sudo" && r.Arguments.Contains("list"), Ok(@"{""tables"":[]}"));
        var sut = CreateSut(fake);

        sut.CreateBlockRules(Array.Empty<string>());
        sut.EnableBlockRules();
        Assert.True(File.Exists(_marker));

        sut.DisableBlockRules();

        // Missing nftables array -> failure retained, marker kept
        Assert.True(File.Exists(_marker));
    }

    [Fact]
    public void Delete_fails_and_inventory_timedout_retains_failure()
    {
        WriteConfig("9.9.9.9");
        var fake = new FakeProcessRunner();
        var timedOut = new ProcessResult(0, "", "", TimeSpan.FromSeconds(10), TimedOut: true);
        fake.OnRun(r => r.ExecutablePath == "/usr/bin/sudo" && r.Arguments.Contains("-f"), Ok());
        fake.OnRun(r => r.ExecutablePath == "/usr/bin/sudo" && r.Arguments.Contains("delete"), Fail("delete failed"));
        fake.OnRun(r => r.ExecutablePath == "/usr/bin/sudo" && r.Arguments.Contains("list"), timedOut);
        var sut = CreateSut(fake);

        sut.CreateBlockRules(Array.Empty<string>());
        sut.EnableBlockRules();
        Assert.True(File.Exists(_marker));

        sut.DisableBlockRules();

        // Timed out inventory -> failure retained, marker kept
        Assert.True(File.Exists(_marker));
    }

    [Theory]
    // Missing family (e.g. {nftables:[{table:{name:'vpnrouter_ks'}}]})
    [InlineData(@"{""nftables"":[{""table"":{""name"":""vpnrouter_ks""}}]}")]
    // Missing name
    [InlineData(@"{""nftables"":[{""table"":{""family"":""inet""}}]}")]
    // Wrong family type (number)
    [InlineData(@"{""nftables"":[{""table"":{""family"":123,""name"":""vpnrouter_ks""}}]}")]
    // Wrong name type (number)
    [InlineData(@"{""nftables"":[{""table"":{""family"":""inet"",""name"":456}}]}")]
    // Wrong family type (null)
    [InlineData(@"{""nftables"":[{""table"":{""family"":null,""name"":""vpnrouter_ks""}}]}")]
    // Wrong name type (null)
    [InlineData(@"{""nftables"":[{""table"":{""family"":""inet"",""name"":null}}]}")]
    // Empty family string
    [InlineData(@"{""nftables"":[{""table"":{""family"":"""",""name"":""vpnrouter_ks""}}]}")]
    // Whitespace family string
    [InlineData(@"{""nftables"":[{""table"":{""family"":""   "",""name"":""vpnrouter_ks""}}]}")]
    // Empty name string
    [InlineData(@"{""nftables"":[{""table"":{""family"":""inet"",""name"":""""}}]}")]
    // Whitespace name string
    [InlineData(@"{""nftables"":[{""table"":{""family"":""inet"",""name"":""   ""}}]}")]
    // Wrong table type (string instead of object)
    [InlineData(@"{""nftables"":[{""table"":""not_an_object""}]}")]
    // Wrong table type (null instead of object)
    [InlineData(@"{""nftables"":[{""table"":null}]}")]
    // Wrong table type (array instead of object)
    [InlineData(@"{""nftables"":[{""table"":[]}]}")]
    // Unknown node type
    [InlineData(@"{""nftables"":[{""unknown"":{}}]}")]
    // Non-object entry in nftables array
    [InlineData(@"{""nftables"":[123]}")]
    // Non-object metainfo
    [InlineData(@"{""nftables"":[{""metainfo"":""not_an_object""}]}")]
    public void Delete_fails_and_inventory_malformed_entry_retains_failure(string malformedInventoryJson)
    {
        WriteConfig("9.9.9.9");
        var fake = new FakeProcessRunner();
        fake.OnRun(r => r.ExecutablePath == "/usr/bin/sudo" && r.Arguments.Contains("-f"), Ok());
        fake.OnRun(r => r.ExecutablePath == "/usr/bin/sudo" && r.Arguments.Contains("delete"), Fail("delete failed"));
        fake.OnRun(r => r.ExecutablePath == "/usr/bin/sudo" && r.Arguments.Contains("list"), Ok(malformedInventoryJson));
        var sut = CreateSut(fake);

        sut.CreateBlockRules(Array.Empty<string>());
        sut.EnableBlockRules();
        Assert.True(File.Exists(_marker));

        sut.DisableBlockRules();

        // Malformed inventory entry -> failure retained, marker kept
        Assert.True(File.Exists(_marker));
    }

    [Theory]
    [InlineData(@"{""nftables"":[{""table"":{""family"":""ip"",""name"":""filter""}}]}")]
    [InlineData(@"{""nftables"":[{""metainfo"":{""version"":""1.0.2""}}]}")]
    [InlineData(@"{""nftables"":[{""metainfo"":{""version"":""1.0.2""}},{""table"":{""family"":""ip"",""name"":""filter""}}]}")]
    public void Delete_fails_but_inventory_valid_other_tables_or_metainfo_proves_absent_and_succeeds(string validInventoryJson)
    {
        WriteConfig("9.9.9.9");
        var fake = new FakeProcessRunner();
        fake.OnRun(r => r.ExecutablePath == "/usr/bin/sudo" && r.Arguments.Contains("-f"), Ok());
        fake.OnRun(r => r.ExecutablePath == "/usr/bin/sudo" && r.Arguments.Contains("delete"), Fail("table not found"));
        fake.OnRun(r => r.ExecutablePath == "/usr/bin/sudo" && r.Arguments.Contains("list"), Ok(validInventoryJson));
        var sut = CreateSut(fake);

        sut.CreateBlockRules(Array.Empty<string>());
        sut.EnableBlockRules();
        Assert.True(File.Exists(_marker));

        sut.DisableBlockRules();

        Assert.False(File.Exists(_marker));
    }

    [Fact]
    public void ReadServerIps_PeerOnlyWireGuard_LiteralIpv4AndIpv6_PresentInRules()
    {
        File.WriteAllText(_cfg, @"{
            ""endpoints"": [
                {
                    ""type"": ""wireguard"",
                    ""tag"": ""proxy"",
                    ""address"": [ ""10.13.13.2/32"" ],
                    ""peers"": [
                        { ""address"": ""198.51.100.1"", ""port"": 51820 },
                        { ""address"": ""2001:db8::10"", ""port"": 51820 }
                    ]
                }
            ]
        }");
        var fake = OkRunner();
        var sut = CreateSut(fake);

        sut.CreateBlockRules(Array.Empty<string>());
        sut.EnableBlockRules();

        var rules = File.ReadAllText(LoadedRulesetFile(fake));
        Assert.Contains("add rule inet vpnrouter_ks output ip daddr { 198.51.100.1 } accept", rules);
        Assert.Contains("add rule inet vpnrouter_ks output ip6 daddr { 2001:db8::10 } accept", rules);
        Assert.DoesNotContain("10.13.13.2", rules);

        var extracted = sut.ReadServerIps();
        Assert.Contains("198.51.100.1", extracted);
        Assert.Contains("2001:db8::10", extracted);
    }

    [Fact]
    public void ReadServerIps_HostnameResolver_GivesAAndAaaaCanonicalDedupe()
    {
        File.WriteAllText(_cfg, @"{
            ""outbounds"": [
                { ""type"": ""vless"", ""server"": ""wg.example.com"" }
            ],
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
        var sut = CreateSut(fake, hostResolver: h => h == "wg.example.com"
            ? new[] { "198.51.100.2", "2001:0db8::1", "198.51.100.2", "2001:DB8::1" }
            : Array.Empty<string>());

        sut.CreateBlockRules(Array.Empty<string>());
        sut.EnableBlockRules();

        var rules = File.ReadAllText(LoadedRulesetFile(fake));
        Assert.Contains("add rule inet vpnrouter_ks output ip daddr { 198.51.100.2 } accept", rules);
        Assert.Contains("add rule inet vpnrouter_ks output ip6 daddr { 2001:db8::1 } accept", rules);
        Assert.DoesNotContain("2001:0db8::1", rules);
        Assert.DoesNotContain("2001:DB8::1", rules);

        var extracted = sut.ReadServerIps();
        Assert.Equal(new[] { "198.51.100.2", "2001:db8::1" }, extracted);
    }

    [Fact]
    public void ReadServerIps_LocalInterfaceCidrAndAllowedIps_AbsentFromRules()
    {
        File.WriteAllText(_cfg, @"{
            ""endpoints"": [
                {
                    ""type"": ""wireguard"",
                    ""address"": [ ""10.13.13.2/32"", ""192.168.200.5/24"", ""fd00::2/128"" ],
                    ""peers"": [
                        {
                            ""address"": ""198.51.100.3"",
                            ""allowed_ips"": [ ""0.0.0.0/0"", ""::/0"", ""10.0.0.0/8"" ]
                        }
                    ]
                }
            ]
        }");
        var fake = OkRunner();
        var sut = CreateSut(fake);

        sut.CreateBlockRules(Array.Empty<string>());
        sut.EnableBlockRules();

        var rules = File.ReadAllText(LoadedRulesetFile(fake));
        Assert.Contains("198.51.100.3", rules);
        Assert.DoesNotContain("10.13.13.2", rules);
        Assert.DoesNotContain("192.168.200.5", rules);
        Assert.DoesNotContain("fd00::2", rules);
        Assert.DoesNotContain("0.0.0.0/0", rules);
        Assert.DoesNotContain("::/0", rules);

        var extracted = sut.ReadServerIps();
        Assert.Equal(new[] { "198.51.100.3" }, extracted);
    }

    [Fact]
    public void ReadServerIps_UnknownEndpointType_ExcludedFromRules()
    {
        File.WriteAllText(_cfg, @"{
            ""endpoints"": [
                {
                    ""type"": ""tailscale"",
                    ""peers"": [
                        { ""address"": ""203.0.113.99"" }
                    ]
                },
                {
                    ""type"": ""unknown_type"",
                    ""peers"": [
                        { ""address"": ""203.0.113.100"" }
                    ]
                },
                {
                    ""type"": ""wireguard"",
                    ""peers"": [
                        { ""address"": ""198.51.100.4"" }
                    ]
                }
            ]
        }");
        var fake = OkRunner();
        var sut = CreateSut(fake);

        sut.CreateBlockRules(Array.Empty<string>());
        sut.EnableBlockRules();

        var rules = File.ReadAllText(LoadedRulesetFile(fake));
        Assert.Contains("198.51.100.4", rules);
        Assert.DoesNotContain("203.0.113.99", rules);
        Assert.DoesNotContain("203.0.113.100", rules);

        var extracted = sut.ReadServerIps();
        Assert.Equal(new[] { "198.51.100.4" }, extracted);
    }

    [Fact]
    public void ReadServerIps_MalformedSiblingsAndThrowingResolver_DoNotLoseLaterPeers()
    {
        File.WriteAllText(_cfg, @"{
            ""outbounds"": [
                { ""type"": ""vless"", ""server"": 123 },
                { ""type"": ""vless"" }
            ],
            ""endpoints"": [
                { ""type"": ""wireguard"", ""peers"": ""not-an-array"" },
                { ""not_an_object"": 42 },
                {
                    ""type"": ""wireguard"",
                    ""peers"": [
                        null,
                        123,
                        { ""address"": null },
                        { ""address"": """" },
                        { ""address"": ""throws.example.com"" },
                        { ""address"": ""198.51.100.5"" }
                    ]
                },
                {
                    ""type"": ""wireguard"",
                    ""peers"": [
                        { ""address"": ""2001:db8::5"" }
                    ]
                }
            ]
        }");
        var fake = OkRunner();
        var sut = CreateSut(fake, hostResolver: h =>
        {
            if (h == "throws.example.com")
                throw new InvalidOperationException("DNS resolve failure simulated");
            return Array.Empty<string>();
        });

        sut.CreateBlockRules(Array.Empty<string>());
        sut.EnableBlockRules();

        var rules = File.ReadAllText(LoadedRulesetFile(fake));
        Assert.Contains("198.51.100.5", rules);
        Assert.Contains("2001:db8::5", rules);

        var extracted = sut.ReadServerIps();
        Assert.Equal(new[] { "198.51.100.5", "2001:db8::5" }, extracted);
    }

    [Fact]
    public void ReadServerIps_InvalidInjectedResolverResult_AbsentFromRules()
    {
        File.WriteAllText(_cfg, @"{
            ""outbounds"": [
                { ""type"": ""vless"", ""server"": ""injection-test.example.com"" }
            ],
            ""endpoints"": [
                {
                    ""type"": ""wireguard"",
                    ""peers"": [
                        { ""address"": ""wg-injection.example.com"" }
                    ]
                }
            ]
        }");
        var fake = OkRunner();
        var sut = CreateSut(fake, hostResolver: h =>
        {
            if (h == "injection-test.example.com")
            {
                return new[]
                {
                    "198.51.100.6",
                    "10.0.0.1 } accept; add rule inet vpnrouter_ks output drop #",
                    "not-an-ip-literal",
                    "192.168.1.1/24"
                };
            }
            if (h == "wg-injection.example.com")
            {
                return new[]
                {
                    "2001:db8::6",
                    "malformed::ipv6::extra",
                    "'; drop table inet vpnrouter_ks; --"
                };
            }
            return Array.Empty<string>();
        });

        sut.CreateBlockRules(Array.Empty<string>());
        sut.EnableBlockRules();

        var rules = File.ReadAllText(LoadedRulesetFile(fake));
        Assert.Contains("add rule inet vpnrouter_ks output ip daddr { 198.51.100.6 } accept", rules);
        Assert.Contains("add rule inet vpnrouter_ks output ip6 daddr { 2001:db8::6 } accept", rules);
        Assert.DoesNotContain("add rule inet vpnrouter_ks output drop", rules);
        Assert.DoesNotContain("not-an-ip-literal", rules);
        Assert.DoesNotContain("192.168.1.1/24", rules);
        Assert.DoesNotContain("malformed::ipv6::extra", rules);
        Assert.DoesNotContain("drop table inet", rules);

        var extracted = sut.ReadServerIps();
        Assert.Equal(new[] { "198.51.100.6", "2001:db8::6" }, extracted);
    }
}
