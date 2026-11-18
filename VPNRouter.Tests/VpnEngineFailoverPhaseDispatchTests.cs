#nullable enable

using System.IO;
using System.Text.RegularExpressions;
using VPNRouter.Core.Interfaces;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;

namespace VPNRouter.Tests;

[Collection(SafeModeStateCollection.Name)]
public sealed class VpnEngineFailoverPhaseDispatchTests
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
#pragma warning disable CS0067 // stub implements the interface; the events are never raised
        public event EventHandler<ProcessEventArgs>? ProcessStarted;
        public event EventHandler<ProcessEventArgs>? ProcessStopped;
#pragma warning restore CS0067
        public void Start() { }
        public void Stop() { }
        public void Dispose() { }
    }

#pragma warning disable CS0618
    private static VpnEngine BuildEngine(NullWindowsDnsHardening dns) =>
        new VpnEngine(
            scanner: new StubProcessScanner(),
            firewallFactory: () => new StubFirewallManager(),
            monitorFactory: () => new StubProcessMonitor(),
            logger: null,
            dnsHardening: dns);
#pragma warning restore CS0618

    private static AppSettings BuildEmptyServersSettings() =>
        new()
        {
            App = new AppConfig
            {
                ConfigMode = "subscribe",
                RoutingMode = "split",
                FlushDnsOnStart = false,
                BypassRussianTraffic = false,
                Subscriptions = new List<SubscriptionEntry>(),
            },
            Vless = new VlessConfig(),
            Tun = new TunSettings(),
            Dns = new DnsSettings(),
            SingBox = new SingBoxSettings(),
            Monitoring = new MonitoringSettings(),
            ActiveProfile = "TestProfile",
        };

    [Fact]
    public async Task ExecuteFailoverRestart_PostStartPhase_RoutesThroughSafeTeardownPath()
    {
        var dns = new NullWindowsDnsHardening();
        using var engine = BuildEngine(dns);
        var settings = BuildEmptyServersSettings();

        engine.EnterPostStartPhase();

        var result = await engine.ExecuteFailoverRestartAsync(settings, CancellationToken.None);

        Assert.False(result, "fresh idle engine has no session — safe path must abort after teardown");
        Assert.False(engine.IsRunning);
        Assert.True(dns.RestoreCount >= 1,
            "TeardownInternal must run on the safe gated path (RestoreCount proves teardown executed). " +
            $"Actual RestoreCount: {dns.RestoreCount}");
    }

    [Fact]
    public async Task ExecuteProbeFailoverRestart_StaleCapturedSettings_ReturnsFalseWithoutTeardown()
    {
        var dns = new NullWindowsDnsHardening();
        using var engine = BuildEngine(dns);
        var settingsA = BuildEmptyServersSettings();
        var settingsB = BuildEmptyServersSettings();

        engine.EnterPostStartPhase();
        engine.ResetFailoverContext(settingsB);

        var result = await engine.ExecuteProbeFailoverRestartAsync(settingsA, CancellationToken.None);

        Assert.False(result, "stale captured settings must be rejected when active failover context differs");
        Assert.Equal(0, dns.RestoreCount);
    }

    [Fact]
    public async Task ExecuteFailoverRestart_PreStart_StaleCapturedSettings_ReturnsFalseBeforeStartAsyncInternal()
    {
        var dns = new NullWindowsDnsHardening();
        using var engine = BuildEngine(dns);
        var settingsA = BuildEmptyServersSettings();
        var settingsB = BuildEmptyServersSettings();

        engine.ResetFailoverContext(settingsB);

        var result = await engine.ExecuteFailoverRestartAsync(settingsA, CancellationToken.None);

        Assert.False(result, "pre-start failover restart must be rejected when captured settings do not match active context");
        Assert.False(engine.IsRunning);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task QueuedStartupFailover_DiscardsReplacedChildOrCancelledProbe(bool cancelProbe, bool selecting)
    {
        const System.Reflection.BindingFlags flags =
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var dns = new NullWindowsDnsHardening();
        using var engine = BuildEngine(dns);
        var settings = BuildEmptyServersSettings();
        settings.Vless.ActiveServer = "active";
        settings.App.ActiveSubscriptionServer = "active";
        settings.Vless.Servers = [
            new VlessServerEntry { Name = "active", Server = "active.invalid", Port = 443 },
            new VlessServerEntry { Name = "standby", Server = "standby.invalid", Port = 443 },
            new VlessServerEntry { Name = "other", Server = "other.invalid", Port = 443 }
        ];
        engine.ResetFailoverContext(settings);
        var choosing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var verdict = new TaskCompletionSource<ServerProbeResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var failover = new AutoFailoverEngine(settings, new ConfigSanityCheck(), store: new InMemorySettingsStore())
        {
            IsCurrentIntent = () => true,
            ProbeCandidate = (_, _) =>
            {
                choosing.TrySetResult();
                return verdict.Task;
            }
        };
        var generation = Assert.IsType<long>(typeof(VpnEngine).GetField("_failoverGeneration", flags)!.GetValue(engine));
        var gate = Assert.IsType<SemaphoreSlim>(typeof(VpnEngine).GetField("_lifecycleGate", flags)!.GetValue(engine));
        var runner = new FakeProcessRunner().OnRun(_ => true, new ProcessResult(0, "", "", TimeSpan.Zero, false));
        var manager = new SingBoxManager(settings.SingBox, http: new FakeHttpClient(), runner: runner);
        using var original = new FakeProcessHandle(401);
        using var rollback = new FakeProcessHandle(original.Pid);
        var child = typeof(SingBoxManager).GetField("_handle", flags)!;
        var managerField = typeof(VpnEngine).GetField("_singBox", flags)!;
        child.SetValue(manager, original);
        managerField.SetValue(engine, manager);
        using var probe = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var previousDiagnostics = TunAdapterDiagnostics.Runner;
        TunAdapterDiagnostics.Runner = runner;
        try
        {
            // Exercise both an outstanding Apply and a process change during asynchronous candidate selection.
            if (!selecting) await gate.WaitAsync(probe.Token);
            Task<FailoverOutcome?> pending;
            try
            {
                pending = engine.HandlePostStartFailureAsync(failover, "fixture", settings, generation, original, probe.Token);
                if (selecting) await choosing.Task.WaitAsync(TimeSpan.FromSeconds(5), probe.Token);
                Assert.False(pending.IsCompleted);
                if (cancelProbe) probe.Cancel();
                else child.SetValue(manager, rollback);
            }
            finally
            {
                verdict.TrySetResult(new ServerProbeResult(ServerProbeStatus.Ok, 1, null));
                if (!selecting) gate.Release();
            }

            var outcome = await pending.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            if (selecting) Assert.False(Assert.IsType<FailoverOutcome>(outcome).Switched);
            else Assert.Null(outcome);
            Assert.Same(cancelProbe ? original : rollback, manager.OwnedProcessHandle);
            Assert.Equal(0, original.KillCallCount);
            Assert.Equal(0, rollback.KillCallCount);
            Assert.Equal(0, dns.RestoreCount);
            Assert.Empty(runner.StartCalls);
            Assert.Empty(failover.TriedServers);
            Assert.Equal("active", settings.Vless.ActiveServer);
            Assert.Equal("active", settings.App.ActiveSubscriptionServer);
        }
        finally
        {
            child.SetValue(manager, null);
            managerField.SetValue(engine, null);
            try { manager.Dispose(); }
            finally { TunAdapterDiagnostics.Runner = previousDiagnostics; }
        }
    }

    private static string StripComments(string source)
    {
        var noBlock = Regex.Replace(source, @"/\*.*?\*/", "", RegexOptions.Singleline);
        var noLine = Regex.Replace(noBlock, @"//.*", "");
        return noLine;
    }

    private static string LoadVpnEngineSource()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        for (var i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "VPNRouter.Core", "Services", "VpnEngine.cs");
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
        }
        throw new FileNotFoundException("Could not locate VPNRouter.Core/Services/VpnEngine.cs");
    }

    private static string ExtractWireFailoverCore(string src)
    {
        var m = Regex.Match(src, @"AutoFailoverEngine\s+WireFailoverCore\s*\(");
        Assert.True(m.Success,
            "WireFailoverCore declaration not found — both wire methods must share one core so the " +
            "??= slot installs a single phase-aware delegate (P02 FAIL-1).");
        var brace = src.IndexOf('{', m.Index + m.Length);
        Assert.True(brace >= 0, "WireFailoverCore must have a body installing the shared delegate.");
        int depth = 1, i = brace + 1;
        while (i < src.Length && depth > 0) { if (src[i] == '{') depth++; else if (src[i] == '}') depth--; i++; }
        return src.Substring(brace, i - brace);
    }
}
