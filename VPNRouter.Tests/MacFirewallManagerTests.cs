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

    private static bool IsAnchorLoad(ProcessRequest c) =>
        c.Arguments.Contains("-a") && c.Arguments.Contains(Anchor) && c.Arguments.Contains("-f");
    private static bool IsAnchorFlush(ProcessRequest c) =>
        c.Arguments.Contains("-a") && c.Arguments.Contains(Anchor) && c.Arguments.Contains("-F");
    private static bool IsMainLoad(ProcessRequest c) =>
        !c.Arguments.Contains("-a") && c.Arguments.Contains("-f");

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

        Assert.Contains(fake.RunCalls, IsAnchorLoad);
        Assert.DoesNotContain(fake.RunCalls, IsMainLoad);
    }

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
        Assert.DoesNotContain(fake.RunCalls, c => c.Arguments.Contains("/etc/pf.conf"));
    }

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
        var sut = Sut(fake);

        sut.DeleteAllRules();

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

    [Fact]
    public void Enable_when_all_loads_fail_releases_enable_and_stays_unloaded()
    {
        WriteConfig("9.9.9.9");
        var fake = new FakeProcessRunner();
        fake.OnRun(r => r.Arguments.Contains("-E"), Ok(stderr: "Token : 999"));
        fake.OnRun(r => r.Arguments.Contains("-sr"), Fail());
        fake.OnRun(r => r.Arguments.Contains("-f"), Fail());
        fake.OnRun(r => r.ExecutablePath == "/usr/bin/sudo", Ok());
        var sut = Sut(fake);

        sut.CreateBlockRules(Array.Empty<string>());
        sut.EnableBlockRules();

        Assert.Contains(fake.RunCalls, c => c.Arguments.Contains("-X") && c.Arguments.Contains("999"));
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
        fake.OnRun(r => r.Arguments.Contains("-E"), Ok(stderr: "Token : 777"));
        fake.OnRun(r => r.Arguments.Contains("-sr"), Ok(stdout: "anchor \"com.apple/*\" all\n"));
        fake.OnRun(r => r.Arguments.Contains("-a") && r.Arguments.Contains("-f"), Fail());
        fake.OnRun(r => r.ExecutablePath == "/usr/bin/sudo", Ok());
        var sut = Sut(fake);

        sut.CreateBlockRules(Array.Empty<string>());
        sut.EnableBlockRules();

        Assert.Contains(fake.RunCalls, c => c.Arguments.Contains("-X") && c.Arguments.Contains("777"));
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

    [Theory]
    [InlineData("pf enabled\nToken : 12345678", "12345678")]
    [InlineData("Token : 42", "42")]
    [InlineData("no token here", null)]
    [InlineData("", null)]
    public void ParsePfToken_extracts_numeric_token(string stderr, string? expected)
        => Assert.Equal(expected, MacFirewallManager.ParsePfToken(stderr));

    [Fact]
    public void CleanupOrphanedRules_with_anchorV1_marker_flushes_anchor_only()
    {
        File.WriteAllText(_marker, "anchor-v1");
        var fake = OkRunner();
        var sut = Sut(fake);

        sut.CleanupOrphanedRules(null);

        Assert.Contains(fake.RunCalls, IsAnchorFlush);
        Assert.DoesNotContain(fake.RunCalls, c => c.Arguments.Contains("/etc/pf.conf"));
        Assert.False(File.Exists(_marker));
    }

    [Fact]
    public void CleanupOrphanedRules_with_legacy_marker_restores_default_and_clears_marker()
    {
        File.WriteAllText(_marker, "engaged");
        var fake = OkRunner();
        var sut = Sut(fake);

        sut.CleanupOrphanedRules(null);

        Assert.Contains(fake.RunCalls, c => c.Arguments.Contains("-f") && c.Arguments.Contains("/etc/pf.conf"));
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

        flushFails = true;
        sut.DisableBlockRules();
        Assert.True(File.Exists(_marker));
        Assert.DoesNotContain(fake.RunCalls, c => c.Arguments.Contains("-X"));

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

        flushFails = true;
        sut.DeleteAllRules();
        Assert.True(GetArmed(sut));

        flushFails = false;
        sut.DeleteAllRules();
        Assert.False(GetArmed(sut));

        sut.EnableBlockRules();
        Assert.DoesNotContain(fake.RunCalls, c => c.Arguments.Contains("-E"));
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
        fake.OnRun(c => c.Arguments.Contains("-f") && c.Arguments.Contains("/etc/pf.conf"),
            _ => Task.FromResult(restoreFails ? Fail() : Ok()));
        fake.OnRun(r => r.ExecutablePath == "/usr/bin/sudo", Ok());
        var sut = Sut(fake);

        sut.CleanupOrphanedRules(null);
        Assert.True(File.Exists(_marker));

        restoreFails = false;
        sut.CleanupOrphanedRules(null);
        Assert.False(File.Exists(_marker));
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

        releaseFails = true;
        sut.DisableBlockRules();
        Assert.False(File.Exists(_marker));

        sut.EnableBlockRules();
        Assert.True(File.Exists(_marker));
        var enableCalls = fake.RunCalls.Count(c => c.Arguments.Contains("-E"));
        Assert.Equal(1, enableCalls);

        releaseFails = false;
        sut.DisableBlockRules();
        Assert.False(File.Exists(_marker));

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

        flushFails = true;
        releaseFails = true;
        sut.Dispose();
        Assert.True(File.Exists(_marker));

        flushFails = false;
        sut.Dispose();
        Assert.False(File.Exists(_marker));

        releaseFails = false;
        sut.Dispose();
        Assert.Contains(fake.RunCalls, c => c.Arguments.Contains("-X") && c.Arguments.Contains("12345678"));

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
        fake.OnRun(IsAnchorLoad, new ProcessResult(0, "", "timed out", TimeSpan.FromSeconds(10), TimedOut: true));
        fake.OnRun(r => r.ExecutablePath == "/usr/bin/sudo", Ok());
        var sut = Sut(fake);

        sut.CreateBlockRules(Array.Empty<string>());
        sut.EnableBlockRules();

        Assert.False(File.Exists(_marker));
        Assert.Contains(fake.RunCalls, c => c.Arguments.Contains("-X") && c.Arguments.Contains("12345678"));

        File.WriteAllText(_marker, "anchor-v1");
        var orphanFake = new FakeProcessRunner();
        orphanFake.OnRun(IsAnchorFlush, new ProcessResult(0, "", "", TimeSpan.FromSeconds(10), TimedOut: true));
        orphanFake.OnRun(r => r.ExecutablePath == "/usr/bin/sudo", Ok());
        var orphanSut = Sut(orphanFake);

        orphanSut.CleanupOrphanedRules(null);
        Assert.True(File.Exists(_marker));
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

        releaseFails = true;
        sut.DisableBlockRules();
        Assert.False(File.Exists(_marker));
        Assert.Contains(fake.RunCalls, c => c.Arguments.Contains("-X") && c.Arguments.Contains("12345678"));

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
    public void UpdateCommittedConfig_FailedRefresh_RetainsAForRetry()
    {
        WriteConfig("198.51.100.1");
        var failRefresh = false;
        var injections = 0;
        var fake = new FakeProcessRunner();
        fake.OnRun(r => r.ExecutablePath == "/usr/bin/sudo" && r.Arguments.Contains("-E"),
            Ok(stderr: "Token : 12345678"));
        fake.OnRun(r => r.ExecutablePath == "/usr/bin/sudo" && r.Arguments.Contains("-sr"),
            Ok(stdout: "anchor \"com.apple/*\" all\nanchor \"" + Anchor + "\" all\n"));
        fake.OnRun(
            r => failRefresh && r.ExecutablePath == "/usr/bin/sudo" && IsAnchorLoad(r),
            _ =>
            {
                injections++;
                return Task.FromResult(Fail("pfctl error"));
            });
        fake.OnRun(r => r.ExecutablePath == "/usr/bin/sudo", Ok());
        var sut = Sut(fake);

        sut.CreateBlockRules(Array.Empty<string>(), isFullTunnel: true);
        sut.EnableBlockRules();
        Assert.True(sut.IsLoaded);
        Assert.True(sut.IsAnchorMode);
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
        Assert.DoesNotContain(fake.RunCalls.Skip(callsBeforeRetry), c => c.Arguments.Contains("-F"));
        Assert.DoesNotContain(fake.RunCalls.Skip(callsBeforeRetry), c => c.Arguments.Contains("-X"));
        Assert.Single(fake.RunCalls.Skip(callsBeforeRetry), IsAnchorLoad);
    }

    [Fact]
    public void UpdateCommittedConfig_MalformedJson_RetainsPriorList()
    {
        WriteConfig("198.51.100.1");
        var fake = OkRunner();
        var sut = Sut(fake);

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

        sut.UpdateCommittedConfig("{}", enabledForFullTunnel: true);

        Assert.Empty(sut.ServerIps);
    }
}
