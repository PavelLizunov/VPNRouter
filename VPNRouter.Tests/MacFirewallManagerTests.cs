using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using VPNRouter.Core.Interfaces;
using VPNRouter.Core.Platform.macOS;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;
using Xunit;

namespace VPNRouter.Tests;

public class MacFirewallManagerTests : IDisposable
{
    private const string Anchor = "com.vpnrouter/killswitch";

    private readonly string _cfg =
        Path.Combine(Path.GetTempPath(), "vpnrouter-fw-cfg-" + Guid.NewGuid().ToString("N") + ".json");
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
        try { if (File.Exists(_pfconf)) File.Delete(_pfconf); } catch { }
        try { if (File.Exists(_rules)) File.Delete(_rules); } catch { }
        try { if (File.Exists(_mainConf)) File.Delete(_mainConf); } catch { }
    }

    private static ProcessResult Ok(string stdout = "", string stderr = "") =>
        new ProcessResult(0, stdout, stderr, TimeSpan.Zero, false);
    private static ProcessResult Fail(string stderr = "privileged helper denied") =>
        new ProcessResult(1, "", stderr, TimeSpan.Zero, false);

    private static string? Op(ProcessRequest call)
    {
        if (call.StdinInput is null) return null;
        using var document = JsonDocument.Parse(call.StdinInput);
        return document.RootElement.GetProperty("op").GetString();
    }

    private static string? Mode(ProcessRequest call)
    {
        if (call.StdinInput is null) return null;
        using var document = JsonDocument.Parse(call.StdinInput);
        return document.RootElement.TryGetProperty("mode", out var mode) ? mode.GetString() : null;
    }

    private static string? Rules(ProcessRequest call)
    {
        if (call.StdinInput is null) return null;
        using var document = JsonDocument.Parse(call.StdinInput);
        return document.RootElement.TryGetProperty("rules", out var rules) ? rules.GetString() : null;
    }

    private static bool IsHelper(ProcessRequest call, string op) =>
        call.ExecutablePath == MacPrivilegePaths.SudoPath
        && call.Arguments.SequenceEqual(new[] { "-n", MacPrivilegePaths.HelperPath })
        && Op(call) == op;

    private static bool IsPfApply(ProcessRequest call, string mode) =>
        IsHelper(call, "pf_apply") && Mode(call) == mode;

    private static bool IsRelease(ProcessRequest call) => IsHelper(call, "pf_release");

    private void WriteConfig(string serverIp) =>
        File.WriteAllText(_cfg, $@"{{ ""outbounds"": [
            {{ ""type"": ""vless"", ""tag"": ""proxy"", ""server"": ""{serverIp}"" }},
            {{ ""type"": ""direct"", ""tag"": ""direct"" }} ] }}");

    private static FakeProcessRunner OkRunner(bool carrierPresent = false)
    {
        var f = new FakeProcessRunner();
        f.OnRun(c => IsHelper(c, "pf_status"),
            Ok(stdout: carrierPresent
                ? "anchor \"com.apple/*\" all\nanchor \"" + Anchor + "\" all\n"
                : "scrub-anchor \"com.apple/*\" all fragment reassemble\nanchor \"com.apple/*\" all\n"));
        f.OnRun(c => IsHelper(c, "pf_enable"), Ok());
        f.OnRun(c => IsHelper(c, "pf_apply"), Ok());
        f.OnRun(IsRelease, Ok());
        return f;
    }

    private MacFirewallManager Sut(FakeProcessRunner fake, string? pfconf = null, string? rulesPath = null, string? mainConfPath = null, Func<string, IReadOnlyList<string>>? hostResolver = null) =>
        new MacFirewallManager(null, fake, _cfg, hostResolver: hostResolver, pfConfPath: pfconf ?? _pfconf, rulesPath: rulesPath ?? _rules, mainConfPath: mainConfPath ?? _mainConf);

    private static void Bind(MacFirewallManager manager, FakeProcessRunner runner)
    {
        runner.OnRun(c => IsHelper(c, "pf_session"), Ok(stdout: """{"pid":391,"start_time":3000}"""));
        manager.TrackRuntimeLaunch(() => "0123456789abcdef0123456789abcdef");
    }

    [Fact]
    public void SplitTunnel_disarms_and_Enable_is_noop()
    {
        WriteConfig("9.9.9.9");
        var fake = OkRunner();
        var sut = Sut(fake);

        sut.CreateBlockRules(new[] { "Discord", "chrome" }, isFullTunnel: false);
        sut.EnableBlockRules();

        Assert.Empty(fake.RunCalls);
    }

    [Fact]
    public void SplitTunnel_emptyList_scanTimeout_still_disarms()
    {
        WriteConfig("9.9.9.9");
        var fake = OkRunner();
        var sut = Sut(fake);

        sut.CreateBlockRules(Array.Empty<string>(), isFullTunnel: false);
        sut.EnableBlockRules();

        Assert.Empty(fake.RunCalls);
    }

    [Fact]
    public void SecondEnable_with_carrier_already_present_touches_only_the_anchor()
    {
        WriteConfig("9.9.9.9");
        var fake = OkRunner(carrierPresent: true);
        var sut = Sut(fake);

        Bind(sut, fake);
        sut.CreateBlockRules(Array.Empty<string>());
        sut.EnableBlockRules();

        Assert.Contains(fake.RunCalls, c => IsPfApply(c, "anchor"));
        Assert.DoesNotContain(fake.RunCalls, c => IsPfApply(c, "main"));
    }

    [Fact]
    public void Disable_after_load_requests_typed_release()
    {
        WriteConfig("9.9.9.9");
        var fake = OkRunner(carrierPresent: true);
        var sut = Sut(fake);
        Bind(sut, fake);
        sut.CreateBlockRules(Array.Empty<string>());
        sut.EnableBlockRules();

        sut.DisableBlockRules();

        Assert.Single(fake.RunCalls.Where(IsRelease));
        Assert.DoesNotContain(fake.RunCalls, c => IsPfApply(c, "main"));
    }

    [Fact]
    public void DeleteAllRules_requests_typed_release()
    {
        WriteConfig("9.9.9.9");
        var fake = OkRunner(carrierPresent: true);
        var sut = Sut(fake);
        Bind(sut, fake);
        sut.CreateBlockRules(Array.Empty<string>());
        sut.EnableBlockRules();

        sut.DeleteAllRules();

        Assert.Single(fake.RunCalls.Where(IsRelease));
    }

    [Fact]
    public void DeleteAllRules_when_nothing_loaded_never_touches_main_ruleset()
    {
        WriteConfig("9.9.9.9");
        var fake = OkRunner();
        var sut = Sut(fake);

        sut.DeleteAllRules();

        Assert.DoesNotContain(fake.RunCalls, c => IsPfApply(c, "main"));
        Assert.DoesNotContain(fake.RunCalls, c => IsPfApply(c, "anchor"));
    }

    [Fact]
    public void Dispose_after_load_requests_typed_release()
    {
        WriteConfig("9.9.9.9");
        var fake = OkRunner(carrierPresent: true);
        var sut = Sut(fake);
        Bind(sut, fake);
        sut.CreateBlockRules(Array.Empty<string>());
        sut.EnableBlockRules();

        sut.Dispose();

        Assert.Single(fake.RunCalls.Where(IsRelease));
        Assert.DoesNotContain(fake.RunCalls, c => IsPfApply(c, "main"));
    }

    [Fact]
    public void Enable_when_all_loads_fail_releases_enable_and_stays_unloaded()
    {
        WriteConfig("9.9.9.9");
        var fake = new FakeProcessRunner();
        fake.OnRun(c => IsHelper(c, "pf_enable"), Ok());
        fake.OnRun(c => IsHelper(c, "pf_status"), Fail());
        fake.OnRun(c => IsHelper(c, "pf_apply"), Fail());
        fake.OnRun(IsRelease, Ok());
        var sut = Sut(fake);

        Bind(sut, fake);
        sut.CreateBlockRules(Array.Empty<string>());
        sut.EnableBlockRules();

        Assert.Single(fake.RunCalls.Where(IsRelease));
        Assert.False(sut.IsLoaded);

        var before = fake.RunCalls.Count;
        sut.DisableBlockRules();
        Assert.Equal(before, fake.RunCalls.Count);
    }

    [Fact]
    public void Enable_when_anchor_body_load_fails_releases_and_does_not_claim_block()
    {
        WriteConfig("9.9.9.9");
        var fake = new FakeProcessRunner();
        fake.OnRun(c => IsHelper(c, "pf_enable"), Ok());
        fake.OnRun(c => IsHelper(c, "pf_status"), Ok(stdout: "anchor \"com.apple/*\" all\n"));
        fake.OnRun(c => IsPfApply(c, "anchor"), Fail());
        fake.OnRun(IsRelease, Ok());
        var sut = Sut(fake);

        Bind(sut, fake);
        sut.CreateBlockRules(Array.Empty<string>());
        sut.EnableBlockRules();

        Assert.Single(fake.RunCalls.Where(IsRelease));
        Assert.False(sut.IsLoaded);
    }

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
        Assert.Contains("pass out quick inet from any to 100.64.0.0/10", rules);
        Assert.Contains("pass out quick inet6 from any to fe80::/10", rules);
        Assert.Contains("pass out quick inet6 from any to fc00::/7", rules);
        Assert.Contains("pass out quick inet from any to 1.2.3.4", rules);
        Assert.DoesNotContain("set block-policy", rules);
    }

    [Fact]
    public void BuildRules_MixedFamily_BothFamilies()
    {
        var rules = MacFirewallManager.BuildRules(new List<string> { "1.2.3.4", "2001:db8::1" });

        Assert.Contains("pass out quick inet from any to 1.2.3.4", rules);
        Assert.Contains("pass out quick inet6 from any to 2001:db8::1", rules);
        Assert.DoesNotContain("pass out quick inet from any to 2001:db8::1", rules);
    }

    [Fact]
    public void Enable_WritesRulesToConfiguredPath_NotSharedTemp()
    {
        WriteConfig("9.9.9.9");
        var customRules = Path.Combine(Path.GetTempPath(), "custom-mac-rules-" + Guid.NewGuid().ToString("N") + ".conf");
        var customMain = Path.Combine(Path.GetTempPath(), "custom-mac-main-" + Guid.NewGuid().ToString("N") + ".conf");
        var fake = OkRunner();
        var sut = new MacFirewallManager(null, fake, _cfg, pfConfPath: _pfconf, rulesPath: customRules, mainConfPath: customMain);

        try
        {
            Bind(sut, fake);
            sut.CreateBlockRules(Array.Empty<string>());
            sut.EnableBlockRules();

            Assert.True(File.Exists(customRules));
            Assert.True(File.Exists(customMain));
            Assert.Contains(fake.RunCalls, c => IsPfApply(c, "anchor")
                && (Rules(c) ?? string.Empty).Contains("block drop out all", StringComparison.Ordinal));
            Assert.DoesNotContain(fake.RunCalls, c => c.Arguments.Any(a => a.Contains("/tmp/vpnrouter-pf-killswitch.conf")));
            Assert.DoesNotContain(fake.RunCalls, c => c.Arguments.Any(a => a.Contains("/tmp/vpnrouter-pf-main.conf")));
        }
        finally
        {
            try { if (File.Exists(customRules)) File.Delete(customRules); } catch { }
            try { if (File.Exists(customMain)) File.Delete(customMain); } catch { }
        }
    }

    [Theory]
    [InlineData("disable")]
    [InlineData("delete")]
    [InlineData("dispose")]
    public void Release_failure_remains_retryable(string action)
    {
        var fake = new FakeProcessRunner();
        var fails = true;
        fake.OnRun(IsRelease, _ => Task.FromResult(fails ? Fail() : Ok()));
        fake.OnRun(c => IsHelper(c, "pf_status"), Ok(stdout: $"anchor \"{Anchor}\" all\n"));
        fake.OnRun(c => IsHelper(c, "pf_enable") || IsHelper(c, "pf_apply"), Ok());
        var sut = Sut(fake);
        Bind(sut, fake);
        sut.CreateBlockRules(Array.Empty<string>());
        sut.EnableBlockRules();
        Assert.True(sut.IsLoaded);

        Action release = action switch
        {
            "disable" => sut.DisableBlockRules,
            "delete" => sut.DeleteAllRules,
            _ => sut.Dispose
        };
        release();
        Assert.True(sut.IsLoaded);
        fails = false;
        release();
        release();

        Assert.Equal(2, fake.RunCalls.Count(IsRelease));
        Assert.DoesNotContain(fake.RunCalls, c => IsPfApply(c, "main"));
    }

    [Fact]
    public void FailedReleaseKeepsRulesEligibleForCommittedRefresh()
    {
        var fake = new FakeProcessRunner();
        fake.OnRun(c => IsHelper(c, "pf_enable") || IsPfApply(c, "anchor"), Ok());
        fake.OnRun(c => IsHelper(c, "pf_status"), Ok(stdout: $"anchor \"{Anchor}\" all\n"));
        fake.OnRun(IsRelease, Fail());
        var sut = Sut(fake);
        Bind(sut, fake);
        sut.CreateBlockRules(Array.Empty<string>());
        sut.EnableBlockRules();
        Assert.False(sut.TryDisableBlockRules());

        sut.UpdateCommittedConfig(
            """{"outbounds":[{"tag":"proxy","type":"vless","server":"192.0.2.25"}]}""", true);

        var apply = fake.RunCalls.Where(call => IsPfApply(call, "anchor")).ToArray();
        Assert.Equal(2, apply.Length);
        Assert.Contains("192.0.2.25", Rules(apply[1]) ?? string.Empty);
    }

    [Fact]
    public void Release_after_confirmed_restart_uses_replacement_identity()
    {
        var fake = OkRunner(carrierPresent: true);
        var sut = Sut(fake);
        var identities = new Queue<ProcessResult>(new[]
        {
            Ok(stdout: """{"pid":391,"start_time":3000}"""),
            Ok(stdout: """{"pid":405,"start_time":4000}""")
        });
        fake.OnRun(c => IsHelper(c, "pf_session"), _ => Task.FromResult(identities.Dequeue()));
        var launchId = Guid.NewGuid().ToString("N");
        sut.TrackRuntimeLaunch(() => launchId);
        sut.CreateBlockRules(Array.Empty<string>());
        sut.EnableBlockRules();
        launchId = Guid.NewGuid().ToString("N");

        sut.DisableBlockRules();

        using var release = JsonDocument.Parse(Assert.Single(fake.RunCalls.Where(IsRelease)).StdinInput!);
        Assert.Equal(405, release.RootElement.GetProperty("pid").GetInt32());
        Assert.Equal(4000, release.RootElement.GetProperty("start_time").GetInt64());
    }

    [Fact]
    public void RepeatedEnableChecksKernelAndReappliesEvenWithLoadedRules()
    {
        var fake = OkRunner(carrierPresent: true);
        var sut = Sut(fake);
        Bind(sut, fake);
        sut.CreateBlockRules(Array.Empty<string>());

        sut.EnableBlockRules();
        Assert.True(sut.IsLoaded);
        sut.EnableBlockRules();

        Assert.Equal(2, fake.RunCalls.Count(c => IsHelper(c, "pf_enable")));
        Assert.Equal(2, fake.RunCalls.Count(c => IsPfApply(c, "anchor")));
    }

    [Fact]
    public void ReplacementLookupRetriesWithoutSendingPreviousIdentity()
    {
        var fake = OkRunner(carrierPresent: true);
        var replies = new Queue<ProcessResult>(new[]
        {
            Ok(stdout: """{"pid":391,"start_time":3000}"""),
            Fail("replacement still starting"),
            Ok(stdout: """{"pid":405,"start_time":4000}""")
        });
        fake.OnRun(c => IsHelper(c, "pf_session"), _ => Task.FromResult(replies.Dequeue()));
        var launchId = Guid.NewGuid().ToString("N");
        var sut = Sut(fake);
        sut.TrackRuntimeLaunch(() => launchId);
        sut.CreateBlockRules(Array.Empty<string>());
        sut.EnableBlockRules();

        launchId = Guid.NewGuid().ToString("N");
        sut.EnableBlockRules();
        sut.EnableBlockRules();

        var enables = fake.RunCalls.Where(c => IsHelper(c, "pf_enable")).ToArray();
        Assert.Equal(2, enables.Length);
        using var replacement = JsonDocument.Parse(enables[1].StdinInput!);
        Assert.Equal(405, replacement.RootElement.GetProperty("pid").GetInt32());
        var lookups = fake.RunCalls.Where(c => IsHelper(c, "pf_session")).ToArray();
        Assert.Equal(3, lookups.Length);
        foreach (var lookup in lookups.Skip(1))
        {
            using var document = JsonDocument.Parse(lookup.StdinInput!);
            Assert.Equal(launchId, document.RootElement.GetProperty("launch_id").GetString());
        }
    }

    [Fact]
    public void Armed_firewall_without_confirmed_session_cannot_change_pf()
    {
        var fake = OkRunner();
        var sut = Sut(fake);
        sut.CreateBlockRules(Array.Empty<string>());
        sut.EnableBlockRules();
        Assert.Empty(fake.RunCalls);
    }

    [Fact]
    public void Apply_timeout_releases_the_same_session_without_claiming_rules_loaded()
    {
        var fake = new FakeProcessRunner();
        fake.OnRun(c => IsHelper(c, "pf_enable"), Ok());
        fake.OnRun(c => IsHelper(c, "pf_status"), Ok(stdout: $"anchor \"{Anchor}\" all\n"));
        fake.OnRun(c => IsPfApply(c, "anchor"),
            new ProcessResult(0, "", "timed out", TimeSpan.FromSeconds(10), TimedOut: true));
        fake.OnRun(IsRelease, Ok());
        var sut = Sut(fake);
        Bind(sut, fake);
        sut.CreateBlockRules(Array.Empty<string>());
        sut.EnableBlockRules();

        Assert.False(sut.IsLoaded);
        Assert.Single(fake.RunCalls.Where(IsRelease));
    }
}
