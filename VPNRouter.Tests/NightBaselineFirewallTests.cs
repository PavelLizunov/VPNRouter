using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using VPNRouter.Core.Platform.Linux;
using VPNRouter.Core.Platform.macOS;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// Behavioral regression tests for NIGHT04 kill-switch marker retention.
/// Verifies that failed orphan cleanup retains the crash-recovery sentinel marker,
/// and a subsequent successful cleanup on the same manager instance clears it.
/// </summary>
public sealed class NightBaselineFirewallTests
{
    private static ProcessResult OkResult() =>
        new ProcessResult(0, string.Empty, string.Empty, TimeSpan.Zero, false);

    private static ProcessResult FailResult(string stderr = "command failed") =>
        new ProcessResult(1, string.Empty, stderr, TimeSpan.Zero, false);

    [Fact]
    public void Night04_Linux_CleanupOrphanedRules_FailedDelete_RetainsMarker_AndRecoveredSuccess_ClearsMarker()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "night04-linux-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        var configPath = Path.Combine(tempDir, "current.json");
        var markerPath = Path.Combine(tempDir, "nft-killswitch-engaged.marker");
        var rulesetPath = Path.Combine(tempDir, "vpnrouter-nft-killswitch.conf");

        File.WriteAllText(configPath, @"{ ""outbounds"": [] }");
        File.WriteAllText(markerPath, "engaged");

        var fail = true;
        var failureCount = 0;
        var commandHitCount = 0;

        var fakeRunner = new FakeProcessRunner();
        fakeRunner.OnRun(
            req => req.ExecutablePath == "/usr/bin/sudo" &&
                   req.Arguments.Contains("nft") &&
                   (req.Arguments.Contains("delete") || req.Arguments.Contains("list")),
            req =>
            {
                commandHitCount++;
                if (fail)
                {
                    failureCount++;
                    return Task.FromResult(FailResult("nft delete/inventory failed"));
                }
                return Task.FromResult(OkResult());
            });

        // Fallback: all other commands are intercepted with fake OK (no physical process spawned).
        fakeRunner.OnRun(_ => true, req => Task.FromResult(OkResult()));

        LinuxFirewallManager? sut = null;
        try
        {
            sut = new LinuxFirewallManager(
                logger: null,
                runner: fakeRunner,
                currentConfigPath: configPath,
                markerPath: markerPath,
                hostResolver: _ => Array.Empty<string>(),
                rulesetPath: rulesetPath);

            // First cleanup attempt: fake delete/inventory fails.
            sut.CleanupOrphanedRules(null);

            Assert.True(failureCount > 0, "Expected failure count must be greater than 0 before checking marker retention.");
            Assert.True(File.Exists(markerPath), "Engaged marker must be retained when Linux table cleanup fails (baseline shouldfail).");

            // Same instance recovery: subsequent cleanup succeeds.
            fail = false;
            var hitsBeforeRecovery = commandHitCount;

            sut.CleanupOrphanedRules(null);

            Assert.True(commandHitCount > hitsBeforeRecovery, "Expected command hit counter must increase upon recovery cleanup.");
            Assert.False(File.Exists(markerPath), "Engaged marker must be removed once table cleanup succeeds.");
        }
        finally
        {
            try { sut?.Dispose(); } catch { }
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Night04_Mac_CleanupOrphanedRules_FailedAnchorFlush_RetainsMarker_AndRecoveredSuccess_ClearsMarker()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "night04-mac-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        var configPath = Path.Combine(tempDir, "current.json");
        var markerPath = Path.Combine(tempDir, "pf-killswitch-engaged.marker");
        var pfConfPath = Path.Combine(tempDir, "pf.conf");
        var rulesPath = Path.Combine(tempDir, "vpnrouter-pf-killswitch.conf");
        var mainConfPath = Path.Combine(tempDir, "vpnrouter-pf-main.conf");

        File.WriteAllText(configPath, @"{ ""outbounds"": [] }");
        File.WriteAllText(pfConfPath, "anchor \"com.apple/*\"\n");
        File.WriteAllText(markerPath, MacFirewallManager.AnchorMarker);

        var fail = true;
        var failureCount = 0;
        var commandHitCount = 0;

        var fakeRunner = new FakeProcessRunner();
        fakeRunner.OnRun(
            req => req.ExecutablePath == "/usr/bin/sudo" &&
                   req.Arguments.Contains("/sbin/pfctl") &&
                   req.Arguments.Contains("-a") &&
                   req.Arguments.Contains(MacFirewallManager.Anchor) &&
                   req.Arguments.Contains("-F"),
            req =>
            {
                commandHitCount++;
                if (fail)
                {
                    failureCount++;
                    return Task.FromResult(FailResult("pfctl anchor flush failed"));
                }
                return Task.FromResult(OkResult());
            });

        // Fallback: all other commands are intercepted with fake OK (no physical process spawned).
        fakeRunner.OnRun(_ => true, req => Task.FromResult(OkResult()));

        MacFirewallManager? sut = null;
        try
        {
            sut = new MacFirewallManager(
                logger: null,
                runner: fakeRunner,
                currentConfigPath: configPath,
                markerPath: markerPath,
                hostResolver: _ => Array.Empty<string>(),
                pfConfPath: pfConfPath,
                rulesPath: rulesPath,
                mainConfPath: mainConfPath);

            // First cleanup attempt: anchor flush fails.
            sut.CleanupOrphanedRules(null);

            Assert.True(failureCount > 0, "Expected failure count must be greater than 0 before checking marker retention.");
            Assert.True(File.Exists(markerPath), "Engaged marker must be retained when Mac anchor flush fails (baseline shouldfail).");

            // Same instance recovery: subsequent cleanup succeeds.
            fail = false;
            var hitsBeforeRecovery = commandHitCount;

            sut.CleanupOrphanedRules(null);

            Assert.True(commandHitCount > hitsBeforeRecovery, "Expected command hit counter must increase upon recovery cleanup.");
            Assert.False(File.Exists(markerPath), "Engaged marker must be removed once anchor flush succeeds.");
        }
        finally
        {
            try { sut?.Dispose(); } catch { }
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }
}
