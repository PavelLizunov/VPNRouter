// Task #49 (2026-05-21) — Symmetric ON-case test for the BR-7 deferred
// DNS-leak lockdown branch in the warmup probe, completing Agent C's
// Task #36-C Group 5 deferred coverage.
//
// Background: Task #36-C (commit 681b61c) shipped the OFF-case test
// (Start_DnsLeakLockdownOff_DoesNotInvokeEnableLockdown) but punted on
// the symmetric ON case. Agent C's brief documented:
//
//   > The symmetric DnsLeakLockdown=true → EnableLockdownCount=1 case is
//   > deferred: the BR-7 success branch in ScheduleWarmupProbe uses
//   > `new HttpClient` directly (not injected) and probes
//   > https://www.gstatic.com/generate_204 for real. Deterministic
//   > coverage of the success path needs an IHttpClientFactory injection
//   > into StartupPipeline — separate seam, out of scope.
//
// Task #49 adds the seam: `StartupPipeline.WarmupHttp` static IHttpClient
// field with null default (preserving the inline `new HttpClient`
// production behaviour). Tests overwrite to a FakeHttpClient that returns
// 200 OK on the gstatic probe URL, swap restored in cleanup. Mirrors the
// existing static-seam patterns for SingBoxManager.Runner and
// TunAdapterDiagnostics.Runner.
//
// ── Scope realised ───────────────────────────────────────────────────────
//
// 2 tests, Windows-only:
//
//   1. Start_DnsLeakLockdownOn_WarmupSuccess_InvokesEnableLockdown — drives
//      a successful warmup probe via the new IHttpClient seam, then polls
//      NullWindowsDnsHardening.EnableLockdownCount for up to 5s to wait
//      for the fire-and-forget Task.Run body to complete. Asserts the
//      BR-7 success branch fired EnableLockdownIfConfigured with settings
//      carrying DnsLeakLockdown=true.
//
//   2. Start_DnsLeakLockdownOn_WarmupFailure_DoesNotInvokeEnableLockdown
//      — symmetric defence pin: even with DnsLeakLockdown=true, a failing
//      warmup probe (FakeHttpClient ThrowOn the gstatic URL) does NOT
//      fire EnableLockdownIfConfigured. The warmup loop expires after
//      15 attempts × 1s; we Stop the engine after 2s to short-circuit
//      the loop via the ct (faster than waiting the full 15s). Pin:
//      EnableLockdownCount stays at 0.
//
// Why polling vs. signal:
//   The fire-and-forget Task.Run inside ScheduleWarmupProbe has no
//   awaitable handle exposed by the pipeline. We could add one (a
//   TaskCompletionSource exposed via a new IStartupHost callback or via
//   a "WarmupCompleted" event on VpnEngine), but that's scope creep — the
//   poll-with-timeout approach gets us deterministic coverage in O(1s)
//   wall-clock without further production-API surface area.
//
// Brief: plans/phase4-lifecycle-test-gaps-task49-2026-05-21.md.

#nullable enable

using System.Net.Http;
using System.Text;
using VPNRouter.Core;
using VPNRouter.Core.Interfaces;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;

namespace VPNRouter.Tests;

/// <summary>
/// Characterization tests for the BR-7 deferred-lockdown success branch
/// in <see cref="StartupPipeline.ScheduleWarmupProbe"/>. Companion to
/// <see cref="VpnEngineLifecycleTests"/>'s Group 5 OFF-case test.
///
/// <para>Cross-references:
/// <see cref="VpnEngineLifecycleTests"/> (Group 5 OFF-case),
/// <see cref="StartupPipeline.WarmupHttp"/> (the seam this file exercises),
/// <see cref="WindowsDnsHardeningInjectionTests"/> (DNS-hardening seam wiring).</para>
/// </summary>
public sealed class VpnEngineDnsLockdownLifecycleTests
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
            $"vpnrouter-dnslockdown-stub-{Guid.NewGuid():N}.exe");
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
        BuildSingBoxSpawnFake(int pid = 99401)
    {
        var handle = new FakeProcessHandle(pid);
        var runner = new FakeProcessRunner();
        runner.OnStart(_ => true, _ => handle);
        return (runner, handle);
    }

    /// <summary>
    /// Build a FakeHttpClient that returns 200 OK on the gstatic warmup
    /// probe URL. The body is the canonical "" (the real gstatic.com
    /// returns HTTP 204 with empty body — we use 200 + empty body which
    /// still hits the IsSuccess() branch in ScheduleWarmupProbe).
    /// </summary>
    private static FakeHttpClient BuildWarmupSuccessHttpClient() =>
        new FakeHttpClient().Setup(
            "gstatic.com/generate_204",
            new HttpResponse(
                StatusCode: 200,
                Headers: new Dictionary<string, string>(),
                Body: Array.Empty<byte>(),
                Duration: TimeSpan.FromMilliseconds(1)));

    /// <summary>
    /// Build a FakeHttpClient that throws on the gstatic warmup probe URL,
    /// simulating a network-level probe failure (no TUN routing).
    /// </summary>
    private static FakeHttpClient BuildWarmupFailureHttpClient() =>
        new FakeHttpClient().ThrowOn(
            "gstatic.com/generate_204",
            new HttpRequestException("simulated TUN-not-routing"));

    /// <summary>
    /// Wait up to <paramref name="timeout"/> for <paramref name="predicate"/>
    /// to return true. Polls every 50 ms. Returns true if the predicate
    /// fires, false if the timeout elapsed.
    /// </summary>
    private static async Task<bool> WaitForAsync(
        Func<bool> predicate, TimeSpan timeout)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (predicate()) return true;
            await Task.Delay(50);
        }
        return predicate();
    }

    private sealed class DnsLockdownCleanup : IDisposable
    {
        private readonly VpnEngine _engine;
        private readonly string _stubExe;
        private readonly IProcessRunner _prevSingBoxRunner;
        private readonly IProcessRunner _prevTunDiagRunner;
        private readonly IHttpClient? _prevWarmupHttp;
        private bool _disposed;

        public DnsLockdownCleanup(
            VpnEngine engine,
            string stubExe,
            IProcessRunner prevSingBoxRunner,
            IProcessRunner prevTunDiagRunner,
            IHttpClient? prevWarmupHttp)
        {
            _engine = engine;
            _stubExe = stubExe;
            _prevSingBoxRunner = prevSingBoxRunner;
            _prevTunDiagRunner = prevTunDiagRunner;
            _prevWarmupHttp = prevWarmupHttp;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _engine.Stop(); } catch { /* best-effort */ }
            try { _engine.Dispose(); } catch { /* best-effort */ }
            SingBoxManager.Runner = _prevSingBoxRunner;
            TunAdapterDiagnostics.Runner = _prevTunDiagRunner;
            StartupPipeline.WarmupHttp = _prevWarmupHttp;
            TunAdapterDiagnostics.ResetRemoveNetAdapterLatchForTests();
            try { File.Delete(_stubExe); } catch { /* best-effort */ }
        }
    }

    // ─── Tests ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Start_DnsLeakLockdownOn_WarmupSuccess_InvokesEnableLockdown()
    {
        // Drives a full ColdStart with DnsLeakLockdown=true AND a
        // FakeHttpClient seam that returns 200 on the gstatic probe.
        // The fire-and-forget warmup probe's success branch must fire
        // _dnsHardening.EnableLockdownIfConfigured exactly once.
        //
        // Why we poll instead of awaiting: ScheduleWarmupProbe's
        // background Task.Run isn't observable from the caller — there's
        // no completion signal exposed by the pipeline. We poll the
        // NullWindowsDnsHardening capture surface for up to 5s — well
        // under the 15s warmup-loop budget but generous against any
        // CI/dev VM jitter.
        Assert.SkipUnless(OperatingSystem.IsWindows(),
            "ColdStart drives SingBoxManager's Windows spawn path; Linux uses pkexec + getcap shell-outs not behind IProcessRunner.");

        var prevSingBoxRunner = SingBoxManager.Runner;
        var prevTunDiagRunner = TunAdapterDiagnostics.Runner;
        var prevWarmupHttp = StartupPipeline.WarmupHttp;
        TunAdapterDiagnostics.ResetRemoveNetAdapterLatchForTests();
        TunAdapterDiagnostics.SetNetAdapterModuleAvailableForTests(false);

        var (singBoxRunner, handle) = BuildSingBoxSpawnFake();
        SingBoxManager.Runner = singBoxRunner;
        TunAdapterDiagnostics.Runner = BuildTunCleanupFake();

        // Install the warmup seam BEFORE engine.StartAsync so the fire-
        // and-forget Task.Run snapshot picks it up.
        var fakeWarmupHttp = BuildWarmupSuccessHttpClient();
        StartupPipeline.WarmupHttp = fakeWarmupHttp;

        var stubExe = CreateStubExe();
        var dnsHardening = new NullWindowsDnsHardening();
        var engine = BuildEngine(dnsHardening, out var firewall, out var monitor);

        var settings = BuildHappyPathSettings(stubExe, dnsLeakLockdown: true);

        using var cleanup = new DnsLockdownCleanup(
            engine, stubExe, prevSingBoxRunner, prevTunDiagRunner, prevWarmupHttp);

        await engine.StartAsync(settings, TestContext.Current.CancellationToken, skipVpnConflictCheck: true);

        // Phase 8 fired Apply with settings carrying DnsLeakLockdown=true.
        Assert.Equal(1, dnsHardening.ApplyCount);
        var applyCall = dnsHardening.Calls.First(c => c.Op == "Apply");
        Assert.NotNull(applyCall.Settings);
        Assert.True(applyCall.Settings!.App.DnsLeakLockdown);

        // Wait for the warmup probe's fire-and-forget Task.Run to fire
        // EnableLockdownIfConfigured. The probe has `await Task.Delay(1000)`
        // before the first HTTP attempt, so we expect a minimum 1s delay
        // before the success branch runs. 5s timeout is conservative.
        var fired = await WaitForAsync(
            () => dnsHardening.EnableLockdownCount >= 1,
            TimeSpan.FromSeconds(5));

        Assert.True(fired,
            "Expected the BR-7 success branch to fire EnableLockdownIfConfigured within 5s. " +
            $"Actual EnableLockdownCount={dnsHardening.EnableLockdownCount}, " +
            $"FakeHttpClient.SentRequests={fakeWarmupHttp.SentRequests.Count}");

        // EnableLockdownIfConfigured was called with settings carrying
        // DnsLeakLockdown=true.
        Assert.Equal(1, dnsHardening.EnableLockdownCount);
        var lockdownCall = dnsHardening.Calls.First(c => c.Op == "EnableLockdownIfConfigured");
        Assert.NotNull(lockdownCall.Settings);
        Assert.True(lockdownCall.Settings!.App.DnsLeakLockdown);

        // The FakeHttpClient received at least one probe request.
        Assert.True(fakeWarmupHttp.SentRequests.Count >= 1);
        Assert.Contains(
            fakeWarmupHttp.SentRequests,
            r => r.Uri.ToString().Contains("gstatic.com/generate_204"));
    }

    [Fact]
    public async Task Start_DnsLeakLockdownOn_WarmupFailure_DoesNotInvokeEnableLockdown()
    {
        // Symmetric defence pin: even with DnsLeakLockdown=true, a
        // FAILING warmup probe (HTTP throws) must NOT fire the BR-7
        // EnableLockdownIfConfigured branch. The probe's warning-only
        // failure-path (line 1185 in StartupPipeline.cs at the time of
        // writing) is the only path that doesn't reach the
        // EnableLockdownIfConfigured call.
        //
        // We Stop the engine after 2s to short-circuit the 15-attempt
        // warmup loop via the cancellation token — otherwise we'd wait
        // ~15s for the loop to naturally expire (each attempt does
        // Task.Delay(1000) + HTTP-throws-immediately).
        Assert.SkipUnless(OperatingSystem.IsWindows(),
            "ColdStart prerequisite is Windows-only.");

        var prevSingBoxRunner = SingBoxManager.Runner;
        var prevTunDiagRunner = TunAdapterDiagnostics.Runner;
        var prevWarmupHttp = StartupPipeline.WarmupHttp;
        TunAdapterDiagnostics.ResetRemoveNetAdapterLatchForTests();
        TunAdapterDiagnostics.SetNetAdapterModuleAvailableForTests(false);

        var (singBoxRunner, handle) = BuildSingBoxSpawnFake();
        SingBoxManager.Runner = singBoxRunner;
        TunAdapterDiagnostics.Runner = BuildTunCleanupFake();

        var fakeWarmupHttp = BuildWarmupFailureHttpClient();
        StartupPipeline.WarmupHttp = fakeWarmupHttp;

        var stubExe = CreateStubExe();
        var dnsHardening = new NullWindowsDnsHardening();
        var engine = BuildEngine(dnsHardening, out var firewall, out var monitor);

        var settings = BuildHappyPathSettings(stubExe, dnsLeakLockdown: true);

        using var cleanup = new DnsLockdownCleanup(
            engine, stubExe, prevSingBoxRunner, prevTunDiagRunner, prevWarmupHttp);

        await engine.StartAsync(settings, TestContext.Current.CancellationToken, skipVpnConflictCheck: true);

        // Phase 8 fired Apply with DnsLeakLockdown=true.
        Assert.Equal(1, dnsHardening.ApplyCount);

        // Let the warmup probe attempt at least once (probe has
        // Task.Delay(1000) before HTTP). 2s gives the probe time to
        // hit the throwing fake at least once + fall into the failure
        // path — but isn't long enough for the full 15-attempt loop
        // to expire on its own (15s).
        await Task.Delay(2000, TestContext.Current.CancellationToken);

        // Stop the engine — cancels the warmup probe's ct, so the
        // failure branch's final "warmup failed" warning runs without
        // EnableLockdownIfConfigured firing.
        engine.Stop();

        // Final assertion: even with DnsLeakLockdown=true, the failure
        // branch did NOT fire EnableLockdownIfConfigured. The Stop
        // cancelled the warmup loop's ct before its 15-attempt expiry,
        // but either way the failure branch never reaches the lockdown
        // call (the BR-7 lockdown is ONLY in the success branch).
        Assert.Equal(0, dnsHardening.EnableLockdownCount);
        Assert.Equal(1, dnsHardening.RestoreCount);   // Stop drove Restore

        // The FakeHttpClient received at least one probe request before
        // we stopped — confirms the seam was actually used.
        Assert.True(fakeWarmupHttp.SentRequests.Count >= 1,
            $"Expected at least one warmup probe request via the seam, got {fakeWarmupHttp.SentRequests.Count}");
    }
}
