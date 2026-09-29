#nullable enable

using VPNRouter.Core;
using VPNRouter.Core.Interfaces;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;

namespace VPNRouter.Tests;

[Collection(SafeModeStateCollection.Name)]
public sealed class VpnEngineHotReloadLifecycleTests
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
#pragma warning disable CS0067 // stub implements the interface; the events are never raised
        public event EventHandler<ProcessEventArgs>? ProcessStarted;
        public event EventHandler<ProcessEventArgs>? ProcessStopped;
#pragma warning restore CS0067
        public int StartCount;
        public int StopCount;
        public int DisposeCount;

        public void Start() => StartCount++;
        public void Stop() => StopCount++;
        public void Dispose() => DisposeCount++;
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

    private static AppSettings BuildHappyPathSettings(string singBoxExePath) =>
        new()
        {
            App = new AppConfig
            {
                ConfigMode = "generated",
                RoutingMode = "full",
                FlushDnsOnStart = false,
                BypassRussianTraffic = false,
                Subscriptions = new List<SubscriptionEntry>(),
                DnsLeakLockdown = false,
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
            $"vpnrouter-hot-reload-stub-{Guid.NewGuid():N}.exe");
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

    private static (FakeProcessRunner runner, List<FakeProcessHandle> spawnedHandles)
        BuildFreshHandleSpawnFake(int startPid = 99301)
    {
        var spawnedHandles = new List<FakeProcessHandle>();
        var runner = new FakeProcessRunner();
        var nextPid = startPid;
        runner.OnStart(_ => true, _ =>
        {
            var handle = new FakeProcessHandle(pid: nextPid++);
            spawnedHandles.Add(handle);
            return handle;
        });
        return (runner, spawnedHandles);
    }

    private static async Task<(VpnEngine engine,
                                NullWindowsDnsHardening dnsHardening,
                                List<FakeProcessHandle> spawnedHandles,
                                StubFirewallManager firewall,
                                StubProcessMonitor monitor,
                                AppSettings settings,
                                IDisposable cleanup)>
        StartHappyPathAsync()
    {
        var previousDataDir = AppPaths.DataDir;
        var tempDataDir = Path.Combine(
            Path.GetTempPath(),
            $"vpnrouter-hot-reload-data-{Guid.NewGuid():N}");
        AppPaths.OverrideDataDir(tempDataDir);
        AppPaths.EnsureDirectories();

        var prevSingBoxRunner = SingBoxManager.Runner;
        var prevTunDiagRunner = TunAdapterDiagnostics.Runner;
        TunAdapterDiagnostics.ResetRemoveNetAdapterLatchForTests();
        TunAdapterDiagnostics.SetNetAdapterModuleAvailableForTests(false);

        var (singBoxRunner, spawnedHandles) = BuildFreshHandleSpawnFake();
        SingBoxManager.Runner = singBoxRunner;
        TunAdapterDiagnostics.Runner = BuildTunCleanupFake();

        var stubExe = CreateStubExe();
        var dnsHardening = new NullWindowsDnsHardening();
        var engine = BuildEngine(dnsHardening, out var firewall, out var monitor);

        var settings = BuildHappyPathSettings(stubExe);

        var cleanup = new HotReloadCleanup(
            engine,
            stubExe,
            previousDataDir,
            tempDataDir,
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

        return (engine, dnsHardening, spawnedHandles, firewall, monitor, settings, cleanup);
    }

    private sealed class HotReloadCleanup : IDisposable
    {
        private readonly VpnEngine _engine;
        private readonly string _stubExe;
        private readonly string _previousDataDir;
        private readonly string _tempDataDir;
        private readonly IProcessRunner _prevSingBoxRunner;
        private readonly IProcessRunner _prevTunDiagRunner;
        private bool _disposed;

        public HotReloadCleanup(
            VpnEngine engine,
            string stubExe,
            string previousDataDir,
            string tempDataDir,
            IProcessRunner prevSingBoxRunner,
            IProcessRunner prevTunDiagRunner)
        {
            _engine = engine;
            _stubExe = stubExe;
            _previousDataDir = previousDataDir;
            _tempDataDir = tempDataDir;
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
            AppPaths.OverrideDataDir(_previousDataDir);
            try { Directory.Delete(_tempDataDir, recursive: true); } catch { }
            try { File.Delete(_stubExe); } catch { }
        }
    }

    [Fact]
    public async Task Apply_OnRunningEngine_RunsHotReloadFallbackToRestart()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(),
            "ColdStart prerequisite + SingBoxManager.Restart's Windows-specific TUN cleanup are Windows-only.");

        var (engine, dnsHardening, spawnedHandles, firewall, monitor, settings, cleanup) =
            await StartHappyPathAsync();
        using var _ = cleanup;

        Assert.True(engine.IsRunning);
        Assert.Single(spawnedHandles);
        var initialHandle = spawnedHandles[0];
        Assert.False(initialHandle.HasExited);

        var ok = await engine.ApplyAsync(settings, TestContext.Current.CancellationToken);

        Assert.True(ok);

        Assert.True(initialHandle.HasExited);

        Assert.True(spawnedHandles.Count >= 2,
            $"Expected at least 2 spawned handles (Start + Restart), got {spawnedHandles.Count}");

        var latestHandle = spawnedHandles[^1];
        Assert.False(latestHandle.HasExited);
        Assert.True(engine.IsRunning);
    }

    [Fact]
    public async Task StartAsync_WhenAlreadyRunning_IsNoOp_NotSecondSpawn()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(),
            "ColdStart prerequisite is Windows-only.");

        var (engine, dnsHardening, spawnedHandles, firewall, monitor, settings, cleanup) =
            await StartHappyPathAsync();
        using var _ = cleanup;

        Assert.True(engine.IsRunning);
        Assert.Single(spawnedHandles);
        var initialHandle = spawnedHandles[0];
        Assert.False(initialHandle.HasExited);

        await engine.StartAsync(settings, TestContext.Current.CancellationToken, skipVpnConflictCheck: true);

        Assert.Single(spawnedHandles);
        Assert.False(initialHandle.HasExited);
        Assert.True(engine.IsRunning);
    }

    [Fact]
    public async Task Apply_OnRunningEngine_DoesNotMutateDnsHardening()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(),
            "ColdStart prerequisite is Windows-only.");

        var (engine, dnsHardening, spawnedHandles, firewall, monitor, settings, cleanup) =
            await StartHappyPathAsync();
        using var _ = cleanup;

        Assert.Equal(1, dnsHardening.ApplyCount);
        Assert.Equal(0, dnsHardening.RestoreCount);

        var ok = await engine.ApplyAsync(settings, TestContext.Current.CancellationToken);
        Assert.True(ok);

        Assert.Equal(1, dnsHardening.ApplyCount);
        Assert.Equal(0, dnsHardening.RestoreCount);
    }

    [Fact]
    public async Task Apply_AfterStop_ReturnsFalse_AndDoesNotRespawn()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(),
            "ColdStart prerequisite is Windows-only.");

        var (engine, dnsHardening, spawnedHandles, firewall, monitor, settings, cleanup) =
            await StartHappyPathAsync();
        using var _ = cleanup;

        engine.Stop();
        var handlesAfterStop = spawnedHandles.Count;

        var ok = await engine.ApplyAsync(settings, TestContext.Current.CancellationToken);

        Assert.False(ok);
        Assert.Equal(handlesAfterStop, spawnedHandles.Count);
        Assert.False(engine.IsRunning);
    }

    [Fact]
    public async Task Apply_ConcurrentWithStop_NeverResurrectsTunnel()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(),
            "ColdStart prerequisite is Windows-only.");

        var (engine, dnsHardening, spawnedHandles, firewall, monitor, settings, cleanup) =
            await StartHappyPathAsync();
        using var _ = cleanup;

        var applyTask = Task.Run(() => engine.ApplyAsync(settings, CancellationToken.None));
        var stopTask = Task.Run(() => engine.Stop(), TestContext.Current.CancellationToken);
        await stopTask;
        await applyTask;

        Assert.False(engine.IsRunning);
        Assert.True(spawnedHandles[^1].HasExited,
            "no sing-box handle may remain alive after Stop has completed");
    }

    [Fact]
    public async Task Apply_OnRunningEngine_PreservesFirewallAndMonitorReferences()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(),
            "ColdStart prerequisite is Windows-only.");

        var (engine, dnsHardening, spawnedHandles, firewall, monitor, settings, cleanup) =
            await StartHappyPathAsync();
        using var _ = cleanup;

        Assert.Equal(1, monitor.StartCount);

        Assert.Equal(0, firewall.CreateBlockRulesCount);

        var ok = await engine.ApplyAsync(settings, TestContext.Current.CancellationToken);
        Assert.True(ok);

        Assert.Equal(1, monitor.StartCount);
        Assert.Equal(0, monitor.StopCount);
        Assert.Equal(0, firewall.CreateBlockRulesCount);
        Assert.Equal(0, firewall.DeleteAllRulesCount);
    }
}
