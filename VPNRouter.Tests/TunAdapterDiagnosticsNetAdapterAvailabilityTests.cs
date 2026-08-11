using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// TUN orphan-removal regression suite.
///
/// <para><b>2026-06-08 root-cause rewrite (Pavel main-machine crash loop,
/// v2.41.1):</b> <c>Remove-NetAdapter</c> IS NOT A CMDLET
/// (<c>Get-Command Remove-NetAdapter</c> → 0; the NetAdapter module exports
/// only Get/Set/Enable/Disable/Rename/Restart). The old
/// <c>Get-NetAdapter | Remove-NetAdapter</c> therefore ALWAYS threw
/// CommandNotFoundException and fell through to netsh-disable — which never
/// deletes the device record. The stale record made the next
/// WintunCreateAdapter crash with "The device is not ready for use" /
/// "Cannot create a file ... already exists" (7 crashes in one day on Pavel's
/// main machine). The whole PinkuDani "module missing" framing + the Alena
/// a20a047 CommandNotFoundException latch were treating the symptom of calling
/// a phantom cmdlet.</para>
///
/// <para><b>Fix:</b> resolve the orphan's PnP InstanceId via the REAL
/// <see cref="TunAdapterDiagnostics.TryRemoveAdapterAsync"/> → Get-NetAdapter
/// -ExpandProperty PnPDeviceID, then delete the device record with the
/// built-in <c>pnputil /remove-device</c> (plain, then /force retry). The
/// availability probe is repointed from <c>Get-Module NetAdapter</c> to
/// <c>Get-Command Get-NetAdapter</c>. Verified on the dev VM (read-only):
/// Get-NetAdapter exposes PnPDeviceID and pnputil targets the same InstanceId.</para>
///
/// <para>Tests assign a <see cref="FakeProcessRunner"/> to the static
/// <see cref="TunAdapterDiagnostics.Runner"/> seam and assert the shell-out
/// shapes (Get-NetAdapter resolve + pnputil remove). Windows-only helpers
/// silently skip on non-Windows so the class stays portable.</para>
/// </summary>
public sealed class TunAdapterDiagnosticsNetAdapterAvailabilityTests
{
    // ─── helpers ────────────────────────────────────────────────────────

    private static FakeProcessRunner NewRunner()
    {
        var fake = new FakeProcessRunner();
        fake.OnRun(_ => true,
            new ProcessResult(0, "", "", TimeSpan.FromMilliseconds(5), false));
        return fake;
    }

    private static async Task WithFakeAsync(
        FakeProcessRunner fake, bool removalAvailable, Func<Task> body)
    {
        var previous = TunAdapterDiagnostics.Runner;
        var previousDelay = TunAdapterDiagnostics.RemovalDelayAsync;
        fake.OnRun(IsPnpUtilScan, Ok());
        fake.OnRun(IsPnpUtilInstanceQuery, Ok("No devices were found.\r\n"));
        TunAdapterDiagnostics.Runner = fake;
        TunAdapterDiagnostics.RemovalDelayAsync = static (_, _) => Task.CompletedTask;
        TunAdapterDiagnostics.ResetRemoveNetAdapterLatchForTests();
        TunAdapterDiagnostics.SetNetAdapterModuleAvailableForTests(removalAvailable);
        try { await body(); }
        finally
        {
            TunAdapterDiagnostics.Runner = previous;
            TunAdapterDiagnostics.RemovalDelayAsync = previousDelay;
            TunAdapterDiagnostics.ResetRemoveNetAdapterLatchForTests();
        }
    }

    private static async Task WithFakeNoPresetAsync(FakeProcessRunner fake, Func<Task> body)
    {
        var previous = TunAdapterDiagnostics.Runner;
        var previousDelay = TunAdapterDiagnostics.RemovalDelayAsync;
        fake.OnRun(IsPnpUtilScan, Ok());
        fake.OnRun(IsPnpUtilInstanceQuery, Ok("No devices were found.\r\n"));
        TunAdapterDiagnostics.Runner = fake;
        TunAdapterDiagnostics.RemovalDelayAsync = static (_, _) => Task.CompletedTask;
        TunAdapterDiagnostics.ResetRemoveNetAdapterLatchForTests();
        try { await body(); }
        finally
        {
            TunAdapterDiagnostics.Runner = previous;
            TunAdapterDiagnostics.RemovalDelayAsync = previousDelay;
            TunAdapterDiagnostics.ResetRemoveNetAdapterLatchForTests();
        }
    }

    /// <summary>The repointed availability probe: `Get-Command Get-NetAdapter`.</summary>
    private static bool IsGetNetAdapterProbe(ProcessRequest r) =>
        r.ExecutablePath == "powershell.exe"
        && r.Arguments.Count == 4
        && r.Arguments[3].Contains("Get-Command Get-NetAdapter");

    /// <summary>Step 1: resolve InstanceId via Get-NetAdapter → PnPDeviceID.</summary>
    private static bool IsGetNetAdapterResolve(ProcessRequest r) =>
        r.ExecutablePath == "powershell.exe"
        && r.Arguments.Count == 4
        && r.Arguments[3].Contains("Get-NetAdapter -Name")
        && r.Arguments[3].Contains("PnPDeviceID");

    /// <summary>Step 2: pnputil /remove-device (plain, no /force).</summary>
    private static bool IsPnpUtilRemovePlain(ProcessRequest r) =>
        r.ExecutablePath == "pnputil.exe"
        && r.Arguments.Contains("/remove-device")
        && !r.Arguments.Contains("/force");

    /// <summary>pnputil /remove-device /force.</summary>
    private static bool IsPnpUtilRemoveForce(ProcessRequest r) =>
        r.ExecutablePath == "pnputil.exe"
        && r.Arguments.Contains("/remove-device")
        && r.Arguments.Contains("/force");

    private static bool IsPnpUtilScan(ProcessRequest r) =>
        r.ExecutablePath == "pnputil.exe" && r.Arguments.Contains("/scan-devices");

    private static bool IsPnpUtilInstanceQuery(ProcessRequest r) =>
        r.ExecutablePath == "pnputil.exe" && r.Arguments.Contains("/enum-devices");

    private static bool IsNetshDisable(ProcessRequest r) =>
        r.ExecutablePath == "netsh" && r.Arguments.Contains("admin=disabled");

    private static bool IsNetshEnumeration(ProcessRequest r) =>
        r.ExecutablePath == "netsh"
        && r.Arguments.Count == 3
        && r.Arguments[0] == "interface" && r.Arguments[1] == "show" && r.Arguments[2] == "interface";

    private static ProcessResult Ok(string stdout = "") =>
        new ProcessResult(0, stdout, "", TimeSpan.FromMilliseconds(5), false);

    // ─── Test 1: available + adapter exists → resolve then pnputil remove ──

    [Fact]
    public async Task Available_AdapterExists_ResolvesInstanceIdThenPnpUtilRemoves()
    {
        if (!OperatingSystem.IsWindows()) return;

        const string instanceId = @"ROOT\NET\0001";
        var fake = new FakeProcessRunner();
        fake.OnRun(IsGetNetAdapterResolve, Ok(instanceId + "\r\n"));
        fake.OnRun(IsPnpUtilRemovePlain, Ok());

        await WithFakeAsync(fake, removalAvailable: true, async () =>
        {
            var ok = await TunAdapterDiagnostics.TryRemoveAdapterAsync(
                logger: null, adapterName: "VPNRouter-TUN", context: "test.remove");
            Assert.True(ok);
        });

        // Exactly one Get-NetAdapter resolve, then one pnputil /remove-device
        // carrying the resolved InstanceId. No phantom Remove-NetAdapter.
        Assert.Single(fake.RunCalls.Where(IsGetNetAdapterResolve));
        var pnp = fake.RunCalls.Where(IsPnpUtilRemovePlain).ToList();
        Assert.Single(pnp);
        Assert.Contains(instanceId, pnp[0].Arguments);
        Assert.DoesNotContain(fake.RunCalls,
            c => c.ExecutablePath == "powershell.exe" && c.Arguments.Any(a => a.Contains("Remove-NetAdapter")));
    }

    // ─── Test 2: adapter already gone (empty resolve) → idempotent, no pnputil ──

    [Fact]
    public async Task Available_AdapterGone_EmptyResolve_NoPnpUtil_ReturnsTrue()
    {
        if (!OperatingSystem.IsWindows()) return;

        var fake = new FakeProcessRunner();
        fake.OnRun(IsGetNetAdapterResolve, Ok("")); // no adapter → empty stdout

        await WithFakeAsync(fake, removalAvailable: true, async () =>
        {
            var ok = await TunAdapterDiagnostics.TryRemoveAdapterAsync(
                logger: null, adapterName: "VPNRouter-TUN", context: "test.gone");
            Assert.True(ok); // idempotent success — nothing to remove
        });

        Assert.DoesNotContain(fake.RunCalls, c => c.ExecutablePath == "pnputil.exe");
    }

    // ─── Test 3: pnputil plain refused → /force retry ──────────────────────

    [Fact]
    public async Task Available_PnpUtilPlainRefused_RetriesWithForce()
    {
        if (!OperatingSystem.IsWindows()) return;

        var fake = new FakeProcessRunner();
        fake.OnRun(IsGetNetAdapterResolve, Ok(@"ROOT\NET\0002" + "\r\n"));
        // Order matters: register the /force matcher first so it wins for the
        // force call; plain matcher returns a refusal exit.
        fake.OnRun(IsPnpUtilRemoveForce, Ok());
        fake.OnRun(IsPnpUtilRemovePlain,
            new ProcessResult(1, "", "remove refused", TimeSpan.FromMilliseconds(5), false));

        await WithFakeAsync(fake, removalAvailable: true, async () =>
        {
            var ok = await TunAdapterDiagnostics.TryRemoveAdapterAsync(
                logger: null, adapterName: "VPNRouter-TUN", context: "test.force");
            Assert.True(ok);
        });

        Assert.Single(fake.RunCalls.Where(IsPnpUtilRemovePlain));
        Assert.Single(fake.RunCalls.Where(IsPnpUtilRemoveForce));
    }

    // ─── Test 4: probe fires once across many calls ────────────────────────

    [Fact]
    public async Task Available_Probe_CachedAcrossCalls()
    {
        if (!OperatingSystem.IsWindows()) return;

        var fake = new FakeProcessRunner();
        fake.OnRun(IsGetNetAdapterProbe, Ok("1\r\n"));
        fake.OnRun(IsGetNetAdapterResolve, Ok("")); // adapter gone — keep it cheap
        fake.OnRun(_ => true, Ok());

        await WithFakeNoPresetAsync(fake, async () =>
        {
            for (var i = 0; i < 5; i++)
                _ = await TunAdapterDiagnostics.TryRemoveAdapterAsync(
                    logger: null, adapterName: "VPNRouter-TUN", context: $"test.cache{i}");
        });

        // The Get-Command Get-NetAdapter probe runs exactly once (Lazy-cached),
        // even though resolve runs all 5 times.
        Assert.Single(fake.RunCalls.Where(IsGetNetAdapterProbe));
        Assert.Equal(5, fake.RunCalls.Where(IsGetNetAdapterResolve).Count());
    }

    // ─── Test 5: removal unavailable → PreStartCleanup uses netsh disable ──

    [Fact]
    public async Task RemovalUnavailable_PreStartCleanup_FallsBackToNetshDisable()
    {
        if (!OperatingSystem.IsWindows()) return;

        var fake = new FakeProcessRunner();
        fake.OnRun(IsNetshEnumeration,
            new ProcessResult(0,
                Stdout: """
                Admin State    State          Type             Interface Name
                -------------------------------------------------------------------------
                Enabled        Connected      Dedicated        Ethernet
                Disabled       Disconnected   Dedicated        VPNRouter-TUN
                """,
                Stderr: "", Duration: TimeSpan.FromMilliseconds(10), TimedOut: false));
        fake.OnRun(IsNetshDisable, new ProcessResult(0, "Ok.", "", TimeSpan.FromMilliseconds(5), false));

        await WithFakeAsync(fake, removalAvailable: false, async () =>
        {
            _ = await TunAdapterDiagnostics.PreStartCleanupAsync(logger: null, context: "test.fallback");
        });

        Assert.Contains(fake.RunCalls.Where(IsNetshDisable),
            c => c.Arguments.Contains("name=VPNRouter-TUN"));
        // No pnputil and no Get-NetAdapter resolve — removal path skipped.
        Assert.DoesNotContain(fake.RunCalls, c => c.ExecutablePath == "pnputil.exe");
        Assert.DoesNotContain(fake.RunCalls, IsGetNetAdapterResolve);
    }

    // ─── Test 6: removal unavailable → actionable INF once ─────────────────

    [Fact]
    public async Task RemovalUnavailable_FirstCall_LogsActionableInfoOnce()
    {
        if (!OperatingSystem.IsWindows()) return;

        var fake = NewRunner();
        var sink = new InMemorySink();
        var logger = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger();

        await WithFakeAsync(fake, removalAvailable: false, async () =>
        {
            _ = await TunAdapterDiagnostics.TryRemoveAdapterAsync(logger, "VPNRouter-TUN", "t.first");
            _ = await TunAdapterDiagnostics.TryRemoveAdapterAsync(logger, "VPNRouter-TUN", "t.second");
            _ = await TunAdapterDiagnostics.TryRemoveAdapterAsync(logger, "VPNRouter-TUN", "t.third");
        });

        var infEvents = sink.Events(LogEventLevel.Information)
            .Where(s => s.Contains("Get-NetAdapter cmdlet unavailable"))
            .ToList();
        Assert.Single(infEvents);
        Assert.Contains("netsh-disable fallback", infEvents[0]);
    }

    // ─── Test 7: Get-NetAdapter genuinely not-found → latch + skip ──────────

    [Fact]
    public async Task ResolveThrowsCommandNotFound_LatchesAndSkipsSecondResolve()
    {
        if (!OperatingSystem.IsWindows()) return;

        // Degenerate: Get-NetAdapter itself unresolvable (broken PS state). The
        // resolve returns CommandNotFoundException in stderr → latch so the
        // second call short-circuits (no second resolve spawn).
        var fake = new FakeProcessRunner();
        fake.OnRun(IsGetNetAdapterResolve,
            new ProcessResult(1, "",
                "Get-NetAdapter : ... [], CommandNotFoundException",
                TimeSpan.FromMilliseconds(5), false));

        await WithFakeAsync(fake, removalAvailable: true, async () =>
        {
            var r1 = await TunAdapterDiagnostics.TryRemoveAdapterAsync(null, "VPNRouter-TUN", "t.cnf1");
            Assert.False(r1);
            var r2 = await TunAdapterDiagnostics.TryRemoveAdapterAsync(null, "VPNRouter-TUN", "t.cnf2");
            Assert.False(r2);
        });

        // Latched on first failure → exactly one resolve spawn across both calls.
        Assert.Single(fake.RunCalls.Where(IsGetNetAdapterResolve));
        Assert.DoesNotContain(fake.RunCalls, c => c.ExecutablePath == "pnputil.exe");
    }

    // ─── Serilog test sink ──────────────────────────────────────────────

    private sealed class InMemorySink : ILogEventSink
    {
        private readonly List<(LogEventLevel Level, string Rendered)> _events = new();

        public void Emit(LogEvent logEvent)
        {
            var rendered = logEvent.RenderMessage();
            lock (_events) _events.Add((logEvent.Level, rendered));
        }

        public IReadOnlyList<string> Events(LogEventLevel level)
        {
            lock (_events)
                return _events.Where(e => e.Level == level).Select(e => e.Rendered).ToList();
        }
    }
}
