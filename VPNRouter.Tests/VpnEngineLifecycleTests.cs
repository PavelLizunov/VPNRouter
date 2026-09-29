#nullable enable

using System.Reflection;
using VPNRouter.Core;
using VPNRouter.Core.Interfaces;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;

namespace VPNRouter.Tests;

public sealed class VpnEngineLifecycleTests
{
    private sealed class StubProcessScanner : IProcessScanner
    {
        public ScanResult ScanForProfile(Profile profile) =>
            new() { ProcessNames = new List<string>(), ScannedAt = DateTime.Now };
    }

    private sealed class StubFirewallManager : IFirewallManager
    {
        public int CreateBlockRulesCount;
        public int EnableBlockRulesCount;
        public int DisableBlockRulesCount;
        public int DeleteAllRulesCount;
        public int DisposeCount;

        public void CreateBlockRules(IEnumerable<string> processNames, bool isFullTunnel = true) => CreateBlockRulesCount++;
        public void EnableBlockRules() => EnableBlockRulesCount++;
        public void DisableBlockRules() => DisableBlockRulesCount++;
        public void DeleteAllRules() => DeleteAllRulesCount++;
        public void Dispose() => DisposeCount++;
    }

    private sealed class StubProcessMonitor : IProcessMonitor
    {
        public event EventHandler<ProcessEventArgs>? ProcessStarted;
        public event EventHandler<ProcessEventArgs>? ProcessStopped;
        public int StartCount;
        public int StopCount;
        public int DisposeCount;

        public void Start() => StartCount++;
        public void Stop() => StopCount++;
        public void Dispose() => DisposeCount++;
        public void RaiseDummy()
        {
            ProcessStarted?.Invoke(this, new());
            ProcessStopped?.Invoke(this, new());
        }
    }

#pragma warning disable CS0618
    private static VpnEngine BuildEngine(
        IWindowsDnsHardening dnsHardening,
        out StubFirewallManager firewall,
        out StubProcessMonitor monitor)
    {
        var fw = new StubFirewallManager();
        var mon = new StubProcessMonitor();
        firewall = fw;
        monitor = mon;
        return new VpnEngine(
            scanner: new StubProcessScanner(),
            firewallFactory: () => fw,
            monitorFactory: () => mon,
            logger: null,
            dnsHardening: dnsHardening);
    }
#pragma warning restore CS0618

    private static AppSettings BuildHappyPathSettings(
        string singBoxExePath, bool dnsLeakLockdown = false) =>
        new()
        {
            App = new AppConfig
            {
                ConfigMode = "generated",
                RoutingMode = "full",
                FlushDnsOnStart = false,
                BypassRussianTraffic = false,
                Subscriptions = new List<SubscriptionEntry>(),
                DnsLeakLockdown = dnsLeakLockdown,
            },
            Vless = new VlessConfig
            {
                ActiveServer = "main",
                Servers = new List<VlessServerEntry>
                {
                    new()
                    {
                        Name = "main",
                        Server = "10.0.0.1",
                        Port = 443,
                        Uuid = "11111111-2222-3333-4444-555555555555",
                        Flow = "xtls-rprx-vision",
                        Security = "reality",
                        Reality = new VlessRealityConfig
                        {
                            Enabled = true,
                            ServerName = "www.cloudflare.com",
                            Fingerprint = "chrome",
                            PublicKey = "gDawCMB0X6iGXZkG8nZIFW5TaaW29x0DMzWijN-gc2A",
                            ShortId = "d86e92a0c6dd2271",
                        },
                    },
                },
            },
            Tun = new TunSettings(),
            Dns = new DnsSettings { VpnDns = "https://1.1.1.1/dns-query" },
            SingBox = new SingBoxSettings
            {
                ExecutablePath = singBoxExePath,
                ClashApi = "127.0.0.1:65535",
            },
            Monitoring = new MonitoringSettings
            {
                HealthCheckInterval = 3600,
                MaxRestartAttempts = 5,
                RestartOnFailure = true,
            },
            ActiveProfile = "",
        };

    private static string CreateStubExe()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"vpnrouter-lifecycle-stub-{Guid.NewGuid():N}.exe");
        File.WriteAllText(path, "stub");
        return path;
    }

    private static FakeProcessRunner BuildTunCleanupFake() =>
        new FakeProcessRunner()
            .OnRun(
                r => r.ExecutablePath == "netsh"
                  && r.Arguments.Count >= 3
                  && r.Arguments[0] == "interface"
                  && r.Arguments[1] == "show",
                new ProcessResult(
                    ExitCode: 0,
                    Stdout: "Admin State    State          Type             Interface Name\r\n"
                          + "----------------------------------------------------------\r\n"
                          + "Enabled        Connected      Dedicated        Ethernet\r\n",
                    Stderr: "",
                    Duration: TimeSpan.FromMilliseconds(1),
                    TimedOut: false))
            .OnRun(
                r => r.ExecutablePath == "netsh"
                  && r.Arguments.Contains("admin=disabled"),
                new ProcessResult(
                    ExitCode: 5,
                    Stdout: "",
                    Stderr: "Adapter not found (synthetic)",
                    Duration: TimeSpan.FromMilliseconds(1),
                    TimedOut: false));

    private static (FakeProcessRunner runner, FakeProcessHandle handle)
        BuildSingBoxSpawnFake(int pid = 99001)
    {
        var handle = new FakeProcessHandle(pid);
        var runner = new FakeProcessRunner();
        runner.OnStart(_ => true, _ => handle);
        return (runner, handle);
    }

    private static (FakeProcessRunner runner, List<FakeProcessHandle> handles)
        BuildFreshSingBoxSpawnFake(int startPid = 99001)
    {
        var handles = new List<FakeProcessHandle>();
        var runner = new FakeProcessRunner();
        var nextPid = startPid;
        runner.OnStart(_ => true, _ =>
        {
            var handle = new FakeProcessHandle(nextPid++);
            handles.Add(handle);
            return handle;
        });
        return (runner, handles);
    }

    private static async Task<(VpnEngine engine,
                                NullWindowsDnsHardening dnsHardening,
                                FakeProcessHandle handle,
                                StubFirewallManager firewall,
                                StubProcessMonitor monitor,
                                IDisposable cleanup)>
        StartHappyPathAsync(bool dnsLeakLockdown = false)
    {
        var prevSingBoxRunner = SingBoxManager.Runner;
        var prevTunDiagRunner = TunAdapterDiagnostics.Runner;
        TunAdapterDiagnostics.ResetRemoveNetAdapterLatchForTests();
        TunAdapterDiagnostics.SetNetAdapterModuleAvailableForTests(false);

        var (singBoxRunner, handle) = BuildSingBoxSpawnFake();
        SingBoxManager.Runner = singBoxRunner;
        TunAdapterDiagnostics.Runner = BuildTunCleanupFake();

        var stubExe = CreateStubExe();
        var dnsHardening = new NullWindowsDnsHardening();
        var engine = BuildEngine(dnsHardening, out var firewall, out var monitor);

        var settings = BuildHappyPathSettings(stubExe, dnsLeakLockdown);

        var cleanup = new LifecycleCleanup(
            engine,
            stubExe,
            prevSingBoxRunner,
            prevTunDiagRunner);

        try
        {
            await engine.StartAsync(settings, default, skipVpnConflictCheck: true);
        }
        catch
        {
            cleanup.Dispose();
            throw;
        }

        return (engine, dnsHardening, handle, firewall, monitor, cleanup);
    }

    private sealed class LifecycleCleanup : IDisposable
    {
        private readonly VpnEngine _engine;
        private readonly string _stubExe;
        private readonly IProcessRunner _prevSingBoxRunner;
        private readonly IProcessRunner _prevTunDiagRunner;
        private bool _disposed;

        public LifecycleCleanup(
            VpnEngine engine,
            string stubExe,
            IProcessRunner prevSingBoxRunner,
            IProcessRunner prevTunDiagRunner)
        {
            _engine = engine;
            _stubExe = stubExe;
            _prevSingBoxRunner = prevSingBoxRunner;
            _prevTunDiagRunner = prevTunDiagRunner;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _engine.Stop(); } catch { }
            try { _engine.Dispose(); } catch { }
            SingBoxManager.Runner = _prevSingBoxRunner;
            TunAdapterDiagnostics.Runner = _prevTunDiagRunner;
            TunAdapterDiagnostics.ResetRemoveNetAdapterLatchForTests();
            try { File.Delete(_stubExe); } catch { }
        }
    }

    private static async Task<(VpnEngine engine, AppSettings settings, IDisposable cleanup)>
        StartHappyPathWithSettingsAsync()
    {
        var prevSingBoxRunner = SingBoxManager.Runner;
        var prevTunDiagRunner = TunAdapterDiagnostics.Runner;
        TunAdapterDiagnostics.ResetRemoveNetAdapterLatchForTests();
        TunAdapterDiagnostics.SetNetAdapterModuleAvailableForTests(false);

        var (singBoxRunner, _) = BuildFreshSingBoxSpawnFake();
        SingBoxManager.Runner = singBoxRunner;
        TunAdapterDiagnostics.Runner = BuildTunCleanupFake();

        var stubExe = CreateStubExe();
        var dns = new NullWindowsDnsHardening();
        var engine = BuildEngine(dns, out _, out _);
        var settings = BuildHappyPathSettings(stubExe);
        var cleanup = new LifecycleCleanup(engine, stubExe, prevSingBoxRunner, prevTunDiagRunner);

        try { await engine.StartAsync(settings, default, skipVpnConflictCheck: true); }
        catch { cleanup.Dispose(); throw; }

        return (engine, settings, cleanup);
    }

    [Fact]
    public async Task ProbeFailoverRestart_UnderCancelledProbeToken_BringsReplacementUp()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(),
            "Drives SingBoxManager's Windows spawn path (see lifecycle harness).");

        var (engine, settings, cleanup) = await StartHappyPathWithSettingsAsync();
        using var lifecycleDispose = cleanup;
        Assert.True(engine.IsRunning);

        using var probeCts = new CancellationTokenSource();
        probeCts.Cancel();

        var ok = await engine.ExecuteProbeFailoverRestartAsync(settings, probeCts.Token);

        Assert.True(ok, "failover restart self-cancelled — no replacement came up");
        Assert.True(engine.IsRunning, "replacement is not running after the failover restart");
    }

    [Fact]
    public async Task ProbeFailoverRestart_AfterUserStop_DoesNotResurrect()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(),
            "Drives SingBoxManager's Windows spawn path (see lifecycle harness).");

        var (engine, settings, cleanup) = await StartHappyPathWithSettingsAsync();
        using var lifecycleDispose = cleanup;
        Assert.True(engine.IsRunning);

        engine.Stop();
        Assert.False(engine.IsRunning);

        var ok = await engine.ExecuteProbeFailoverRestartAsync(settings, CancellationToken.None);

        Assert.False(ok, "failover restart resurrected the tunnel after Disconnect");
        Assert.False(engine.IsRunning, "tunnel resurrected after Disconnect");
    }

    [Fact]
    public async Task Start_ColdStart_FiresLifecycleEvents_InOrder()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(),
            "ColdStart drives SingBoxManager's Windows spawn path; Linux uses pkexec + getcap shell-outs not behind IProcessRunner.");

        var statusLog = new List<string>();
        var pidNotifications = new List<int>();

        var (engine, dnsHardening, handle, firewall, monitor, cleanup) =
            await StartHappyPathAsync();
        using var _ = cleanup;

        engine.StatusChanged += msg => statusLog.Add(msg);
        engine.SingBoxStarted += pid => pidNotifications.Add(pid);

        Assert.Equal(1, dnsHardening.ApplyCount);
        Assert.Equal(0, dnsHardening.RestoreCount);
        Assert.Equal("Apply", dnsHardening.Calls[0].Op);
        Assert.NotNull(dnsHardening.Calls[0].Settings);

        Assert.True(engine.IsRunning);
        Assert.Equal("10.0.0.1", engine.ActiveServerAddress);

        Assert.Equal(0, firewall.CreateBlockRulesCount);

        Assert.Equal(1, monitor.StartCount);

        Assert.False(handle.HasExited);
    }

    [Fact]
    public async Task Stop_AfterStart_FiresRestoreThroughDnsHardening()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(),
            "ColdStart prerequisite is Windows-only (see Start_ColdStart_FiresLifecycleEvents_InOrder).");

        var (engine, dnsHardening, handle, firewall, monitor, cleanup) =
            await StartHappyPathAsync();
        try
        {
            Assert.Equal(1, dnsHardening.ApplyCount);
            Assert.Equal(0, dnsHardening.RestoreCount);
            Assert.True(engine.IsRunning);

            engine.Stop();

            Assert.Equal(1, dnsHardening.RestoreCount);
            Assert.Equal("Restore", dnsHardening.Calls.Last().Op);
            Assert.Null(dnsHardening.Calls.Last().Settings);

            Assert.False(engine.IsRunning);
            Assert.True(handle.HasExited);

            Assert.Equal(1, monitor.DisposeCount);
        }
        finally
        {
            cleanup.Dispose();
        }
    }

    [Fact]
    public async Task Start_Stop_Start_CleanLifecycleIsIdempotent()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(),
            "ColdStart prerequisite is Windows-only.");

        var prevSingBoxRunner = SingBoxManager.Runner;
        var prevTunDiagRunner = TunAdapterDiagnostics.Runner;
        TunAdapterDiagnostics.ResetRemoveNetAdapterLatchForTests();
        TunAdapterDiagnostics.SetNetAdapterModuleAvailableForTests(false);

        var (firstRunner, firstHandle) = BuildSingBoxSpawnFake(pid: 99101);
        SingBoxManager.Runner = firstRunner;
        TunAdapterDiagnostics.Runner = BuildTunCleanupFake();

        var stubExe = CreateStubExe();
        var dnsHardening = new NullWindowsDnsHardening();
        var engine = BuildEngine(dnsHardening, out var firewall, out var monitor);

        try
        {
            var settings = BuildHappyPathSettings(stubExe);

            var testCt = TestContext.Current.CancellationToken;
            await engine.StartAsync(settings, testCt, skipVpnConflictCheck: true);
            Assert.Equal(1, dnsHardening.ApplyCount);
            Assert.Equal(0, dnsHardening.RestoreCount);

            engine.Stop();
            Assert.Equal(1, dnsHardening.RestoreCount);
            Assert.True(firstHandle.HasExited);

            var (secondRunner, secondHandle) = BuildSingBoxSpawnFake(pid: 99102);
            SingBoxManager.Runner = secondRunner;

            await engine.StartAsync(settings, testCt, skipVpnConflictCheck: true);

            Assert.Equal(2, dnsHardening.ApplyCount);
            Assert.Equal(1, dnsHardening.RestoreCount);
            Assert.True(engine.IsRunning);
            Assert.False(secondHandle.HasExited);
        }
        finally
        {
            try { engine.Stop(); } catch { }
            try { engine.Dispose(); } catch { }
            SingBoxManager.Runner = prevSingBoxRunner;
            TunAdapterDiagnostics.Runner = prevTunDiagRunner;
            TunAdapterDiagnostics.ResetRemoveNetAdapterLatchForTests();
            try { File.Delete(stubExe); } catch { }
        }
    }

    private static void SetHealthMonitorField(HealthMonitor hm, string name, object? value)
    {
        var f = typeof(HealthMonitor).GetField(name,
            BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException(
                $"HealthMonitor has no field '{name}'");
        f.SetValue(hm, value);
    }

    private static T GetHealthMonitorField<T>(HealthMonitor hm, string name)
    {
        var f = typeof(HealthMonitor).GetField(name,
            BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException(
                $"HealthMonitor has no field '{name}'");
        return (T)f.GetValue(hm)!;
    }

    [Fact]
    public void Crash_TriggersHealthMonitorRestart_WithAttemptCounterIncrement()
    {
        var sbSettings = new SingBoxSettings { ClashApi = "127.0.0.1:65535" };
        using var singBox = new SingBoxManager(sbSettings, http: new FakeHttpClient());
        var scanner = new StubProcessScanner();
        var firewall = new StubFirewallManager();
        var monitoring = new MonitoringSettings
        {
            HealthCheckInterval = 3600,
            MaxRestartAttempts = 5,
            RestartOnFailure = true,
        };
        using var hm = new HealthMonitor(singBox, scanner, firewall, monitoring);

        var attempts = new List<int>();
        hm.RestartAttempted += (_, n) => attempts.Add(n);

        try
        {
            hm.Start(new Profile { Name = "test" }, new AppSettings());
            var onCrash = typeof(HealthMonitor).GetMethod("OnSingBoxCrashed",
                BindingFlags.NonPublic | BindingFlags.Instance)!;
            onCrash.Invoke(hm, new object?[] { null, EventArgs.Empty });
            onCrash.Invoke(hm, new object?[] { null, EventArgs.Empty });

            Assert.Equal(2, attempts.Count);
            Assert.Equal(1, attempts[0]);
            Assert.Equal(2, attempts[1]);
        }
        finally
        {
            hm.Stop();
        }
    }

    [Fact]
    public void LinuxTunPermissionCrash_DisarmsAutomaticRestart()
    {
        var sbSettings = new SingBoxSettings { ClashApi = "127.0.0.1:65535" };
        using var singBox = new SingBoxManager(sbSettings, http: new FakeHttpClient());
        using var hm = new HealthMonitor(
            singBox,
            new StubProcessScanner(),
            new StubFirewallManager(),
            new MonitoringSettings
            {
                HealthCheckInterval = 3600,
                MaxRestartAttempts = 3,
                RestartOnFailure = true,
            });

        var attempts = new List<int>();
        hm.RestartAttempted += (_, n) => attempts.Add(n);

        try
        {
            hm.Start(new Profile { Name = "test" }, new AppSettings());
            typeof(SingBoxManager)
                .GetProperty(
                    nameof(SingBoxManager.LastCrashWasLinuxTunPermissionFailure),
                    BindingFlags.NonPublic | BindingFlags.Instance)!
                .SetValue(singBox, true);

            typeof(HealthMonitor)
                .GetMethod("OnSingBoxCrashed", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(hm, new object?[] { null, EventArgs.Empty });

            Assert.Empty(attempts);
            Assert.False(GetHealthMonitorField<bool>(hm, "_shouldBeRunning"));
        }
        finally
        {
            hm.Stop();
        }
    }

    [Fact]
    public void Crash_ExceedsMaxRetries_StopsFiringRestartAttempts()
    {
        var sbSettings = new SingBoxSettings { ClashApi = "127.0.0.1:65535" };
        using var singBox = new SingBoxManager(sbSettings, http: new FakeHttpClient());
        var scanner = new StubProcessScanner();
        var firewall = new StubFirewallManager();
        var monitoring = new MonitoringSettings
        {
            HealthCheckInterval = 3600,
            MaxRestartAttempts = 3,
            RestartOnFailure = true,
        };
        using var hm = new HealthMonitor(singBox, scanner, firewall, monitoring);

        var attempts = new List<int>();
        hm.RestartAttempted += (_, n) => attempts.Add(n);

        try
        {
            hm.Start(new Profile { Name = "test" }, new AppSettings());
            var onCrash = typeof(HealthMonitor).GetMethod("OnSingBoxCrashed",
                BindingFlags.NonPublic | BindingFlags.Instance)!;

            for (int i = 0; i < 5; i++)
                onCrash.Invoke(hm, new object?[] { null, EventArgs.Empty });

            Assert.Equal(3, attempts.Count);
            Assert.Equal(new[] { 1, 2, 3 }, attempts);
        }
        finally
        {
            hm.Stop();
        }
    }

    [Fact]
    public async Task Apply_OnIdleEngine_ReturnsFalseWithoutInvokingHardening()
    {
        var fake = new NullWindowsDnsHardening();
        var engine = BuildEngine(fake, out _, out _);
        try
        {
            var settings = BuildHappyPathSettings(singBoxExePath: "irrelevant");

            var ok = await engine.ApplyAsync(settings, TestContext.Current.CancellationToken);

            Assert.False(ok);
            Assert.Empty(fake.Calls);
            Assert.False(engine.IsRunning);
        }
        finally
        {
            engine.Dispose();
        }
    }

    [Fact]
    public async Task Apply_HotReloadPipeline_DoesNotTouchDnsHardening_SourcePin()
    {
        var fake = new NullWindowsDnsHardening();
        var host = new HotReloadTestHost();
        var pipeline = new StartupPipeline(host, dnsHardening: fake);
        var settings = BuildHappyPathSettings(singBoxExePath: "irrelevant");

        var result = await pipeline.ExecuteAsync(
            new StartupContext(settings, StartupMode.HotReload),
            TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.NotNull(result.ConfigJson);
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public void Stop_DuringPendingRestart_CancelsAttempt_AndDisarmsShouldBeRunning()
    {
        var sbSettings = new SingBoxSettings { ClashApi = "127.0.0.1:65535" };
        using var singBox = new SingBoxManager(sbSettings, http: new FakeHttpClient());
        var scanner = new StubProcessScanner();
        var firewall = new StubFirewallManager();
        var monitoring = new MonitoringSettings
        {
            HealthCheckInterval = 3600,
            MaxRestartAttempts = 5,
            RestartOnFailure = true,
        };
        var hm = new HealthMonitor(singBox, scanner, firewall, monitoring);

        var attempts = new List<int>();
        hm.RestartAttempted += (_, n) => attempts.Add(n);

        hm.Start(new Profile { Name = "test" }, new AppSettings());

        var onCrash = typeof(HealthMonitor).GetMethod("OnSingBoxCrashed",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        onCrash.Invoke(hm, new object?[] { null, EventArgs.Empty });

        Assert.Single(attempts);
        Assert.NotNull(GetHealthMonitorField<CancellationTokenSource?>(hm, "_restartCts"));
        Assert.True(GetHealthMonitorField<bool>(hm, "_shouldBeRunning"));

        hm.Stop();

        Assert.False(GetHealthMonitorField<bool>(hm, "_shouldBeRunning"));
        Assert.Null(GetHealthMonitorField<CancellationTokenSource?>(hm, "_restartCts"));

        hm.ProbeNow();
        Assert.Single(attempts);

        hm.Dispose();
    }

    [Fact]
    public async Task Start_DnsLeakLockdownOff_DoesNotInvokeEnableLockdown()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(),
            "ColdStart prerequisite is Windows-only.");

        var (engine, dnsHardening, handle, firewall, monitor, cleanup) =
            await StartHappyPathAsync(dnsLeakLockdown: false);
        try
        {
            Assert.Equal(1, dnsHardening.ApplyCount);

            var applyCall = dnsHardening.Calls.First(c => c.Op == "Apply");
            Assert.NotNull(applyCall.Settings);
            Assert.False(applyCall.Settings!.App.DnsLeakLockdown);

            engine.Stop();

            Assert.Equal(0, dnsHardening.EnableLockdownCount);
            Assert.Equal(1, dnsHardening.RestoreCount);
        }
        finally
        {
            cleanup.Dispose();
        }
    }

    private sealed class HotReloadTestHost : StartupHostInternal
    {
        public Serilog.ILogger? Logger => null;
        public IProcessScanner Scanner { get; } = new StubProcessScanner();
        public Func<IFirewallManager> FirewallFactory { get; } =
            () => throw new InvalidOperationException("HotReload should not reach phase 6");
        public Func<IProcessMonitor> MonitorFactory { get; } =
            () => throw new InvalidOperationException("HotReload should not reach phase 8");
        public SingBoxManager? SingBox => null;
        public IFirewallManager? Firewall => null;

        public void OnStatus(string message) { }
        public void OnWarning(string message) { }
        public void OnSingBoxStarted(int pid) { }
        public void OnConnected(int pid) { }
        public bool IsCurrentStart(int pid) => true;
        public void OnRestartAttempted(int attempt, int max) { }
        public void OnFailoverRequested(string reason) { }
        public void OnAutoFailoverTriggered(string message) { }
        public void OnProcessDetected(string name, int pid) { }
        public void SetActiveServerAddress(string address) { }
        public void SetActiveModes(string configMode, string routingMode, string tunFingerprint) { }
        public void SetActiveProfile(Profile profile) { }
        public void SetScanResult(ScanResult result) { }
        public void SetSingBoxManager(SingBoxManager manager) { }
        public void StartDnsTunnelTransport(VlessServerEntry activeServer, AppSettings settings) { }
        public void SetFirewallManager(IFirewallManager firewall) { }
        public void SetProcessMonitor(IProcessMonitor etw) { }
        public void SetHealthMonitor(HealthMonitor monitor) { }
        public void EnsureSanityCheckScaffolding(AppSettings settings, out ConfigSanityCheck sanityCheck) =>
            sanityCheck = new ConfigSanityCheck();
        public AutoFailoverEngine WireFailover(ConfigSanityCheck sanityCheck) =>
            new(new AppSettings(), sanityCheck);
        public AutoFailoverEngine WireFailoverWithStop(ConfigSanityCheck sanityCheck) =>
            new(new AppSettings(), sanityCheck);
        public void SchedulePostStartProbe(AppSettings settings, ConfigSanityCheck sanityCheck, CancellationToken ct) { }
    }
}
