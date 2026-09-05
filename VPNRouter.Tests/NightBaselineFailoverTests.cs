#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using VPNRouter.Core.Interfaces;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// Baseline-compatible characterization test for NIGHT-06 failover pool invalidation.
/// Verifies that public VpnEngine.Stop() invalidates the cached AutoFailoverEngine instance.
/// On baseline, Stop() leaves _failover untouched (expected RED). Fixed resets _failover to null.
/// </summary>
[Collection(SafeModeStateCollection.Name)]
public sealed class NightBaselineFailoverTests
{
    private sealed class StubProcessScanner : IProcessScanner
    {
        public ScanResult ScanForProfile(Profile profile) => new();
    }

    private sealed class StubFirewallManager : IFirewallManager
    {
        public void CreateBlockRules(IEnumerable<string> processNames, bool isFullTunnel = true) { }
        public void EnableBlockRules() { }
        public void DisableBlockRules() { }
        public void DeleteAllRules() { }
        public void Dispose() { }
    }

    private sealed class StubProcessMonitor : IProcessMonitor
    {
        public event EventHandler<ProcessEventArgs>? ProcessStarted { add { } remove { } }
        public event EventHandler<ProcessEventArgs>? ProcessStopped { add { } remove { } }
        public void Start() { }
        public void Stop() { }
        public void Dispose() { }
    }

    private static string? GetAppPathsDataDir()
    {
        var f = typeof(VPNRouter.Core.AppPaths).GetField("_dataDir", BindingFlags.Static | BindingFlags.NonPublic)
             ?? typeof(VPNRouter.Core.AppPaths).GetField("_dataDirOverride", BindingFlags.Static | BindingFlags.NonPublic);
        return (string?)f?.GetValue(null);
    }

    private static void RestoreAppPathsDataDir(string? priorDataDir)
    {
        var f = typeof(VPNRouter.Core.AppPaths).GetField("_dataDir", BindingFlags.Static | BindingFlags.NonPublic)
             ?? typeof(VPNRouter.Core.AppPaths).GetField("_dataDirOverride", BindingFlags.Static | BindingFlags.NonPublic);
        f?.SetValue(null, priorDataDir);
    }

    private static AppSettings CreateTestSettings(string activeServerName, string candidateServerName) =>
        new()
        {
            App = new AppConfig
            {
                ConfigMode = "generated",
                RoutingMode = "split",
                FlushDnsOnStart = false,
                BypassRussianTraffic = false,
                Subscriptions = new List<SubscriptionEntry>(),
            },
            Vless = new VlessConfig
            {
                ActiveServer = activeServerName,
                Servers = new List<VlessServerEntry>
                {
                    new() { Name = activeServerName, Server = "10.0.0.1", Port = 443 },
                    new() { Name = candidateServerName, Server = "10.0.0.2", Port = 443 }
                }
            },
            Tun = new TunSettings(),
            Dns = new DnsSettings(),
            SingBox = new SingBoxSettings(),
            Monitoring = new MonitoringSettings(),
            ActiveProfile = "TestProfile",
        };

    [Fact]
    public void Night06_PublicStop_InvalidatesPreviousFailoverPool()
    {
        var priorDataDir = GetAppPathsDataDir();
        var tempDir = Path.Combine(Path.GetTempPath(), $"vpnrouter-baseline-failover-{Guid.NewGuid():N}");
        VPNRouter.Core.AppPaths.OverrideDataDir(tempDir);

        var fakeDns = new NullWindowsDnsHardening();
        var fakeDriver = new FakeSplitTunnelDriver();
        VpnEngine? engine = null;

        try
        {
#pragma warning disable CS0618
            engine = new VpnEngine(
                scanner: new StubProcessScanner(),
                firewallFactory: () => new StubFirewallManager(),
                monitorFactory: () => new StubProcessMonitor(),
                logger: null,
                dnsHardening: fakeDns,
                splitDriver: fakeDriver);
#pragma warning restore CS0618

            var settings = CreateTestSettings("server-a", "server-b");

            var hostType = typeof(VpnEngine).GetNestedType("VpnEngineStartupHost", BindingFlags.NonPublic);
            Assert.NotNull(hostType);
            var hostObj = Activator.CreateInstance(hostType, engine);
            Assert.NotNull(hostObj);

            var captureMethod = hostType.GetMethod("CaptureSettings", BindingFlags.Public | BindingFlags.Instance);
            Assert.NotNull(captureMethod);
            captureMethod.Invoke(hostObj, new object[] { settings });

            var host = (IStartupHost)hostObj;
            var sanityCheck = new ConfigSanityCheck();
            var failover = host.WireFailover(sanityCheck);

            var failoverField = typeof(VpnEngine).GetField("_failover", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(failoverField);

            var preStopFailover = failoverField.GetValue(engine);
            Assert.Same(failover, preStopFailover);

            engine.Stop();

            // Under baseline, Stop leaves _failover untouched (expected RED).
            // Under fixed code, Stop sets _failover = null.
            Assert.Null(failoverField.GetValue(engine));

            Assert.Contains(fakeDns.Calls, c => c.Op == "Restore");
            Assert.True(fakeDriver.DisengageCount >= 1);

            engine.Dispose();
            Assert.True(fakeDriver.DisposeCount >= 1);
        }
        finally
        {
            try { engine?.Dispose(); } catch { }
            RestoreAppPathsDataDir(priorDataDir);
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }
}
