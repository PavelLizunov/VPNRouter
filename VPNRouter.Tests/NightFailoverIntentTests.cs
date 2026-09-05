#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using VPNRouter.Core.Interfaces;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// NIGHT-06 regression tests: AutoFailover lifecycle and settings intent synchronization.
/// Verifies that failover pool and restart closure reflect the active user intent,
/// resets occur on public Start and successful Apply, stale delegate invocations are aborted
/// before teardown or persistence, and routine wire calls preserve tried cycle state.
/// </summary>
[Collection(SafeModeStateCollection.Name)]
public sealed class NightFailoverIntentTests
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

#pragma warning disable CS0618
    private static VpnEngine BuildEngine(
        NullWindowsDnsHardening? dns = null,
        FakeSplitTunnelDriver? splitDriver = null) =>
        new(
            scanner: new StubProcessScanner(),
            firewallFactory: () => new StubFirewallManager(),
            monitorFactory: () => new StubProcessMonitor(),
            logger: null,
            dnsHardening: dns ?? new NullWindowsDnsHardening(),
            splitDriver: splitDriver ?? new FakeSplitTunnelDriver());
#pragma warning restore CS0618

    private static IStartupHost CreateStartupHost(VpnEngine engine)
    {
        var hostType = typeof(VpnEngine).GetNestedType("VpnEngineStartupHost", BindingFlags.NonPublic);
        Assert.True(hostType != null, "VpnEngineStartupHost nested type not found on VpnEngine.");
        var host = Activator.CreateInstance(hostType, engine) as IStartupHost;
        Assert.True(host != null, "Could not instantiate VpnEngineStartupHost as IStartupHost.");
        return host;
    }

    private static void SetField(object target, string name, object? value)
    {
        var field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        Assert.True(field != null, $"Field '{name}' not found on {target.GetType().Name}");
        field.SetValue(target, value);
    }

    private static object? GetField(object target, string name)
    {
        var field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        Assert.True(field != null, $"Field '{name}' not found on {target.GetType().Name}");
        return field.GetValue(target);
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
                    new()
                    {
                        Name = activeServerName,
                        Server = "10.0.0.1",
                        Port = 443,
                        Uuid = Guid.NewGuid().ToString(),
                        Flow = "xtls-rprx-vision",
                        Security = "reality",
                    },
                    new()
                    {
                        Name = candidateServerName,
                        Server = "10.0.0.2",
                        Port = 443,
                        Uuid = Guid.NewGuid().ToString(),
                        Flow = "xtls-rprx-vision",
                        Security = "reality",
                    }
                }
            },
            Tun = new TunSettings(),
            Dns = new DnsSettings(),
            SingBox = new SingBoxSettings(),
            Monitoring = new MonitoringSettings(),
            ActiveProfile = "TestProfile",
        };

    [Fact]
    public void WireFailover_SameInternalWire_PreservesInstanceAndTriedCycle()
    {
        var dns = new NullWindowsDnsHardening();
        var fakeDriver = new FakeSplitTunnelDriver();
        using var engine = BuildEngine(dns, fakeDriver);

        var settingsA = CreateTestSettings("server-a1", "server-a2");
        engine.ResetFailoverContext(settingsA);

        var host = CreateStartupHost(engine);
        var sanity = new ConfigSanityCheck();

        var instanceA1 = host.WireFailover(sanity);
        var instanceA2 = host.WireFailoverWithStop(sanity);

        Assert.Same(instanceA1, instanceA2);

        // Mutate tried set in instanceA1 to verify routine wire calls do not reset cycle state
        var tried = (HashSet<string>)GetField(instanceA1, "_tried")!;
        tried.Add("server-a1");

        Assert.True(instanceA2.TriedServers.Contains("server-a1"));
    }

    [Fact]
    public void ResetFailoverContext_CommittedSettings_LazyNullUntilNewWireWithPoolBAndCapturedB()
    {
        var dns = new NullWindowsDnsHardening();
        var fakeDriver = new FakeSplitTunnelDriver();
        using var engine = BuildEngine(dns, fakeDriver);

        var settingsA = CreateTestSettings("server-a1", "server-a2");
        engine.ResetFailoverContext(settingsA);

        var host = CreateStartupHost(engine);
        var sanity = new ConfigSanityCheck();

        var instanceA = host.WireFailover(sanity);
        Assert.Same(instanceA, GetField(engine, "_failover"));

        // Commit intent B
        var settingsB = CreateTestSettings("server-b1", "server-b2");
        engine.ResetFailoverContext(settingsB);

        // 1. Immediately lazy null and updated context
        Assert.Null(GetField(engine, "_failover"));
        Assert.Same(settingsB, GetField(engine, "_failoverSettingsContext"));

        // 2. New wire creates fresh instance with pool B and captured settings B
        var instanceB = host.WireFailover(sanity);
        Assert.NotSame(instanceA, instanceB);
        Assert.Same(instanceB, GetField(engine, "_failover"));

        // Pool B identity
        var pool = GetField(instanceB, "_settings") as AppSettings;
        Assert.Same(settingsB, pool);

        // Restart delegate captured settings B and generation 2
        var restartDelegate = GetField(instanceB, "_restart") as Func<CancellationToken, Task<bool>>;
        Assert.NotNull(restartDelegate);
        Assert.NotNull(restartDelegate.Target);

        var closureSettings = restartDelegate.Target.GetType()
            .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(f => f.FieldType == typeof(AppSettings))
            ?.GetValue(restartDelegate.Target) as AppSettings;

        Assert.Same(settingsB, closureSettings);

        var closureGen = restartDelegate.Target.GetType()
            .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(f => f.FieldType == typeof(long))
            ?.GetValue(restartDelegate.Target);

        Assert.Equal(2L, closureGen);
    }

    [Fact]
    public async Task StaleAutoFailoverDelegate_InvocationAfterApplyOrReset_ReturnsFalseBeforeTeardown_RetainsManager_NoRunner_NoStore()
    {
        var priorDataDir = GetAppPathsDataDir();
        var tempDir = Path.Combine(Path.GetTempPath(), $"vpnrouter-night-failover-{Guid.NewGuid():N}");
        VPNRouter.Core.AppPaths.OverrideDataDir(tempDir);

        var store = new InMemorySettingsStore();
        var settingsA = CreateTestSettings("server-a1", "server-a2");
        store.Save(settingsA);
        var saveCountBefore = store.SaveCount;

        var runner = new FakeProcessRunner();
        var fakeHttp = new FakeHttpClient().Setup("/configs", "{}");
        var singBox = new SingBoxManager(
            new SingBoxSettings { ExecutablePath = "sing-box.exe", ClashApi = "127.0.0.1:9090" },
            null, fakeHttp, runner);

        var initialHandle = new FakeProcessHandle(pid: 24680);
        SetField(singBox, "_handle", initialHandle);
        typeof(SingBoxManager).GetProperty("State", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.SetValue(singBox, SingBoxState.Running);

        var dns = new NullWindowsDnsHardening();
        var splitDriver = new FakeSplitTunnelDriver();
        using var engine = BuildEngine(dns, splitDriver);

        SetField(engine, "_singBox", singBox);
        engine.EnterPostStartPhase();

        // Stale failover engine for intent A
        var sanity = new ConfigSanityCheck();
        var staleFailoverA = new AutoFailoverEngine(
            settingsA,
            sanity,
            restart: (ct) => engine.ExecuteProbeFailoverRestartAsync(settingsA, ct),
            logger: null,
            store: store);

        // Engine switches to intent B
        var settingsB = CreateTestSettings("server-b1", "server-b2");
        engine.ResetFailoverContext(settingsB);

        try
        {
            var outcome = await staleFailoverA.HandleDeadConfigAsync("dead config probe", CancellationToken.None);

            // Stale failover invocation returns false and rolls back in-memory selectors
            Assert.False(outcome.Switched);
            Assert.Null(outcome.NewActiveServer);

            // Teardown did NOT run
            Assert.Equal(0, dns.RestoreCount);

            // Fake manager reference retained
            Assert.Same(singBox, GetField(engine, "_singBox"));

            // No runner calls made
            Assert.Empty(runner.StartCalls);
            Assert.Empty(runner.RunCalls);

            // Store not updated (no persistence on false)
            Assert.Equal(saveCountBefore, store.SaveCount);
            Assert.Equal("server-a1", settingsA.Vless.ActiveServer);
        }
        finally
        {
            SetField(engine, "_singBox", null);
            SetField(singBox, "_handle", null);
            initialHandle.Dispose();
            singBox.Dispose();

            RestoreAppPathsDataDir(priorDataDir);
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task StaleAutoFailoverDelegate_PreStartRestart_ReturnsFalseBeforeStartAsyncInternal()
    {
        var dns = new NullWindowsDnsHardening();
        var splitDriver = new FakeSplitTunnelDriver();
        using var engine = BuildEngine(dns, splitDriver);

        var settingsA = CreateTestSettings("server-a1", "server-a2");
        var settingsB = CreateTestSettings("server-b1", "server-b2");

        // pre-start phase: _postStartPhase is false
        engine.ResetFailoverContext(settingsB);

        var result = await engine.ExecuteFailoverRestartAsync(settingsA, CancellationToken.None);

        Assert.False(result);
        Assert.False(engine.IsRunning);
        Assert.Equal(0, dns.RestoreCount);
    }

    [Fact]
    public async Task WireFailover_ResetSameObjectNewIntent_OldRestartReturnsFalse_NoRunnerNoTeardown()
    {
        var dns = new NullWindowsDnsHardening();
        var fakeDriver = new FakeSplitTunnelDriver();
        using var engine = BuildEngine(dns, fakeDriver);

        var runner = new FakeProcessRunner();
        var fakeHttp = new FakeHttpClient().Setup("/configs", "{}");
        var singBox = new SingBoxManager(
            new SingBoxSettings { ExecutablePath = "sing-box.exe", ClashApi = "127.0.0.1:9090" },
            null, fakeHttp, runner);

        var initialHandle = new FakeProcessHandle(pid: 24680);
        SetField(singBox, "_handle", initialHandle);
        typeof(SingBoxManager).GetProperty("State", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.SetValue(singBox, SingBoxState.Running);

        SetField(engine, "_singBox", singBox);
        engine.EnterPostStartPhase();

        // 1. Wire oldA with initial settings
        var settings = CreateTestSettings("server-a1", "server-a2");
        engine.ResetFailoverContext(settings);

        var host = CreateStartupHost(engine);
        var sanity = new ConfigSanityCheck();
        var failoverOld = host.WireFailover(sanity);

        var oldRestart = GetField(failoverOld, "_restart") as Func<CancellationToken, Task<bool>>;
        Assert.NotNull(oldRestart);

        // 2. Reset SAME AppSettings object for new intent (e.g. Apply / Start reuses existing instance)
        settings.Vless.ActiveServer = "server-a2";
        engine.ResetFailoverContext(settings);

        // Verify settings reference is indeed the exact SAME object (ReferenceEquals alone would pass)
        Assert.Same(settings, GetField(engine, "_failoverSettingsContext"));

        try
        {
            // 3. Invoke old _restart closure from previous intent
            var result = await oldRestart(CancellationToken.None);

            // Stale generation must cause restart to abort
            Assert.False(result);

            // Teardown did NOT run (RestoreCount is 0)
            Assert.Equal(0, dns.RestoreCount);

            // Fake manager reference retained
            Assert.Same(singBox, GetField(engine, "_singBox"));

            // No runner calls made
            Assert.Empty(runner.StartCalls);
            Assert.Empty(runner.RunCalls);
        }
        finally
        {
            SetField(engine, "_singBox", null);
            SetField(singBox, "_handle", null);
            initialHandle.Dispose();
            singBox.Dispose();
        }
    }

    [Fact]
    public async Task WireFailover_PreStart_ResetSameObjectNewIntent_OldRestartReturnsFalse_NoRunner()
    {
        var dns = new NullWindowsDnsHardening();
        var fakeDriver = new FakeSplitTunnelDriver();
        using var engine = BuildEngine(dns, fakeDriver);

        // Pre-start: _postStartPhase is false
        var settings = CreateTestSettings("server-a1", "server-a2");
        engine.ResetFailoverContext(settings);

        var host = CreateStartupHost(engine);
        var sanity = new ConfigSanityCheck();
        var failoverOld = host.WireFailover(sanity);

        var oldRestart = GetField(failoverOld, "_restart") as Func<CancellationToken, Task<bool>>;
        Assert.NotNull(oldRestart);

        // Reset SAME object with new intent
        settings.Vless.ActiveServer = "server-a2";
        engine.ResetFailoverContext(settings);

        Assert.Same(settings, GetField(engine, "_failoverSettingsContext"));

        var result = await oldRestart(CancellationToken.None);

        Assert.False(result);
        Assert.False(engine.IsRunning);
        Assert.Equal(0, dns.RestoreCount);
    }

    [Fact]
    public void PublicStartBoundary_SourceOrder_ResetsFailoverContextAfterAlreadyRunningGuardAndBeforeStartAsyncInternal()
    {
        var source = LoadVpnEngineSource();
        Assert.True(source != null, "VpnEngine.cs source could not be loaded.");

        var clean = StripComments(source);

        var startIdx = clean.IndexOf("public async Task StartAsync(", StringComparison.Ordinal);
        Assert.True(startIdx >= 0, "Public StartAsync method must exist.");

        var runningGuardIdx = clean.IndexOf("HasLiveOrStartingSingBox()", startIdx, StringComparison.Ordinal);
        var resetIdx = clean.IndexOf("ResetFailoverContext(settings);", startIdx, StringComparison.Ordinal);
        var internalIdx = clean.IndexOf("await StartAsyncInternal(", startIdx, StringComparison.Ordinal);

        Assert.True(runningGuardIdx > startIdx, "Already-running guard must appear inside public StartAsync.");
        Assert.True(resetIdx > runningGuardIdx, "ResetFailoverContext must appear AFTER the already-running guard.");
        Assert.True(internalIdx > resetIdx, "ResetFailoverContext must appear BEFORE StartAsyncInternal.");
    }

    private static string? LoadVpnEngineSource()
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        for (var depth = 0; depth < 8 && directory != null; depth++, directory = directory.Parent)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "VPNRouter.Core",
                "Services",
                "VpnEngine.cs");
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);
        }
        return null;
    }

    private static string StripComments(string source)
    {
        var noBlock = Regex.Replace(source, @"/\*.*?\*/", "", RegexOptions.Singleline);
        var noLine = Regex.Replace(noBlock, @"//.*", "");
        return noLine;
    }
}
