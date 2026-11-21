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

    private static bool IsAnchorFlush(ProcessRequest call) => IsHelper(call, "pf_flush");

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
        f.OnRun(c => IsHelper(c, "pf_flush"), Ok());
        f.OnRun(c => IsHelper(c, "pf_disable"), Ok());
        f.OnRun(c => c.ExecutablePath == MacPrivilegePaths.SudoPath, Ok());
        return f;
    }

    private MacFirewallManager Sut(FakeProcessRunner fake, string? pfconf = null, string? rulesPath = null, string? mainConfPath = null, Func<string, IReadOnlyList<string>>? hostResolver = null) =>
        new MacFirewallManager(null, fake, _cfg, _marker, hostResolver: hostResolver, pfConfPath: pfconf ?? _pfconf, rulesPath: rulesPath ?? _rules, mainConfPath: mainConfPath ?? _mainConf);

    private static bool GetArmed(MacFirewallManager sut) =>
        (bool)typeof(MacFirewallManager).GetField("_armed", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(sut)!;

    private static bool GetLoaded(MacFirewallManager sut) =>
        (bool)typeof(MacFirewallManager).GetField("_loaded", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(sut)!;

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

        sut.CreateBlockRules(Array.Empty<string>());
        sut.EnableBlockRules();

        Assert.Contains(fake.RunCalls, c => IsPfApply(c, "anchor"));
        Assert.DoesNotContain(fake.RunCalls, c => IsPfApply(c, "main"));
    }

    [Fact]
    public void Disable_after_load_flushes_anchor_and_releases_enable_no_main_reload()
    {
        WriteConfig("9.9.9.9");
        var fake = OkRunner(carrierPresent: true);
        var sut = Sut(fake);
        sut.CreateBlockRules(Array.Empty<string>());
        sut.EnableBlockRules();

        sut.DisableBlockRules();

        Assert.Contains(fake.RunCalls, IsAnchorFlush);
        Assert.Contains(fake.RunCalls, c => IsHelper(c, "pf_disable"));
        Assert.DoesNotContain(fake.RunCalls, c => IsPfApply(c, "main"));
    }

    [Fact]
    public void DeleteAllRules_flushes_anchor_and_releases_enable()
    {
        WriteConfig("9.9.9.9");
        var fake = OkRunner(carrierPresent: true);
        var sut = Sut(fake);
        sut.CreateBlockRules(Array.Empty<string>());
        sut.EnableBlockRules();

        sut.DeleteAllRules();

        Assert.Contains(fake.RunCalls, IsAnchorFlush);
        Assert.Contains(fake.RunCalls, c => IsHelper(c, "pf_disable"));
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
    public void Dispose_after_load_flushes_anchor_antibrick()
    {
        WriteConfig("9.9.9.9");
        var fake = OkRunner(carrierPresent: true);
        var sut = Sut(fake);
        sut.CreateBlockRules(Array.Empty<string>());
        sut.EnableBlockRules();

        sut.Dispose();

        Assert.Contains(fake.RunCalls, IsAnchorFlush);
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
        fake.OnRun(c => c.ExecutablePath == MacPrivilegePaths.SudoPath, Ok());
        var sut = Sut(fake);

        sut.CreateBlockRules(Array.Empty<string>());
        sut.EnableBlockRules();

        Assert.Contains(fake.RunCalls, c => IsHelper(c, "pf_disable"));
        Assert.False(File.Exists(_marker));

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
        fake.OnRun(c => c.ExecutablePath == MacPrivilegePaths.SudoPath, Ok());
        var sut = Sut(fake);

        sut.CreateBlockRules(Array.Empty<string>());
        sut.EnableBlockRules();

        Assert.Contains(fake.RunCalls, c => IsHelper(c, "pf_disable"));
        Assert.False(File.Exists(_marker));
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
    public void CleanupOrphanedRules_with_anchorV1_marker_flushes_anchor_only()
    {
        File.WriteAllText(_marker, "anchor-v1");
        var fake = OkRunner();
        var sut = Sut(fake);

        sut.CleanupOrphanedRules(null);

        Assert.Contains(fake.RunCalls, IsAnchorFlush);
        Assert.DoesNotContain(fake.RunCalls, c => IsPfApply(c, "main"));
        Assert.False(File.Exists(_marker));
    }

    [Fact]
    public void CleanupOrphanedRules_with_legacy_marker_restores_default_and_clears_marker()
    {
        File.WriteAllText(_marker, "engaged");
        var fake = OkRunner();
        var sut = Sut(fake);

        sut.CleanupOrphanedRules(null);

        Assert.Contains(fake.RunCalls, c => IsPfApply(c, "main"));
        Assert.False(File.Exists(_marker));
    }

    [Fact]
    public void CleanupOrphanedRules_without_marker_is_noop()
    {
        var fake = OkRunner();
        var sut = Sut(fake);

        sut.CleanupOrphanedRules(null);

        Assert.Empty(fake.RunCalls);
    }

    [Fact]
    public void Enable_WritesRulesToConfiguredPath_NotSharedTemp()
    {
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

    [Fact]
    public void Disable_WhenFlushAnchorFails_RetainsLoadedStateAndMarker_AndSubsequentRetryClears()
    {
        WriteConfig("9.9.9.9");
        var flushFails = false;
        var fake = new FakeProcessRunner();
        fake.OnRun(c => IsHelper(c, "pf_enable"), Ok());
        fake.OnRun(c => IsHelper(c, "pf_status"), Ok(stdout: "anchor \"com.apple/*\" all\n"));
        fake.OnRun(IsAnchorFlush, _ => Task.FromResult(flushFails ? Fail("flush failed") : Ok()));
        fake.OnRun(c => IsHelper(c, "pf_disable"), Ok());
        fake.OnRun(c => c.ExecutablePath == MacPrivilegePaths.SudoPath, Ok());
        var sut = Sut(fake);

        sut.CreateBlockRules(Array.Empty<string>());
        sut.EnableBlockRules();
        Assert.True(File.Exists(_marker));

        flushFails = true;
        sut.DisableBlockRules();
        Assert.True(File.Exists(_marker));
        Assert.DoesNotContain(fake.RunCalls, c => IsHelper(c, "pf_disable"));

        flushFails = false;
        sut.DisableBlockRules();
        Assert.False(File.Exists(_marker));
        Assert.Contains(fake.RunCalls, c => IsHelper(c, "pf_disable"));
    }

    [Fact]
    public void DeleteAllRules_WhenFlushAnchorFails_RetainsLoadedStateAndMarker_AndSubsequentRetryClears()
    {
        WriteConfig("9.9.9.9");
        var flushFails = false;
        var fake = new FakeProcessRunner();
        fake.OnRun(c => IsHelper(c, "pf_enable"), Ok());
        fake.OnRun(c => IsHelper(c, "pf_status"), Ok(stdout: "anchor \"com.apple/*\" all\n"));
        fake.OnRun(IsAnchorFlush, _ => Task.FromResult(flushFails ? Fail("flush failed") : Ok()));
        fake.OnRun(c => IsHelper(c, "pf_disable"), Ok());
        fake.OnRun(c => c.ExecutablePath == MacPrivilegePaths.SudoPath, Ok());
        var sut = Sut(fake);

        sut.CreateBlockRules(Array.Empty<string>());
        sut.EnableBlockRules();
        Assert.True(File.Exists(_marker));

        flushFails = true;
        sut.DeleteAllRules();
        Assert.True(File.Exists(_marker));
        Assert.DoesNotContain(fake.RunCalls, c => IsHelper(c, "pf_disable"));

        flushFails = false;
        sut.DeleteAllRules();
        Assert.False(File.Exists(_marker));
        Assert.Contains(fake.RunCalls, c => IsHelper(c, "pf_disable"));
    }

    [Fact]
    public void DeleteAllRules_WhenArmedButUnloaded_FailedFlushAnchorRetainsArmedState_AndRetryClears()
    {
        WriteConfig("9.9.9.9");
        var flushFails = false;
        var fake = new FakeProcessRunner();
        fake.OnRun(IsAnchorFlush, _ => Task.FromResult(flushFails ? Fail("flush failed") : Ok()));
        fake.OnRun(c => c.ExecutablePath == MacPrivilegePaths.SudoPath, Ok());

        var sut = Sut(fake);
        sut.CreateBlockRules(Array.Empty<string>(), isFullTunnel: true);
        Assert.True(GetArmed(sut));
        Assert.False(GetLoaded(sut));

        flushFails = true;
        sut.DeleteAllRules();
        Assert.True(GetArmed(sut));

        flushFails = false;
        sut.DeleteAllRules();
        Assert.False(GetArmed(sut));

        sut.EnableBlockRules();
        Assert.DoesNotContain(fake.RunCalls, c => IsHelper(c, "pf_enable"));
    }

    [Fact]
    public void CleanupOrphanedRules_WhenAnchorFlushFails_PreservesMarker_AndSubsequentRetryClears()
    {
        File.WriteAllText(_marker, "anchor-v1");
        var flushFails = true;
        var fake = new FakeProcessRunner();
        fake.OnRun(IsAnchorFlush, _ => Task.FromResult(flushFails ? Fail() : Ok()));
        fake.OnRun(c => c.ExecutablePath == MacPrivilegePaths.SudoPath, Ok());
        var sut = Sut(fake);

        sut.CleanupOrphanedRules(null);
        Assert.True(File.Exists(_marker));

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
        fake.OnRun(c => IsPfApply(c, "main"),
            _ => Task.FromResult(restoreFails ? Fail() : Ok()));
        fake.OnRun(c => c.ExecutablePath == MacPrivilegePaths.SudoPath, Ok());
        var sut = Sut(fake);

        sut.CleanupOrphanedRules(null);
        Assert.True(File.Exists(_marker));

        restoreFails = false;
        sut.CleanupOrphanedRules(null);
        Assert.False(File.Exists(_marker));
    }

    [Fact]
    public void TokenRelease_WhenReleaseFails_PreservesStateForRetry_AndDoesNotAcquireDoubleEnable()
    {
        WriteConfig("9.9.9.9");
        var releaseFails = false;
        var fake = new FakeProcessRunner();
        fake.OnRun(c => IsHelper(c, "pf_enable"), Ok());
        fake.OnRun(c => IsHelper(c, "pf_status"), Ok(stdout: "anchor \"com.apple/*\" all\n"));
        fake.OnRun(c => IsHelper(c, "pf_disable"),
            _ => Task.FromResult(releaseFails ? Fail("release failed") : Ok()));
        fake.OnRun(c => c.ExecutablePath == MacPrivilegePaths.SudoPath, Ok());
        var sut = Sut(fake);

        sut.CreateBlockRules(Array.Empty<string>());
        sut.EnableBlockRules();
        Assert.True(File.Exists(_marker));

        releaseFails = true;
        sut.DisableBlockRules();
        Assert.False(File.Exists(_marker));

        sut.EnableBlockRules();
        Assert.True(File.Exists(_marker));
        var enableCalls = fake.RunCalls.Count(c => IsHelper(c, "pf_enable"));
        Assert.Equal(1, enableCalls);

        releaseFails = false;
        sut.DisableBlockRules();
        Assert.False(File.Exists(_marker));

        var countBefore = fake.RunCalls.Count;
        sut.DisableBlockRules();
        Assert.Equal(countBefore, fake.RunCalls.Count);
    }

    [Fact]
    public void Dispose_WhenFlushOrReleaseFails_RetriesOnSubsequentDispose()
    {
        WriteConfig("9.9.9.9");
        var flushFails = false;
        var releaseFails = false;
        var fake = new FakeProcessRunner();
        fake.OnRun(c => IsHelper(c, "pf_enable"), Ok());
        fake.OnRun(c => IsHelper(c, "pf_status"), Ok(stdout: "anchor \"com.apple/*\" all\n"));
        fake.OnRun(IsAnchorFlush, _ => Task.FromResult(flushFails ? Fail() : Ok()));
        fake.OnRun(c => IsHelper(c, "pf_disable"),
            _ => Task.FromResult(releaseFails ? Fail() : Ok()));
        fake.OnRun(c => c.ExecutablePath == MacPrivilegePaths.SudoPath, Ok());
        var sut = Sut(fake);

        sut.CreateBlockRules(Array.Empty<string>());
        sut.EnableBlockRules();
        Assert.True(File.Exists(_marker));

        flushFails = true;
        releaseFails = true;
        sut.Dispose();
        Assert.True(File.Exists(_marker));

        flushFails = false;
        sut.Dispose();
        Assert.False(File.Exists(_marker));

        releaseFails = false;
        sut.Dispose();
        Assert.Contains(fake.RunCalls, c => IsHelper(c, "pf_disable"));

        var countBefore = fake.RunCalls.Count;
        sut.Dispose();
        Assert.Equal(countBefore, fake.RunCalls.Count);
    }

    [Fact]
    public void HelperTimeout_IsTreatedAsFailure()
    {
        WriteConfig("9.9.9.9");
        var fake = new FakeProcessRunner();
        fake.OnRun(c => IsHelper(c, "pf_enable"), Ok());
        fake.OnRun(c => IsHelper(c, "pf_status"), Ok(stdout: "anchor \"com.apple/*\" all\n"));
        fake.OnRun(c => IsPfApply(c, "anchor"),
            new ProcessResult(0, "", "timed out", TimeSpan.FromSeconds(10), TimedOut: true));
        fake.OnRun(c => c.ExecutablePath == MacPrivilegePaths.SudoPath, Ok());
        var sut = Sut(fake);

        sut.CreateBlockRules(Array.Empty<string>());
        sut.EnableBlockRules();

        Assert.False(File.Exists(_marker));
        Assert.Contains(fake.RunCalls, c => IsHelper(c, "pf_disable"));

        File.WriteAllText(_marker, "anchor-v1");
        var orphanFake = new FakeProcessRunner();
        orphanFake.OnRun(IsAnchorFlush,
            new ProcessResult(0, "", "", TimeSpan.FromSeconds(10), TimedOut: true));
        orphanFake.OnRun(c => c.ExecutablePath == MacPrivilegePaths.SudoPath, Ok());
        var orphanSut = Sut(orphanFake);

        orphanSut.CleanupOrphanedRules(null);
        Assert.True(File.Exists(_marker));
    }

    [Fact]
    public void Disable_WhenReleaseFails_DirectRetryReleasesWithoutTouchingRules()
    {
        WriteConfig("9.9.9.9");
        var releaseFails = false;
        var fake = new FakeProcessRunner();
        fake.OnRun(c => IsHelper(c, "pf_enable"), Ok());
        fake.OnRun(c => IsHelper(c, "pf_status"), Ok(stdout: "anchor \"com.apple/*\" all\n"));
        fake.OnRun(c => IsHelper(c, "pf_disable"),
            _ => Task.FromResult(releaseFails ? Fail("release failed") : Ok()));
        fake.OnRun(c => c.ExecutablePath == MacPrivilegePaths.SudoPath, Ok());
        var sut = Sut(fake);

        sut.CreateBlockRules(Array.Empty<string>());
        sut.EnableBlockRules();
        Assert.True(File.Exists(_marker));

        releaseFails = true;
        sut.DisableBlockRules();
        Assert.False(File.Exists(_marker));
        Assert.Contains(fake.RunCalls, c => IsHelper(c, "pf_disable"));

        var flushCountBefore = fake.RunCalls.Count(IsAnchorFlush);
        releaseFails = false;
        sut.DisableBlockRules();
        var flushCountAfter = fake.RunCalls.Count(IsAnchorFlush);
        Assert.Equal(flushCountBefore, flushCountAfter);

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
}
