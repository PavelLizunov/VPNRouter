using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VPNRouter.Core;
using VPNRouter.Core.Interfaces;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;

namespace VPNRouter.Tests;

public sealed class BusinessPolicyEngineTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Apply_OnLiveOrRecoveringSession_ThenHealthRecovery_PreservesRemovedSite(bool crashBeforeApply)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        fixture.ProcessScanner.Fail = false;
        Set(fixture.Manager, "_handle", null);
        Set(fixture.Manager, "<State>k__BackingField", SingBoxState.Stopped);
        var loaded = new List<string>();
        var handles = new List<FakeProcessHandle>();
        fixture.Runner.OnStart(_ => true, request =>
        {
            var json = File.ReadAllText(request.Arguments[^1]);
            loaded.Add(json);
            using var config = JsonDocument.Parse(json);
            File.AppendAllText(config.RootElement.GetProperty("log").GetProperty("output").GetString()!,
                "INFO sing-box started (0.01s)\n");
            var handle = new FakeProcessHandle(100 + handles.Count);
            handles.Add(handle);
            return handle;
        });
        var first = GeneratedSettings(fixture.Executable, 7, includeOldSite: true);
        var next = GeneratedSettings(fixture.Executable, 8, includeOldSite: false);
        var profile = new Profile { Name = "Fixture", DnsMode = "vpn_only" };
        var originalJson = ConfigPipeline.Generate(profile, [], first, ConfigPipeline.ValidationMode.Strict);
        var hostType = typeof(VpnEngine).GetNestedType("VpnEngineStartupHost", BindingFlags.NonPublic)!;
        var host = Activator.CreateInstance(hostType, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
            null, [fixture.Engine], null)!;
        hostType.GetMethod("SetSingBoxManager")!.Invoke(host, [fixture.Manager]);
        fixture.Manager.StartWithPolicyJson(originalJson, first.BusinessPolicy!, fixture.Session.Token);
        Set(fixture.Engine, "_failoverSettingsContext", first);
        var api = new FakeSingBoxApi();
        using var monitor = new HealthMonitor(fixture.Manager, fixture.ProcessScanner, new NoopFirewall(), first.Monitoring,
            api: api, dnsHardening: new NullWindowsDnsHardening());
        monitor.Start(profile, first, new ScanResult());
        Set(fixture.Engine, "_healthMonitor", monitor);
        try
        {
            if (crashBeforeApply)
            {
                handles[^1].SignalExit(137);
                Assert.Equal(SingBoxState.Failed, fixture.Manager.State);
                Assert.Same(fixture.Session, fixture.Engine.CapturePolicySession());
            }
            var applied = await fixture.Engine.ApplyPolicyAsync(next, fixture.Session, TestContext.Current.CancellationToken);
            Assert.NotNull(applied);
            Assert.Equal(8, applied.Revision);
            Assert.Equal(next.BusinessPolicy!.PayloadDigest, applied.PayloadDigest);
            Assert.True(fixture.Engine.IsPolicyReceiptCurrent(applied));
            Assert.Equal(2, loaded.Count);
            Assert.Contains("old.example.com", loaded[0]);
            Assert.DoesNotContain("old.example.com", loaded[1]);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(loaded[1]))), applied.ConfigurationDigest);

            // This is the real stale monitor, still retaining the N settings.
            var staleWrite = (bool)typeof(HealthMonitor).GetMethod("TryHotReloadViaApi", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(monitor, [originalJson])!;
            Assert.False(staleWrite);
            Assert.DoesNotContain(api.Calls, call => call.Method == "Reload");
            var recovered = new TaskCompletionSource<BusinessPolicyAppliedReceipt>(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.Engine.BusinessPolicyApplied += receipt =>
            {
                if (receipt.LaunchId != applied.LaunchId) recovered.TrySetResult(receipt);
            };
            handles[^1].SignalExit(137);
            var recovery = await recovered.Task.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
            Assert.Equal(3, loaded.Count);
            Assert.DoesNotContain("old.example.com", loaded[2]);
            Assert.Equal(8, recovery.Revision);
            Assert.Equal(next.BusinessPolicy.PayloadDigest, recovery.PayloadDigest);
            Assert.True(fixture.Engine.IsPolicyReceiptCurrent(recovery));
            Assert.False(fixture.Engine.IsPolicyReceiptCurrent(applied));
            Assert.NotEqual(applied.LaunchId, recovery.LaunchId);
        }
        finally
        {
            monitor.Dispose();
            fixture.Engine.Stop();
        }
    }

    [Fact]
    public async Task Start_DeadCoreReplacement_CancelsAndDisposesPriorOwners()
    {
        using var fixture = new Fixture();
        fixture.Handle.SignalExit(1);
        var settings = new AppSettings { App = new AppConfig { BypassRussianTraffic = false, FlushDnsOnStart = false } };
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Engine.StartAsync(
            settings, TestContext.Current.CancellationToken, skipVpnConflictCheck: true));
        Assert.True(fixture.Session.Token.IsCancellationRequested);
        Assert.True(fixture.Handle.DisposeCallCount > 0);
        Assert.False(fixture.Engine.IsRunning);
        Assert.Empty(fixture.Runner.StartCalls);
    }

    [Fact]
    public async Task Session_EmbeddedRulesWithoutAcceptedPolicy_CanCaptureFirstImmediateUpdate()
    {
        using var fixture = new Fixture();
        fixture.SetManager(null);
        var settings = new AppSettings { App = new AppConfig { BypassRussianTraffic = false, FlushDnsOnStart = false } };
        Assert.Null(settings.BusinessPolicy);
        // Stop before any network/process phase, then complete the real host's
        // Started callback. This pins session creation for unprovisioned starts.
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Engine.StartAsync(
            settings, TestContext.Current.CancellationToken, skipVpnConflictCheck: true));
        Assert.True(fixture.Session.Token.IsCancellationRequested);
        Assert.Null(fixture.Engine.CapturePolicySession());
        var hostType = typeof(VpnEngine).GetNestedType("VpnEngineStartupHost", BindingFlags.NonPublic)!;
        var host = Activator.CreateInstance(hostType, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
            null, [fixture.Engine], null)!;
        hostType.GetMethod("SetSingBoxManager")!.Invoke(host, [fixture.Manager]);
        hostType.GetMethod("OnSingBoxStarted")!.Invoke(host, [fixture.Handle.Pid]);
        var session = fixture.Engine.CapturePolicySession();
        Assert.NotNull(session);
        fixture.SetManager(null);
        Assert.Same(session, fixture.Engine.CapturePolicySession());
        session.Cancellation.Cancel();
        Assert.Null(fixture.Engine.CapturePolicySession());
    }

    [Fact]
    public void Session_NeverStartedOrReplaced_CannotCapturePriorIntent()
    {
        using var fixture = new Fixture();
        fixture.Session.HasRun = false;
        Assert.Null(fixture.Engine.CapturePolicySession());
        fixture.Session.HasRun = true;
        using var replacement = new CancellationTokenSource();
        Set(fixture.Engine, "_sessionCts", replacement);
        Assert.Null(fixture.Engine.CapturePolicySession());
    }

    [Fact]
    public async Task Apply_QueuedBeforeUserStop_CannotRestartStoppedSession()
    {
        using var fixture = new Fixture();
        await fixture.Gate.WaitAsync(TestContext.Current.CancellationToken);
        var apply = fixture.Engine.ApplyPolicyAsync(fixture.Settings(), fixture.Session);
        Assert.False(apply.IsCompleted);
        // Avoid platform cleanup in this gate-race test; the captured session is
        // still live until the actual engine Stop cancels it below.
        fixture.SetManager(null);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = fixture.Session.Token.Register(() => canceled.TrySetResult());
        var stop = Task.Run(fixture.Engine.Stop);
        await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        fixture.Gate.Release();
        Assert.Null(await apply.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        await stop.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Null(fixture.Engine.CapturePolicySession());
        Assert.Empty(fixture.Runner.StartCalls);
    }

    [Fact]
    public async Task Apply_OldQueuedRequest_CannotTargetNewSession()
    {
        using var fixture = new Fixture();
        await fixture.Gate.WaitAsync(TestContext.Current.CancellationToken);
        var apply = fixture.Engine.ApplyPolicyAsync(fixture.Settings(), fixture.Session);
        Assert.False(apply.IsCompleted);
        using var replacement = new CancellationTokenSource();
        var next = new BusinessPolicySession(replacement) { HasRun = true };
        // The old cancellation source can already have been disposed by Start.
        fixture.Cancellation.Cancel();
        fixture.Cancellation.Dispose();
        Set(fixture.Engine, "_sessionCts", replacement);
        Set(fixture.Engine, "_policySession", next);
        fixture.Gate.Release();
        Assert.Null(await apply.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Same(next, fixture.Engine.CapturePolicySession());
        Assert.Null(await fixture.Engine.ApplyPolicyAsync(fixture.Settings(), fixture.Session));
        Assert.Empty(fixture.Runner.StartCalls);
    }

    [Fact]
    public async Task Apply_DuringHealthRestart_WaitsForSameManagersTransition()
    {
        using var fixture = new Fixture();
        var managerGate = typeof(SingBoxManager).GetField("_lifecycleGate", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(fixture.Manager)!;
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var restart = Task.Run(() =>
        {
            lock (managerGate)
            {
                Set(fixture.Manager, "<State>k__BackingField", SingBoxState.Restarting);
                held.TrySetResult();
                Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
                Set(fixture.Manager, "<State>k__BackingField", SingBoxState.Running);
            }
        });
        await held.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var pending = fixture.Engine.ApplyPolicyAsync(fixture.Settings(), fixture.Session);
        try { Assert.False(pending.IsCompleted); }
        finally { release.Set(); }
        await restart;
        // Empty server data is rejected after the transition. No process launch
        // is necessary to establish that readiness was not lost during recovery.
        Assert.Null(await pending.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Empty(fixture.Runner.StartCalls);
    }

    [Fact]
    public async Task Apply_HealthRestartBeginsAfterReadiness_DoesNotRejectBeforeFinalManagerAdmission()
    {
        using var fixture = new Fixture();
        Assert.True(await fixture.Manager.WaitForPolicyReadyAsync(TestContext.Current.CancellationToken));
        // Reproduce the gap between the readiness wait and ApplyGatedAsync.
        Set(fixture.Manager, "<State>k__BackingField", SingBoxState.Restarting);
        fixture.Handle.SignalExit(1);
        var settings = fixture.Settings();
        settings.App.BypassRussianTraffic = false;
        settings.App.FlushDnsOnStart = false;
        settings.App.RoutingAppsIncludeInitialized = true;
        settings.Vless.Servers = [new VlessServerEntry
        {
            Name = "fixture", Server = "192.0.2.1", Port = 443,
            Uuid = "11111111-2222-3333-4444-555555555555", Security = "none", Flow = ""
        }];
        settings.Vless.ActiveServer = "fixture";
        var apply = (Task<bool>)typeof(VpnEngine).GetMethod("ApplyGatedAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(fixture.Engine, [settings, TestContext.Current.CancellationToken, true])!;
        Assert.False(await apply);
        Assert.Equal(1, fixture.ProcessScanner.Calls);
        Assert.Empty(fixture.Runner.StartCalls);
    }

    [Fact]
    public void Session_AcknowledgedFloorSurvivesCoreLoss_AndRejectsIdentityOrDigestReset()
    {
        using var cancellation = new CancellationTokenSource();
        var session = new BusinessPolicySession(cancellation);
        var current = BusinessPolicyLaunchTests.Policy(8);
        Assert.True(session.TryAcknowledge(current));
        Assert.True(session.CanApply(current));
        Assert.False(session.CanApply(BusinessPolicyLaunchTests.Policy(7)));
        Assert.False(session.CanApply(null));
        Assert.False(session.CanApply(new BusinessPolicySnapshot("other-policy", "fixture-key", 9,
            current.PublishedAt, true, [], current.PayloadDigest)));
        Assert.False(session.TryAcknowledge(new BusinessPolicySnapshot(current.PolicyId, "fixture-key", 8,
            current.PublishedAt, true, [], new string('B', 64))));
        Assert.True(session.CanApply(BusinessPolicyLaunchTests.Policy(9)));
        Assert.True(session.CanApply(current)); // Merely accepted N+1 does not raise this floor.
        var newSession = new BusinessPolicySession(cancellation);
        Assert.True(newSession.CanApply(BusinessPolicyLaunchTests.Policy(7)));
    }

    [Fact]
    public async Task Apply_DisposedEngine_RejectsCapturedSessionWithoutLaunch()
    {
        using var fixture = new Fixture();
        fixture.SetManager(null);
        fixture.Engine.Dispose();
        Assert.Null(await fixture.Engine.ApplyPolicyAsync(fixture.Settings(), fixture.Session));
        Assert.Empty(fixture.Runner.StartCalls);
    }

    [Fact]
    public async Task Receipt_BindsPolicyConfigSessionAndExactHandle_EvenWithPidReuse()
    {
        using var fixture = new Fixture();
        var directory = Path.Combine(Path.GetTempPath(), "policy-receipt-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var launch = new BusinessPolicyLaunch("{\"route\":{\"final\":\"proxy\"}}",
                BusinessPolicyLaunchTests.Policy(), directory);
            launch.Bind(fixture.Handle);
            File.AppendAllText(launch.LogPath, "INFO sing-box started (0.02s)\n");
            await launch.WaitForAcknowledgementAsync(() => true, TestContext.Current.CancellationToken);
            Set(fixture.Manager, "_policyLaunch", launch);
            Set(fixture.Manager, "_businessPolicyLifetime", fixture.Session.Token);
            BusinessPolicyAppliedReceipt? received = null;
            fixture.Engine.BusinessPolicyApplied += receipt => received = receipt;
            typeof(VpnEngine).GetMethod("ObservePolicyAcceptance", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(fixture.Engine, [fixture.Manager, launch, fixture.Cancellation]);
            Assert.NotNull(received);
            Assert.True(fixture.Engine.IsPolicyReceiptCurrent(received));
            Assert.Equal(launch.ConfigurationDigest, received.ConfigurationDigest);
            Assert.Equal(launch.Policy.PayloadDigest, received.PayloadDigest);
            Assert.Equal(launch.Policy.Revision, received.Revision);
            Assert.Equal(fixture.Session.SessionId, received.SessionId);
            Assert.Equal(launch.LaunchId, received.LaunchId);
            var older = new AppSettings { BusinessPolicy = BusinessPolicyLaunchTests.Policy(6) };
            Assert.Null(await fixture.Engine.ApplyPolicyAsync(older, fixture.Session));
            Assert.False(await fixture.Engine.ApplyAsync(older));
            Assert.True(fixture.Engine.IsPolicyReceiptCurrent(received));
            using var reusedPid = new FakeProcessHandle(fixture.Handle.Pid);
            Set(fixture.Manager, "_handle", reusedPid);
            Assert.False(fixture.Engine.IsPolicyReceiptCurrent(received));
            Set(fixture.Manager, "_handle", fixture.Handle);
            Assert.True(fixture.Engine.IsPolicyReceiptCurrent(received));
            fixture.Cancellation.Cancel();
            Assert.False(fixture.Engine.IsPolicyReceiptCurrent(received));
            Set(fixture.Manager, "_policyLaunch", null);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static void Set(object target, string name, object? value) =>
        target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(target, value);

    private static AppSettings GeneratedSettings(string executable, long revision, bool includeOldSite) => new()
    {
        BusinessPolicy = new BusinessPolicySnapshot("fixture-policy", "fixture-key", revision, 1794657600, true,
            includeOldSite ? [new BusinessPolicyRule("old.example.com", "direct", "")] : [],
            new string(includeOldSite ? 'A' : 'B', 64)),
        App = new AppConfig
        {
            ConfigMode = "generated", RoutingMode = "full", BypassRussianTraffic = false,
            FlushDnsOnStart = false, DnsLeakLockdown = false, CustomRulesPriority = "custom_first",
            CustomRules = includeOldSite ? [new CustomRule { Type = "domain_suffix", Value = "old.example.com", Action = "direct", Enabled = true }] : []
        },
        Vless = new VlessConfig
        {
            ActiveServer = "fixture",
            Servers = [new VlessServerEntry
            {
                Name = "fixture", Server = "192.0.2.1", Port = 443,
                Uuid = "11111111-2222-3333-4444-555555555555", Security = "none", Flow = ""
            }]
        },
        SingBox = new SingBoxSettings { ExecutablePath = executable, ClashApi = "127.0.0.1:65535" },
        Monitoring = new MonitoringSettings { HealthCheckInterval = 3600, MaxRestartAttempts = 5, RestartOnFailure = true }
    };

    private sealed class Fixture : IDisposable
    {
        private readonly string _priorDirectory = AppPaths.DataDir;
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "policy-engine-test-" + Guid.NewGuid().ToString("N"));
        private readonly IProcessRunner _priorDiagnostics = TunAdapterDiagnostics.Runner;
        internal CancellationTokenSource Cancellation { get; } = new();
        internal BusinessPolicySession Session { get; }
        internal FakeProcessHandle Handle { get; } = new(100);
        internal FakeProcessRunner Runner { get; } = new();
        internal Scanner ProcessScanner { get; } = new();
        internal SingBoxManager Manager { get; }
        internal string Executable { get; }
        internal VpnEngine Engine { get; }
        internal SemaphoreSlim Gate => (SemaphoreSlim)typeof(VpnEngine)
            .GetField("_lifecycleGate", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Engine)!;

        internal Fixture()
        {
            AppPaths.OverrideDataDir(_directory);
            AppPaths.EnsureDirectories();
            Executable = Path.Combine(_directory, "fixture-sing-box.exe");
            File.WriteAllText(Executable, "synthetic fixture; never executed");
            TunAdapterDiagnostics.Runner = new FakeProcessRunner().OnRun(_ => true,
                new ProcessResult(0, string.Empty, string.Empty, TimeSpan.Zero, false));
#pragma warning disable CS0618
            Engine = new VpnEngine(ProcessScanner, () => throw new InvalidOperationException("Unexpected firewall access."),
                () => throw new InvalidOperationException("Unexpected monitor access."),
                dnsHardening: new NullWindowsDnsHardening());
#pragma warning restore CS0618
            Session = new BusinessPolicySession(Cancellation) { HasRun = true };
            Manager = new SingBoxManager(new SingBoxSettings { ExecutablePath = Executable }, runner: Runner, http: new FakeHttpClient());
            Set(Manager, "_handle", Handle);
            Set(Manager, "<State>k__BackingField", SingBoxState.Running);
            Set(Engine, "_sessionCts", Cancellation);
            Set(Engine, "_policySession", Session);
            SetManager(Manager);
        }

        internal AppSettings Settings() => new() { BusinessPolicy = BusinessPolicyLaunchTests.Policy() };
        internal void SetManager(SingBoxManager? manager) => Set(Engine, "_singBox", manager);

        public void Dispose()
        {
            SetManager(null);
            Set(Manager, "_handle", null);
            Engine.Dispose();
            Manager.Dispose();
            Handle.Dispose();
            Cancellation.Dispose();
            TunAdapterDiagnostics.Runner = _priorDiagnostics;
            AppPaths.OverrideDataDir(_priorDirectory);
            try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
        }
    }

    private sealed class Scanner : IProcessScanner
    {
        internal int Calls { get; private set; }
        internal bool Fail { get; set; } = true;
        public ScanResult ScanForProfile(Profile profile)
        {
            Calls++;
            if (Fail) throw new InvalidOperationException("Synthetic generation stop before process launch.");
            return new ScanResult();
        }
    }

    private sealed class NoopFirewall : IFirewallManager
    {
        public void CreateBlockRules(IEnumerable<string> processNames, bool isFullTunnel = true) { }
        public void EnableBlockRules() { }
        public void DisableBlockRules() { }
        public void DeleteAllRules() { }
        public void Dispose() { }
    }
}
