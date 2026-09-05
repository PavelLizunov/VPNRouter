#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using VPNRouter.Core.Platform.Linux;
using VPNRouter.Core.Platform.macOS;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// Baseline-compatible behavioral witness for NIGHT-05 IPv6 endpoint kill-switch bypass.
/// Verifies that dual-stack server endpoints allow IPv6 reconnect traffic in generated firewall rules.
/// Old public API only: CreateBlockRules + EnableBlockRules. Expected RED unexecuted on pre-fix baseline.
/// </summary>
public sealed class NightBaselineEndpointTests
{
    private static ProcessResult OkResult(string stdout = "", string stderr = "") =>
        new ProcessResult(0, stdout, stderr, TimeSpan.Zero, false);

    private const string OutboundConfigJson = """
        {
          "outbounds": [
            {
              "type": "vless",
              "tag": "proxy",
              "server": "relay.example.test"
            }
          ]
        }
        """;

    [Fact]
    public void Night05_Linux_DualStackHostname_IncludesIpv6InRuleset()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "night05-linux-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        var configPath = Path.Combine(tempDir, "current.json");
        var markerPath = Path.Combine(tempDir, "nft-killswitch-engaged.marker");
        var rulesetPath = Path.Combine(tempDir, "vpnrouter-nft-killswitch.conf");

        File.WriteAllText(configPath, OutboundConfigJson);

        var dnsCallCount = 0;
        IReadOnlyList<string> FakeResolver(string host)
        {
            dnsCallCount++;
            return new[] { "198.51.100.8", "2001:db8::8" };
        }

        var fakeRunner = new FakeProcessRunner();
        fakeRunner.OnRun(_ => true, _ => Task.FromResult(OkResult()));

        LinuxFirewallManager? sut = null;
        try
        {
            sut = new LinuxFirewallManager(
                logger: null,
                runner: fakeRunner,
                currentConfigPath: configPath,
                markerPath: markerPath,
                hostResolver: FakeResolver,
                rulesetPath: rulesetPath);

            sut.CreateBlockRules(Array.Empty<string>(), isFullTunnel: true);
            sut.EnableBlockRules();

            Assert.True(dnsCallCount > 0, "Fake DNS resolver call count must be positive.");
            Assert.NotEmpty(fakeRunner.RunCalls);

            Assert.True(File.Exists(rulesetPath), "Generated ruleset file must exist before dispose.");
            var generatedRules = File.ReadAllText(rulesetPath);

            Assert.Contains("2001:db8::8", generatedRules, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { sut?.Dispose(); } catch { }
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Night05_Mac_DualStackHostname_IncludesIpv6InRuleset()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "night05-mac-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        var configPath = Path.Combine(tempDir, "current.json");
        var markerPath = Path.Combine(tempDir, "pf-killswitch-engaged.marker");
        var pfConfPath = Path.Combine(tempDir, "pf.conf");
        var rulesPath = Path.Combine(tempDir, "vpnrouter-pf-killswitch.conf");
        var mainConfPath = Path.Combine(tempDir, "vpnrouter-pf-main.conf");

        File.WriteAllText(configPath, OutboundConfigJson);
        File.WriteAllText(pfConfPath, $"anchor \"{MacFirewallManager.Anchor}\"\n");

        var dnsCallCount = 0;
        IReadOnlyList<string> FakeResolver(string host)
        {
            dnsCallCount++;
            return new[] { "198.51.100.8", "2001:db8::8" };
        }

        var fakeRunner = new FakeProcessRunner();
        fakeRunner.OnRun(
            req => req.Arguments.Contains("-E"),
            _ => Task.FromResult(OkResult("Token : 12345678", "Token : 12345678")));
        fakeRunner.OnRun(
            req => req.Arguments.Contains("-sr"),
            _ => Task.FromResult(OkResult($"anchor \"{MacFirewallManager.Anchor}\"\n")));
        fakeRunner.OnRun(_ => true, _ => Task.FromResult(OkResult()));

        MacFirewallManager? sut = null;
        try
        {
            sut = new MacFirewallManager(
                logger: null,
                runner: fakeRunner,
                currentConfigPath: configPath,
                markerPath: markerPath,
                hostResolver: FakeResolver,
                pfConfPath: pfConfPath,
                rulesPath: rulesPath,
                mainConfPath: mainConfPath);

            sut.CreateBlockRules(Array.Empty<string>(), isFullTunnel: true);
            sut.EnableBlockRules();

            Assert.True(dnsCallCount > 0, "Fake DNS resolver call count must be positive.");
            Assert.NotEmpty(fakeRunner.RunCalls);

            Assert.True(File.Exists(rulesPath), "Generated rules file must exist before dispose.");
            var generatedRules = File.ReadAllText(rulesPath);

            Assert.Contains("2001:db8::8", generatedRules, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { sut?.Dispose(); } catch { }
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }
}
