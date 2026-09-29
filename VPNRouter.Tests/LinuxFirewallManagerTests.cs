using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using VPNRouter.Core.Interfaces;
using VPNRouter.Core.Platform.Linux;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;
using Xunit;

namespace VPNRouter.Tests;

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

        Assert.Empty(fake.RunCalls);
    }

    [Fact]
    public void SplitTunnel_emptyList_scanTimeout_still_disarms()
    {
        WriteConfig("9.9.9.9");
        var fake = OkRunner();
        var sut = CreateSut(fake);

        sut.CreateBlockRules(Array.Empty<string>(), isFullTunnel: false);
        sut.EnableBlockRules();

        Assert.Empty(fake.RunCalls);
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
        sut.EnableBlockRules();

        Assert.False(File.Exists(_marker));

        var before = fake.RunCalls.Count;
        sut.DisableBlockRules();
        Assert.Equal(before, fake.RunCalls.Count);
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
        sut.CreateBlockRules(Array.Empty<string>());

        sut.DisableBlockRules();

        Assert.DoesNotContain(fake.RunCalls, c => c.Arguments.Contains("delete"));
    }

    [Fact]
    public void Dispose_without_load_does_not_delete_and_is_noop()
    {
        WriteConfig("9.9.9.9");
        var fake = OkRunner();
        var sut = CreateSut(fake);
        sut.CreateBlockRules(Array.Empty<string>());

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
        Assert.DoesNotContain("ip daddr {  }", rules);
    }

    [Fact]
    public void BuildRuleset_MixedFamily_EmitsBoth()
    {
        var rules = LinuxFirewallManager.BuildRuleset(new List<string> { "1.2.3.4", "2001:db8::1" });

        Assert.Contains("add rule inet vpnrouter_ks output ip daddr { 1.2.3.4 } accept", rules);
        Assert.Contains("add rule inet vpnrouter_ks output ip6 daddr { 2001:db8::1 } accept", rules);
        Assert.DoesNotContain("ip daddr { 2001:db8::1 }", rules);
    }

    [Fact]
    public void Enable_writes_engaged_marker_Disable_clears_it()
    {
        WriteConfig("9.9.9.9");
        var sut = CreateSut(OkRunner());
        sut.CreateBlockRules(Array.Empty<string>());

        sut.EnableBlockRules();
        Assert.True(File.Exists(_marker));

        sut.DisableBlockRules();
        Assert.False(File.Exists(_marker));
    }

    [Fact]
    public void CleanupOrphanedRules_with_marker_deletes_table_and_clears_marker()
    {
        File.WriteAllText(_marker, "engaged");
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
        var sut = CreateSut(fake);

        sut.CleanupOrphanedRules(null);

        Assert.Empty(fake.RunCalls);
    }

    [Fact]
    public void Enable_WritesRulesetToConfiguredPath_NotSharedTemp()
    {
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

        sut.DisableBlockRules();
        Assert.True(File.Exists(_marker));

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

        sut.CleanupOrphanedRules(null);
        Assert.True(File.Exists(_marker));

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

        sut.DeleteAllRules();
        Assert.True(File.Exists(_marker));

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

        sut.Dispose();
        Assert.True(File.Exists(_marker));

        allowDelete = true;
        sut.Dispose();
        Assert.False(File.Exists(_marker));
        Assert.Equal(2, fake.RunCalls.Count(c => c.Arguments.Contains("delete")));

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

        sut.EnableBlockRules();
        Assert.False(File.Exists(_marker));

        loadShouldTimeout = false;
        sut.EnableBlockRules();
        Assert.True(File.Exists(_marker));

        sut.DisableBlockRules();
        Assert.True(File.Exists(_marker));

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

        Assert.True(File.Exists(_marker));
    }

    [Theory]
    [InlineData(@"{""nftables"":[{""table"":{""name"":""vpnrouter_ks""}}]}")]
    [InlineData(@"{""nftables"":[{""table"":{""family"":""inet""}}]}")]
    [InlineData(@"{""nftables"":[{""table"":{""family"":123,""name"":""vpnrouter_ks""}}]}")]
    [InlineData(@"{""nftables"":[{""table"":{""family"":""inet"",""name"":456}}]}")]
    [InlineData(@"{""nftables"":[{""table"":{""family"":null,""name"":""vpnrouter_ks""}}]}")]
    [InlineData(@"{""nftables"":[{""table"":{""family"":""inet"",""name"":null}}]}")]
    [InlineData(@"{""nftables"":[{""table"":{""family"":"""",""name"":""vpnrouter_ks""}}]}")]
    [InlineData(@"{""nftables"":[{""table"":{""family"":""   "",""name"":""vpnrouter_ks""}}]}")]
    [InlineData(@"{""nftables"":[{""table"":{""family"":""inet"",""name"":""""}}]}")]
    [InlineData(@"{""nftables"":[{""table"":{""family"":""inet"",""name"":""   ""}}]}")]
    [InlineData(@"{""nftables"":[{""table"":""not_an_object""}]}")]
    [InlineData(@"{""nftables"":[{""table"":null}]}")]
    [InlineData(@"{""nftables"":[{""table"":[]}]}")]
    [InlineData(@"{""nftables"":[{""unknown"":{}}]}")]
    [InlineData(@"{""nftables"":[123]}")]
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
    public void UpdateCommittedConfig_FailedRefresh_RetainsAForRetry()
    {
        WriteConfig("198.51.100.1");
        var failRefresh = false;
        var injections = 0;
        var fake = new FakeProcessRunner();
        fake.OnRun(
            r => failRefresh && r.ExecutablePath == "/usr/bin/sudo" && r.Arguments.Contains("-f"),
            _ =>
            {
                injections++;
                return Task.FromResult(Fail("nft failed"));
            });
        fake.OnRun(r => r.ExecutablePath == "/usr/bin/sudo", Ok());
        var sut = CreateSut(fake);

        sut.CreateBlockRules(Array.Empty<string>(), isFullTunnel: true);
        sut.EnableBlockRules();
        Assert.True(sut.IsLoaded);
        Assert.Equal(new[] { "198.51.100.1" }, sut.ServerIps);
        Assert.True(File.Exists(_marker));

        var committedJsonB = """
        {
          "outbounds": [
            { "type": "vless", "tag": "proxy", "server": "203.0.113.99" }
          ]
        }
        """;

        failRefresh = true;
        sut.UpdateCommittedConfig(committedJsonB, enabledForFullTunnel: true);

        Assert.Equal(1, injections);
        Assert.Equal(new[] { "198.51.100.1" }, sut.ServerIps);
        Assert.True(sut.IsLoaded);
        Assert.True(File.Exists(_marker));

        int callsBeforeRetry = fake.RunCalls.Count;
        failRefresh = false;
        sut.UpdateCommittedConfig(committedJsonB, enabledForFullTunnel: true);

        Assert.Equal(1, injections);
        Assert.Equal(new[] { "203.0.113.99" }, sut.ServerIps);
        Assert.True(sut.IsLoaded);
        Assert.True(File.Exists(_marker));
        Assert.DoesNotContain(fake.RunCalls.Skip(callsBeforeRetry), c => c.Arguments.Contains("delete"));
        Assert.Single(fake.RunCalls.Skip(callsBeforeRetry), c =>
            c.ExecutablePath == "/usr/bin/sudo" && c.Arguments.Contains("nft") && c.Arguments.Contains("-f"));
    }

    [Fact]
    public void UpdateCommittedConfig_MalformedJson_RetainsPriorList()
    {
        WriteConfig("198.51.100.1");
        var fake = OkRunner();
        var sut = CreateSut(fake);

        sut.CreateBlockRules(Array.Empty<string>(), isFullTunnel: true);
        Assert.Equal(new[] { "198.51.100.1" }, sut.ServerIps);

        sut.UpdateCommittedConfig("{ invalid json content", enabledForFullTunnel: true);

        Assert.Equal(new[] { "198.51.100.1" }, sut.ServerIps);
    }

    [Fact]
    public void UpdateCommittedConfig_Disabled_LiftsRulesAndDisarms()
    {
        WriteConfig("198.51.100.1");
        var fake = OkRunner();
        var sut = CreateSut(fake);

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

        Assert.False(sut.IsArmed);
        Assert.False(sut.IsLoaded);
        Assert.False(File.Exists(_marker));
        Assert.Contains(fake.RunCalls.Skip(callsBefore), c => c.Arguments.Contains("delete") && c.Arguments.Contains("table"));
        Assert.Equal(new[] { "198.51.100.1" }, sut.ServerIps);
    }

    [Fact]
    public void UpdateCommittedConfig_MalformedJson_Disabled_RemovesRuleAndDisarms()
    {
        WriteConfig("198.51.100.1");
        var fake = OkRunner();
        var sut = CreateSut(fake);

        sut.CreateBlockRules(Array.Empty<string>(), isFullTunnel: true);
        sut.EnableBlockRules();
        Assert.True(sut.IsLoaded);
        Assert.True(sut.IsArmed);

        int callsBefore = fake.RunCalls.Count;

        sut.UpdateCommittedConfig("{ not valid json content", enabledForFullTunnel: false);

        Assert.False(sut.IsArmed);
        Assert.False(sut.IsLoaded);
        Assert.False(File.Exists(_marker));
        Assert.Contains(fake.RunCalls.Skip(callsBefore), c => c.Arguments.Contains("delete") && c.Arguments.Contains("table"));
        Assert.Equal(new[] { "198.51.100.1" }, sut.ServerIps);
    }

    [Fact]
    public void UpdateCommittedConfig_Disabled_HostnameResolverThrows_InvokesZeroResolverAndDisarms()
    {
        WriteConfig("198.51.100.1");
        var fake = OkRunner();
        var sut = CreateSut(fake, hostResolver: _ => throw new InvalidOperationException("Hostname resolver must not be invoked when disabled"));

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

        sut.UpdateCommittedConfig(committedJsonWithHost, enabledForFullTunnel: false);

        Assert.False(sut.IsArmed);
        Assert.False(sut.IsLoaded);
        Assert.False(File.Exists(_marker));
        Assert.Contains(fake.RunCalls.Skip(callsBefore), c => c.Arguments.Contains("delete") && c.Arguments.Contains("table"));
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
        var sut = CreateSut(fake);

        Assert.Throws<JsonException>(() => sut.ParseServerIps(malformedRoot));
    }

    [Fact]
    public void ParseServerIps_EmptyObject_ReturnsEmptyList()
    {
        var fake = OkRunner();
        var sut = CreateSut(fake);

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
        var sut = CreateSut(fake);

        sut.CreateBlockRules(Array.Empty<string>(), isFullTunnel: true);
        Assert.Equal(new[] { "198.51.100.1" }, sut.ServerIps);

        sut.UpdateCommittedConfig(malformedRoot, enabledForFullTunnel: true);

        Assert.Equal(new[] { "198.51.100.1" }, sut.ServerIps);
    }

    [Fact]
    public void UpdateCommittedConfig_EmptyObject_EmptiesList()
    {
        WriteConfig("198.51.100.1");
        var fake = OkRunner();
        var sut = CreateSut(fake);

        sut.CreateBlockRules(Array.Empty<string>(), isFullTunnel: true);
        Assert.Equal(new[] { "198.51.100.1" }, sut.ServerIps);

        sut.UpdateCommittedConfig("{}", enabledForFullTunnel: true);

        Assert.Empty(sut.ServerIps);
    }
}
