// Task #49 (2026-05-21) — Hot-reload Apply lifecycle characterization tests
// on a RUNNING engine, completing Agent C's Task #36-C Group 3 deferred
// coverage.
//
// Background: Task #36-C (commit 681b61c, Group 3) shipped 2 hot-reload
// tests but punted on the "Apply on running engine drives hot-reload"
// case. Agent C's brief documented the gap:
//
//   > Group 3 partially compromised. The brief asked for "Apply on a
//   > running engine regenerates config — engine doesn't kill sing-box
//   > (hot-reload preferred), Apply returns true." Driving Apply through
//   > a running engine to a successful Clash API hot-reload requires
//   > either (a) faking out the Clash API's PUT /configs?force=true 200
//   > response via FakeSingBoxApi, OR (b) using reflection to lie about
//   > IsRunning() and bypass the idle-engine guard. Both are uglier than
//   > the existing test surface deserves.
//
// What Agent C missed: we DON'T need a successful hot-reload to test the
// running-engine Apply path. We can:
//   1. Start the engine via StartHappyPathAsync (Group 1's existing fake-
//      process pipeline) — sing-box is "alive" via FakeProcessHandle.
//   2. Call engine.ApplyAsync(settings) — guard `_singBox.IsRunning()`
//      returns true (FakeProcessHandle.HasExited=false) so the pipeline
//      proceeds.
//   3. The pipeline runs HotReload mode → regenerates config JSON →
//      VpnEngine.ApplyAsync calls `_singBox.TryReloadConfigJson(json)`.
//   4. TryReloadConfigJson → TryHotReload → HTTP PUT to 127.0.0.1:65535
//      (unused port) → connection refused fast → returns false.
//   5. Falls back to `_singBox.ReloadConfigJson(json, forceRestart=false)`.
//   6. ReloadConfigJson tries TryHotReload AGAIN (same failure) → Restart().
//   7. Restart kills the old handle, spawns a new one via FakeProcessRunner
//      → new "alive" handle.
//   8. ApplyAsync returns true.
//
// This is the FULL running-engine Apply pipeline exercised deterministically.
// No FakeSingBoxApi needed (the HTTP refused-fast IS deterministic — port
// 65535 has no listener); no IsRunning reflection lying needed (the fake
// handle reports alive truthfully).
//
// ── Scope realised ───────────────────────────────────────────────────────
//
// 3 tests, Windows-only (same SkipUnless gating as Task #36-C Group 1):
//
//   1. Apply_OnRunningEngine_RunsHotReloadFallbackToRestart — full path:
//      running engine → Apply → hot-reload HTTP fails → restart fallback.
//      Pins ApplyAsync returns true, engine still running after the dust
//      settles, second sing-box handle spawned.
//
//   2. Apply_OnRunningEngine_DoesNotMutateDnsHardening — defence pin:
//      Apply on running engine should NOT touch DNS hardening (phase 7+8
//      skipped in HotReload mode). Mirrors Group 3's HotReload-pipeline
//      direct-drive test from the engine-lifecycle angle.
//
//   3. Apply_OnRunningEngine_PreservesFirewallAndMonitorReferences — Apply
//      does NOT replace _firewall or _etw on the engine (those stayed
//      from initial Start). Confirms the lifecycle distinction:
//      ReloadConfig restarts sing-box but does NOT re-do phases 6/8.
//
// ── Refuse-to-proceed analysis ───────────────────────────────────────────
//
// Per Task #49 brief: "If existing seam is sufficient → write 2-3 tests.
// If new seam needed → STOP and report." Result: existing seams ARE
// sufficient (SingBoxManager.Runner static seam + FakeProcessRunner +
// the HTTP-refused-fast determinism of an unused port). No production
// code change needed. Tests use the same scaffolding as Task #36-C
// Group 1 + an extra OnStart factory that produces a fresh handle per
// spawn (so Restart's second Start gets a fresh "alive" handle, not the
// killed one from the prior cycle).
//
// Brief: plans/phase4-lifecycle-test-gaps-task49-2026-05-21.md.

#nullable enable

using VPNRouter.Core;
using VPNRouter.Core.Interfaces;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;

namespace VPNRouter.Tests;

/// <summary>
/// Characterization tests for <see cref="VpnEngine.ApplyAsync"/> on a
/// RUNNING engine. Companion to <see cref="VpnEngineLifecycleTests"/>'s
/// Group 3 (idle-engine guard + HotReload pipeline source pin).
///
/// <para>Cross-references:
/// <see cref="VpnEngineLifecycleTests"/> (Group 3 idle-engine guard tests),
/// <see cref="VpnEngineSplitTunnelLifecycleTests"/> (Task #49 split-tunnel tests),
/// <see cref="WindowsDnsHardeningInjectionTests"/> (DNS-hardening seam wiring).</para>
/// </summary>
public sealed class VpnEngineHotReloadLifecycleTests
{
    // ─── Inline stubs (mirrors VpnEngineLifecycleTests pattern) ──────────

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

        public void CreateBlockRules(IEnumerable<string> processNames) => CreateBlockRulesCount++;
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
    }

    // ─── Engine + Settings factories ─────────────────────────────────────

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

    /// <summary>
    /// Same shape as <see cref="VpnEngineLifecycleTests"/>'s helper but
    /// using <c>RoutingMode="full"</c> to bypass the bundled-profile
    /// catalogue lookup (this test class focuses on hot-reload Apply
    /// behaviour; profile-resolution is covered by the split-tunnel sibling).
    /// </summary>
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
                ClashApi = "127.0.0.1:65535",   // unused port → HTTP refused fast
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

    /// <summary>
    /// FakeProcessRunner that produces a FRESH FakeProcessHandle per
    /// spawn. Critical for hot-reload tests: <see cref="SingBoxManager.Restart"/>
    /// calls StopInternal (kills the current handle) then LaunchProcess
    /// (spawns a fresh one via the Runner.Start matcher). If the matcher
    /// returned the SAME handle every time, the killed-from-Stop handle
    /// would resurface and engine.IsRunning() would lie ("HasExited=true"
    /// → "not running" → test asserts fail).
    ///
    /// <para>Tracks all spawned handles in <c>spawnedHandles</c> so the
    /// test can assert on N spawns + verify the latest handle is alive.</para>
    /// </summary>
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
            try { _engine.Stop(); } catch { /* best-effort */ }
            try { _engine.Dispose(); } catch { /* best-effort */ }
            SingBoxManager.Runner = _prevSingBoxRunner;
            TunAdapterDiagnostics.Runner = _prevTunDiagRunner;
            TunAdapterDiagnostics.ResetRemoveNetAdapterLatchForTests();
            AppPaths.OverrideDataDir(_previousDataDir);
            try { Directory.Delete(_tempDataDir, recursive: true); } catch { /* best-effort */ }
            try { File.Delete(_stubExe); } catch { /* best-effort */ }
        }
    }

    // ─── Tests ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Apply_OnRunningEngine_RunsHotReloadFallbackToRestart()
    {
        // Drive Apply against the running engine. Hot-reload (HTTP PUT
        // to 127.0.0.1:65535) will fail-fast (port unused → connection
        // refused), causing ReloadConfigJson to fall back to Restart().
        // The fresh-handle Runner factory provides a new alive handle
        // for the restart's Start() call, so the engine stays "running"
        // after the dust settles.
        //
        // Pins:
        //  • ApplyAsync returns true (the restart-fallback path succeeded).
        //  • At least 2 handles were spawned (initial Start + Restart).
        //  • The latest spawned handle is alive (engine IsRunning).
        //  • The initial handle was killed (Restart's StopInternal fired).
        Assert.SkipUnless(OperatingSystem.IsWindows(),
            "ColdStart prerequisite + SingBoxManager.Restart's Windows-specific TUN cleanup are Windows-only.");

        var (engine, dnsHardening, spawnedHandles, firewall, monitor, settings, cleanup) =
            await StartHappyPathAsync();
        using var _ = cleanup;

        // Pre-Apply baseline.
        Assert.True(engine.IsRunning);
        Assert.Single(spawnedHandles);
        var initialHandle = spawnedHandles[0];
        Assert.False(initialHandle.HasExited);

        // Drive Apply.
        var ok = await engine.ApplyAsync(settings, TestContext.Current.CancellationToken);

        Assert.True(ok);

        // The initial handle was killed by Restart's StopInternal.
        Assert.True(initialHandle.HasExited);

        // At least one additional handle was spawned by Restart.
        // (May be more than 2 if any other path in the pipeline races —
        // but in practice we expect exactly 2: initial Start + Restart.)
        Assert.True(spawnedHandles.Count >= 2,
            $"Expected at least 2 spawned handles (Start + Restart), got {spawnedHandles.Count}");

        // The most recent handle is alive — engine is running again.
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
        // Defence pin: ApplyAsync on a running engine must NOT call
        // _dnsHardening.Apply or .Restore. The HotReload pipeline mode
        // skips phases 5-8 (where Apply lives — phase 8). Restore is
        // gated to engine.Stop(), not Apply.
        //
        // Mirrors VpnEngineLifecycleTests's
        // Apply_HotReloadPipeline_DoesNotTouchDnsHardening_SourcePin
        // but from a real running-engine lifecycle angle (not direct
        // StartupPipeline drive). Catches any future refactor that
        // accidentally wires phase 7/8 into HotReload mode.
        Assert.SkipUnless(OperatingSystem.IsWindows(),
            "ColdStart prerequisite is Windows-only.");

        var (engine, dnsHardening, spawnedHandles, firewall, monitor, settings, cleanup) =
            await StartHappyPathAsync();
        using var _ = cleanup;

        // Phase 8 from initial Start fired Apply once. Baseline.
        Assert.Equal(1, dnsHardening.ApplyCount);
        Assert.Equal(0, dnsHardening.RestoreCount);

        var ok = await engine.ApplyAsync(settings, TestContext.Current.CancellationToken);
        Assert.True(ok);

        // Apply should NOT have touched the DNS hardening Apply/Restore
        // seams. ApplyCount must still be 1 (no second Phase 8); RestoreCount
        // must still be 0 (no Stop yet).
        //
        // NB: dnsHardening.EnableLockdownCount is intentionally NOT pinned
        // here. The warmup probe (ScheduleWarmupProbe inside Phase 7) is
        // fire-and-forget; if the dev/CI machine has working internet it
        // may complete + fire EnableLockdownIfConfigured at any time
        // (before, during, or after ApplyAsync). The lockdown branch is
        // unrelated to ApplyAsync's contract — pinning it would make this
        // test racy on machines with working network.
        Assert.Equal(1, dnsHardening.ApplyCount);
        Assert.Equal(0, dnsHardening.RestoreCount);
    }

    [Fact]
    public async Task Apply_OnRunningEngine_PreservesFirewallAndMonitorReferences()
    {
        // Lifecycle invariant: ApplyAsync does NOT re-create firewall
        // or ETW monitor. Phase 6 (firewall setup) + Phase 8 (ETW + DNS)
        // are skipped on HotReload, so the same _firewall and _etw
        // references from the initial ColdStart must still be active.
        //
        // We assert by checking that the firewall + monitor capture
        // counters do NOT receive additional Start invocations during
        // Apply — same instances stayed wired.
        Assert.SkipUnless(OperatingSystem.IsWindows(),
            "ColdStart prerequisite is Windows-only.");

        var (engine, dnsHardening, spawnedHandles, firewall, monitor, settings, cleanup) =
            await StartHappyPathAsync();
        using var _ = cleanup;

        // Initial Phase 8 fired etw.Start() exactly once.
        Assert.Equal(1, monitor.StartCount);

        // FullTunnel synthetic profile has BlockOnVpnFail=false → Phase 6
        // didn't call CreateBlockRules. Baseline.
        Assert.Equal(0, firewall.CreateBlockRulesCount);

        // Drive Apply.
        var ok = await engine.ApplyAsync(settings, TestContext.Current.CancellationToken);
        Assert.True(ok);

        // After Apply: monitor.Start was NOT called again (HotReload
        // doesn't recreate ETW). firewall.CreateBlockRules also not
        // called again. Counters stay at their post-ColdStart values.
        Assert.Equal(1, monitor.StartCount);
        Assert.Equal(0, monitor.StopCount);   // monitor.Stop is Stop-only, not Apply
        Assert.Equal(0, firewall.CreateBlockRulesCount);
        Assert.Equal(0, firewall.DeleteAllRulesCount);   // delete is Stop-only
    }
}
