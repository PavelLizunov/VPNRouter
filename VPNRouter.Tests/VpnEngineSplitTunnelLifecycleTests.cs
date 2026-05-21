// Task #49 (2026-05-21) — Split-tunnel happy-path lifecycle characterization
// tests, completing Agent C's Task #36-C deferred coverage.
//
// Background: Task #36-C (commit 681b61c, Group 1) shipped 3 ColdStart
// lifecycle tests with RoutingMode=full because the bundled profile
// catalogue is platform-specific (Windows-only names: Discord_Privacy,
// Browsers, Work_Suite, etc.) and Agent C couldn't trust the catalogue
// shape across CI environments at the time. Agent C documented the gap
// in Task #36-C's outcome section "Surprises encountered" + brief's
// "Follow-ups spawned" list:
//
//   > Split-tunnel happy-path lifecycle (Group 1 variant). Same scaffolding,
//   > just point ActiveProfile to a bundled name (Browsers works on
//   > Windows). Effort: trivial — could be a one-liner addition to
//   > Group 1 once we trust the bundled catalogue in CI.
//
// We now trust the catalogue — VPNRouter.Tests/bin/.../profiles/default.json
// is copied into the test output via VPNRouter.Tests.csproj's profile
// glob (verified via the Glob tool: profiles/default.json exists in
// VPNRouter.Tests/bin/Release/net8.0/profiles/). The "Browsers" profile
// has 22 entries with low scan-pattern overhead — a small enough fixture
// that the test stays sub-second.
//
// ── Scope realised ───────────────────────────────────────────────────────
//
// 2 tests, Windows-only (same SkipUnless gating as Task #36-C Group 1):
//
//   1. Start_SplitTunnel_Browsers_FiresLifecycleEvents — full ColdStart
//      against RoutingMode=split + ActiveProfile=Browsers. Pins:
//        - Apply fires exactly once (Phase 8 wiring same as full-tunnel).
//        - ActiveProfileName="Browsers" (NOT "FullTunnel" — proves the
//          pipeline picked the user's profile, not the inline synthetic).
//        - MonitoredProcesses non-empty — the scanner ran against a real
//          profile with 22 scan_patterns (Chrome, Firefox, etc.).
//        - IsRunning true post-StartAsync.
//
//   2. Stop_SplitTunnel_FiresRestoreThroughDnsHardening — symmetric Stop
//      test mirroring Group 1's test 2 but on a split-tunnel engine.
//      Same Restore-via-seam invariant: split-tunnel Stop hits the same
//      teardown path as full-tunnel Stop (proving the lifecycle code
//      path doesn't branch on routing mode at teardown).
//
// ── Why a new file (vs. extending VpnEngineLifecycleTests.cs) ────────────
//
// Per VPNRouter.Tests/CLAUDE.md "Layout" rule: one class — one file.
// Phase 2E (2026-05-17) extracted 42 classes out of the old UnitTest1.cs
// bag into per-file classes. Adding to VpnEngineLifecycleTests.cs (Task
// #36-C's 9-test file) would re-grow that file and violate the convention.
// A sibling file with the same scaffolding helpers via direct reflection
// access on VpnEngine internals keeps the existing 9 tests frozen
// (per coordination protocol with parallel agents in Task #41 Stage 2).
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
/// Characterization tests for the <see cref="VpnEngine"/> split-tunnel
/// happy-path lifecycle. Companion to <see cref="VpnEngineLifecycleTests"/>
/// (Task #36-C, full-tunnel only).
///
/// <para>Cross-references:
/// <see cref="VpnEngineLifecycleTests"/> (Group 1 full-tunnel tests this file mirrors),
/// <see cref="VpnEngineHotReloadLifecycleTests"/> (Task #49 hot-reload tests),
/// <see cref="WindowsDnsHardeningInjectionTests"/> (DNS-hardening seam wiring).</para>
/// </summary>
public sealed class VpnEngineSplitTunnelLifecycleTests
{
    // ─── Inline stubs (mirrors VpnEngineLifecycleTests pattern) ──────────

    private sealed class StubProcessScanner : IProcessScanner
    {
        /// <summary>
        /// Returns a canned non-empty ScanResult so the test pins
        /// "scanner produced results" without depending on the real
        /// scanner walking the OS process table (which on CI Windows
        /// would yield wildly different results per build agent).
        ///
        /// <para>The scan result mirrors what a real scan of the
        /// Browsers profile would surface if Chrome were running:
        /// one process name. Tests assert on MonitoredProcesses.Count
        /// rather than specific names so the fixture stays
        /// independent of which browser is running on CI.</para>
        /// </summary>
        public ScanResult ScanForProfile(Profile profile) =>
            new()
            {
                ProcessNames = new List<string> { "chrome.exe" },
                ScannedAt = DateTime.Now,
            };
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
        public void RaiseDummy()
        {
            ProcessStarted?.Invoke(this, new());
            ProcessStopped?.Invoke(this, new());
        }
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
    /// Build settings tuned for split-tunnel happy-path lifecycle test.
    /// Same shape as <see cref="VpnEngineLifecycleTests"/>'s helper but
    /// with <c>RoutingMode="split"</c> + <c>ActiveProfile="Browsers"</c>
    /// to engage the bundled-catalogue lookup path.
    ///
    /// <para>"Browsers" is chosen because: (a) it's bundled (in
    /// <c>profiles/default.json</c> via <see cref="VPNRouter.Core.Services.VpnEngine.BuildProfileSources"/>'s
    /// built-in source), (b) BlockOnVpnFail=false (so the firewall
    /// block-rule branch stays disabled — same as Group 1's full-tunnel
    /// FullTunnel synthetic), (c) the scan_patterns list is bounded
    /// (~22 entries) — small enough to keep the StubProcessScanner's
    /// canned result deterministic.</para>
    ///
    /// <para>Pubkey/short_id chosen to NOT collide with
    /// <see cref="PlaceholderDefense.KnownFingerprints"/> — otherwise
    /// Phase 5's <c>ConfigSanityCheck.CheckBeforeStart</c> would route
    /// us into the F-E AutoFailover branch instead of completing the
    /// happy path.</para>
    /// </summary>
    private static AppSettings BuildSplitTunnelSettings(
        string singBoxExePath, string activeProfile = "Browsers") =>
        new()
        {
            App = new AppConfig
            {
                ConfigMode = "generated",
                RoutingMode = "split",
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
                ClashApi = "127.0.0.1:65535",   // unused port so probes don't connect
            },
            Monitoring = new MonitoringSettings
            {
                HealthCheckInterval = 3600,    // 1 h — keeps periodic timer dormant
                MaxRestartAttempts = 5,
                RestartOnFailure = true,
            },
            ActiveProfile = activeProfile,
        };

    private static string CreateStubExe()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"vpnrouter-split-stub-{Guid.NewGuid():N}.exe");
        File.WriteAllText(path, "stub");
        return path;
    }

    /// <summary>
    /// Pre-populated FakeProcessRunner that lets the pipeline's TUN
    /// pre-cleanup pass without contacting real netsh/PowerShell.
    /// Mirrors <see cref="VpnEngineLifecycleTests"/>'s helper of the
    /// same shape.
    /// </summary>
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
        BuildSingBoxSpawnFake(int pid = 99201)
    {
        var handle = new FakeProcessHandle(pid);
        var runner = new FakeProcessRunner();
        runner.OnStart(_ => true, _ => handle);
        return (runner, handle);
    }

    /// <summary>
    /// Drive a full ColdStart against an isolated test environment in
    /// split-tunnel mode. Returns the running engine + capture surfaces.
    /// Caller MUST call <c>cleanup.Dispose()</c> to restore static seams
    /// and delete the stub exe.
    ///
    /// <para>Sibling to <see cref="VpnEngineLifecycleTests"/>'s
    /// <c>StartHappyPathAsync</c> — same shape but with split-tunnel
    /// settings + a real bundled profile name.</para>
    /// </summary>
    private static async Task<(VpnEngine engine,
                                NullWindowsDnsHardening dnsHardening,
                                FakeProcessHandle handle,
                                StubFirewallManager firewall,
                                StubProcessMonitor monitor,
                                IDisposable cleanup)>
        StartSplitTunnelHappyPathAsync(string activeProfile = "Browsers")
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

        var settings = BuildSplitTunnelSettings(stubExe, activeProfile);

        var cleanup = new SplitTunnelCleanup(
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

    private sealed class SplitTunnelCleanup : IDisposable
    {
        private readonly VpnEngine _engine;
        private readonly string _stubExe;
        private readonly IProcessRunner _prevSingBoxRunner;
        private readonly IProcessRunner _prevTunDiagRunner;
        private bool _disposed;

        public SplitTunnelCleanup(
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
            try { _engine.Stop(); } catch { /* best-effort */ }
            try { _engine.Dispose(); } catch { /* best-effort */ }
            SingBoxManager.Runner = _prevSingBoxRunner;
            TunAdapterDiagnostics.Runner = _prevTunDiagRunner;
            TunAdapterDiagnostics.ResetRemoveNetAdapterLatchForTests();
            try { File.Delete(_stubExe); } catch { /* best-effort */ }
        }
    }

    // ─── Tests ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Start_SplitTunnel_Browsers_FiresLifecycleEvents()
    {
        // Full ColdStart against the real bundled "Browsers" profile.
        // Pins that the pipeline:
        //  • Resolved the profile from the bundled catalogue (NOT the
        //    FullTunnel synthetic that Group 1's tests exercise).
        //  • Fired Phase 8's Apply seam exactly once.
        //  • ETW process monitor was started.
        //  • ActiveProfileName carries "Browsers".
        //  • Firewall block rules were NOT created (Browsers profile
        //    has block_on_vpn_fail=false in profiles/default.json).
        //  • MonitoredProcesses populated from the stub scanner.
        //
        // Cross-platform constraint: same as Group 1 — Windows-only
        // because SingBoxManager's Linux path uses pkexec/sudo argv +
        // a direct Process.Start("/usr/sbin/getcap") probe that isn't
        // routed through IProcessRunner.
        Assert.SkipUnless(OperatingSystem.IsWindows(),
            "ColdStart drives SingBoxManager's Windows spawn path; Linux uses pkexec + getcap shell-outs not behind IProcessRunner.");

        var (engine, dnsHardening, handle, firewall, monitor, cleanup) =
            await StartSplitTunnelHappyPathAsync(activeProfile: "Browsers");
        using var _ = cleanup;

        // Phase 8 fired Apply via the seam — exactly once, settings non-null.
        Assert.Equal(1, dnsHardening.ApplyCount);
        Assert.Equal(0, dnsHardening.RestoreCount);
        Assert.Equal("Apply", dnsHardening.Calls[0].Op);
        Assert.NotNull(dnsHardening.Calls[0].Settings);

        // Engine is running, active server address propagated.
        Assert.True(engine.IsRunning);
        Assert.Equal("10.0.0.1", engine.ActiveServerAddress);

        // Critical split-tunnel-specific assertions:
        //   ActiveProfileName carries the bundled profile name, not
        //   the FullTunnel synthetic from Group 1.
        Assert.Equal("Browsers", engine.ActiveProfileName);
        //   Routing mode is split (the FullTunnel path overrides this
        //   to "split" too, but we set it explicitly here and want to
        //   confirm the pipeline didn't escalate to full).
        Assert.Equal("split", engine.ActiveRoutingMode);

        // MonitoredProcesses was populated from the stub scanner's
        // canned result — pins that the scan phase ran (didn't skip
        // because of split-tunnel-specific routing decisions).
        Assert.NotEmpty(engine.MonitoredProcesses);
        Assert.Contains("chrome.exe", engine.MonitoredProcesses,
            StringComparer.OrdinalIgnoreCase);

        // Firewall block rules were NOT created (Browsers profile has
        // BlockOnVpnFail=false in the bundled catalogue). Pin: Phase 6
        // honoured the profile-level flag, didn't force-create.
        Assert.Equal(0, firewall.CreateBlockRulesCount);

        // Phase 8 started the ETW monitor.
        Assert.Equal(1, monitor.StartCount);

        // FakeProcessHandle hasn't exited — sing-box is "alive".
        Assert.False(handle.HasExited);
    }

    [Fact]
    public async Task Stop_SplitTunnel_FiresRestoreThroughDnsHardening()
    {
        // Symmetric Stop test for split-tunnel — proves the teardown
        // path doesn't branch on routing mode. Same Restore-via-seam
        // invariant as Group 1's Stop_AfterStart_FiresRestoreThroughDnsHardening.
        Assert.SkipUnless(OperatingSystem.IsWindows(),
            "ColdStart prerequisite is Windows-only.");

        var (engine, dnsHardening, handle, firewall, monitor, cleanup) =
            await StartSplitTunnelHappyPathAsync(activeProfile: "Browsers");
        try
        {
            // Snapshot before Stop.
            Assert.Equal(1, dnsHardening.ApplyCount);
            Assert.Equal(0, dnsHardening.RestoreCount);
            Assert.True(engine.IsRunning);
            Assert.Equal("Browsers", engine.ActiveProfileName);

            engine.Stop();

            // Restore fired exactly once via the seam.
            Assert.Equal(1, dnsHardening.RestoreCount);
            Assert.Equal("Restore", dnsHardening.Calls.Last().Op);
            Assert.Null(dnsHardening.Calls.Last().Settings);

            // Engine is no longer running. SingBoxManager.Stop → Kill
            // → handle disposed.
            Assert.False(engine.IsRunning);
            Assert.True(handle.HasExited);

            // ETW monitor stopped.
            Assert.True(monitor.StopCount >= 1);

            // Firewall: Browsers profile has BlockOnVpnFail=false, so
            // the Stop path's DisableBlockRules+DeleteAllRules branch
            // does NOT fire (gated by _activeProfile?.BlockOnVpnFail
            // at VpnEngine.cs:390). Pin that the gate held.
            Assert.Equal(0, firewall.DisableBlockRulesCount);
            Assert.Equal(0, firewall.DeleteAllRulesCount);
        }
        finally
        {
            cleanup.Dispose();
        }
    }
}
