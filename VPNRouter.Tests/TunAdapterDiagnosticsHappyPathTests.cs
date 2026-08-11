#nullable enable
using System;
using System.Linq;
using System.Threading.Tasks;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// Task #36-B (Phase 4 prep, 2026-05-21) happy-path coverage for
/// <see cref="TunAdapterDiagnostics.PreStartCleanupAsync"/>. Wire-shape
/// (<c>TunAdapterDiagnosticsProcessRunnerWireShapeTests</c>) and module-
/// availability (<c>TunAdapterDiagnosticsNetAdapterAvailabilityTests</c>)
/// suites pin per-call argv + cache invariants. This file pins the
/// orchestrator's end-to-end "orphan found → cleanup succeeds, count
/// returned" contract that production callers (VpnEngine pre-start,
/// SingBoxManager auto-restart) actually consume.
///
/// <para>Three happy paths:</para>
/// <list type="number">
/// <item>NetAdapter module available + orphan found → PowerShell
/// Remove-NetAdapter fires, count == 1.</item>
/// <item>NetAdapter module unavailable + orphan found → CIM resolves the
/// exact PnP ID and pnputil removes it, count == 1.</item>
/// <item>No orphan in enumeration + module unavailable → the direct-by-name
/// CIM lookup confirms idempotent absence, count == 1.</item>
/// </list>
///
/// <para>All three are Windows-only (PreStartCleanupAsync early-returns
/// 0 on non-Windows via <see cref="OperatingSystem.IsWindows"/>). Linux
/// CI skips via <see cref="Assert.SkipUnless(bool, string)"/> per the
/// post-ddc2399 pattern.</para>
/// </summary>
public sealed class TunAdapterDiagnosticsHappyPathTests
{
    // ─── helpers ────────────────────────────────────────────────────────

    /// <summary>Identify the `netsh interface show interface` enumeration
    /// call shape (single source of truth for matchers/assertions).</summary>
    private static bool IsNetshEnumeration(ProcessRequest r)
    {
        return r.ExecutablePath == "netsh"
            && r.Arguments.Count == 3
            && r.Arguments[0] == "interface"
            && r.Arguments[1] == "show"
            && r.Arguments[2] == "interface";
    }

    /// <summary>Identify a `netsh interface set interface name=... admin=disabled`
    /// call shape — the cleanup path used by both DisableOrphanedAdapter
    /// and TryDisableAdapterViaNetshAsync (Fix #4 fallback).</summary>
    private static bool IsNetshDisable(ProcessRequest r)
    {
        return r.ExecutablePath == "netsh"
            && r.Arguments.Contains("admin=disabled");
    }

    /// <summary>Identify the Get-NetAdapter → PnPDeviceID resolve call shape
    /// (step 1 of the 2026-06-08 pnputil removal — replaces the phantom
    /// Remove-NetAdapter that never existed).</summary>
    private static bool IsGetNetAdapterResolve(ProcessRequest r)
    {
        return r.ExecutablePath == "powershell.exe"
            && r.Arguments.Count == 4
            && r.Arguments[3].Contains("Get-NetAdapter -Name")
            && r.Arguments[3].Contains("PnPDeviceID");
    }

    private static bool IsCimResolve(ProcessRequest r)
    {
        return r.ExecutablePath == "powershell.exe"
            && r.Arguments.Count == 4
            && r.Arguments[3].Contains("Get-CimInstance -ClassName Win32_NetworkAdapter")
            && r.Arguments[3].Contains("PNPDeviceID");
    }

    /// <summary>Identify a `pnputil /remove-device` call (step 2).</summary>
    private static bool IsPnpUtilRemove(ProcessRequest r)
    {
        return r.ExecutablePath == "pnputil.exe"
            && r.Arguments.Contains("/remove-device");
    }

    private static bool IsPnpScan(ProcessRequest r) =>
        r.ExecutablePath == "pnputil.exe" && r.Arguments.Contains("/scan-devices");

    private static bool IsPnpInstanceQuery(ProcessRequest r) =>
        r.ExecutablePath == "pnputil.exe" && r.Arguments.Contains("/enum-devices");

    /// <summary>Swap in fake Runner, pre-set NetAdapter module availability,
    /// run body, restore. Mirrors the WithFakeAsync pattern from
    /// <c>TunAdapterDiagnosticsNetAdapterAvailabilityTests</c>.</summary>
    private static async Task WithFakeAsync(
        FakeProcessRunner fake,
        bool moduleAvailable,
        Func<Task> body)
    {
        var previous = TunAdapterDiagnostics.Runner;
        var previousDelay = TunAdapterDiagnostics.RemovalDelayAsync;
        var previousRequirement = TunAdapterDiagnostics.RequiresNativePnpApi;
        var previousRemove = TunAdapterDiagnostics.RemoveNativePnpDevice;
        var previousQuery = TunAdapterDiagnostics.QueryNativePnpPresence;
        fake.OnRun(IsPnpScan, new ProcessResult(0, "", "", TimeSpan.Zero, false));
        fake.OnRun(IsPnpInstanceQuery, new ProcessResult(
            0, "No devices were found.\r\n", "", TimeSpan.Zero, false));
        TunAdapterDiagnostics.Runner = fake;
        TunAdapterDiagnostics.RemovalDelayAsync = static (_, _) => Task.CompletedTask;
        TunAdapterDiagnostics.RequiresNativePnpApi = static () => false;
        TunAdapterDiagnostics.RemoveNativePnpDevice =
            _ => new NativePnpRemovalResult(true, false, 0);
        TunAdapterDiagnostics.QueryNativePnpPresence =
            _ => new NativePnpPresenceResult(NativePnpPresence.Absent, 0x0D);
        TunAdapterDiagnostics.ResetRemoveNetAdapterLatchForTests();
        TunAdapterDiagnostics.SetNetAdapterModuleAvailableForTests(moduleAvailable);
        try { await body(); }
        finally
        {
            TunAdapterDiagnostics.Runner = previous;
            TunAdapterDiagnostics.RemovalDelayAsync = previousDelay;
            TunAdapterDiagnostics.RequiresNativePnpApi = previousRequirement;
            TunAdapterDiagnostics.RemoveNativePnpDevice = previousRemove;
            TunAdapterDiagnostics.QueryNativePnpPresence = previousQuery;
            TunAdapterDiagnostics.ResetRemoveNetAdapterLatchForTests();
        }
    }

    // ─── Test 1: module available + orphan found → PowerShell removal ───

    [Fact]
    public async Task PreStartCleanupAsync_OrphanFound_ModuleAvailable_RemoveNetAdapterFires()
    {
        // PreStartCleanupAsync is [SupportedOSPlatform("windows")] and
        // early-returns 0 on non-Windows. Linux CI must skip rather than
        // fail an "Assert.Equal(1, removed)" with the production guard's
        // baseline 0.
        Assert.SkipUnless(OperatingSystem.IsWindows(),
            "PreStartCleanupAsync is Windows-only (netsh + Remove-NetAdapter)");

        // Enumeration returns one VPNRouter-TUN orphan row. NetAdapter
        // module is forced available so the full path runs:
        // DisableOrphanedAdapter (netsh disable) + TryRemoveAdapterAsync
        // (PowerShell Remove-NetAdapter). Both fakes return success; the
        // count returned must be 1 (single orphan removed).
        var fake = new FakeProcessRunner();
        fake.OnRun(IsNetshEnumeration,
            new ProcessResult(
                ExitCode: 0,
                Stdout:
                """
                Admin State    State          Type             Interface Name
                -------------------------------------------------------------------------
                Enabled        Connected      Dedicated        Ethernet
                Disabled       Disconnected   Dedicated        VPNRouter-TUN
                """,
                Stderr: "",
                Duration: TimeSpan.FromMilliseconds(10),
                TimedOut: false));
        // netsh admin=disabled — used by DisableOrphanedAdapter during
        // the per-adapter cleanup loop (release kernel handle before
        // PowerShell removal).
        fake.OnRun(IsNetshDisable,
            new ProcessResult(0, "Ok.", "", TimeSpan.FromMilliseconds(5), false));
        // Step 1: Get-NetAdapter resolve returns the orphan's InstanceId.
        fake.OnRun(IsGetNetAdapterResolve,
            new ProcessResult(0, @"ROOT\NET\0001" + "\r\n", "", TimeSpan.FromMilliseconds(5), false));
        // Step 2: pnputil /remove-device exit 0 = "device record removed".
        fake.OnRun(IsPnpUtilRemove,
            new ProcessResult(0, "", "", TimeSpan.FromMilliseconds(5), false));

        int removed = 0;
        await WithFakeAsync(fake, moduleAvailable: true, async () =>
        {
            removed = await TunAdapterDiagnostics.PreStartCleanupAsync(
                logger: null, context: "test.happy.ps-remove");
        });

        // Single orphan handled → returned count is 1.
        Assert.Equal(1, removed);

        // The enumeration call fired exactly once at the top of the
        // orchestrator.
        var enumCalls = fake.RunCalls.Where(IsNetshEnumeration).ToList();
        Assert.Single(enumCalls);

        // netsh disable carried the VPNRouter-TUN name token (single argv
        // entry; .NET ArgumentList re-quotes for the kernel if needed).
        var disableCalls = fake.RunCalls.Where(IsNetshDisable).ToList();
        Assert.NotEmpty(disableCalls);
        Assert.Contains(disableCalls,
            c => c.Arguments.Contains("name=VPNRouter-TUN"));

        // Step 1: Get-NetAdapter resolve fired with the single-quoted
        // adapter-name embed (whitelist-safe, no apostrophe path).
        var resolveCalls = fake.RunCalls.Where(IsGetNetAdapterResolve).ToList();
        Assert.NotEmpty(resolveCalls);
        var psCall = resolveCalls[0];
        Assert.Equal(4, psCall.Arguments.Count);
        Assert.Equal("-NoProfile", psCall.Arguments[0]);
        Assert.Equal("-NonInteractive", psCall.Arguments[1]);
        Assert.Equal("-Command", psCall.Arguments[2]);
        Assert.Contains("Get-NetAdapter -Name", psCall.Arguments[3]);
        Assert.Contains("'VPNRouter-TUN'", psCall.Arguments[3]);
        Assert.Contains("PnPDeviceID", psCall.Arguments[3]);

        // Step 2: pnputil /remove-device carried the resolved InstanceId.
        var pnpCalls = fake.RunCalls.Where(IsPnpUtilRemove).ToList();
        Assert.NotEmpty(pnpCalls);
        Assert.Contains(@"ROOT\NET\0001", pnpCalls[0].Arguments);
    }

    // ─── Test 2: module unavailable + orphan found → CIM exact removal ──

    [Fact]
    public async Task PreStartCleanupAsync_OrphanFound_ModuleUnavailable_CimRemovalFires()
    {
        // WINBRAT-class path: the optional NetAdapter module is absent, but
        // Win32_NetworkAdapter CIM discovery is still available. The adapter
        // must be disabled, resolved and removed before sing-box can spawn.
        Assert.SkipUnless(OperatingSystem.IsWindows(),
            "PreStartCleanupAsync is Windows-only (netsh)");

        var fake = new FakeProcessRunner();
        fake.OnRun(IsNetshEnumeration,
            new ProcessResult(
                ExitCode: 0,
                Stdout:
                """
                Admin State    State          Type             Interface Name
                -------------------------------------------------------------------------
                Enabled        Connected      Dedicated        Ethernet
                Disabled       Disconnected   Dedicated        VPNRouter-TUN
                """,
                Stderr: "",
                Duration: TimeSpan.FromMilliseconds(10),
                TimedOut: false));
        // netsh releases the kernel handle before exact device removal.
        fake.OnRun(IsNetshDisable,
            new ProcessResult(0, "Ok.", "", TimeSpan.FromMilliseconds(5), false));
        fake.OnRun(IsCimResolve,
            new ProcessResult(0, @"ROOT\NET\0049" + "\r\n", "", TimeSpan.FromMilliseconds(5), false));
        fake.OnRun(IsPnpUtilRemove,
            new ProcessResult(0, "", "", TimeSpan.FromMilliseconds(5), false));

        int removed = 0;
        await WithFakeAsync(fake, moduleAvailable: false, async () =>
        {
            removed = await TunAdapterDiagnostics.PreStartCleanupAsync(
                logger: null, context: "test.happy.cim-removal");
        });

        // Single orphan removed through CIM + pnputil → handled count 1.
        Assert.Equal(1, removed);

        // The module-specific resolver is skipped, but exact removal is not.
        Assert.DoesNotContain(fake.RunCalls, IsGetNetAdapterResolve);
        Assert.Single(fake.RunCalls.Where(IsCimResolve));
        Assert.Single(fake.RunCalls.Where(IsPnpUtilRemove));

        // netsh admin=disabled fired for VPNRouter-TUN.
        var disableCalls = fake.RunCalls.Where(IsNetshDisable).ToList();
        Assert.NotEmpty(disableCalls);
        var primary = disableCalls.First(c =>
            c.Arguments.Contains("name=VPNRouter-TUN"));
        Assert.Equal("netsh", primary.ExecutablePath);
        Assert.Equal(new[]
        {
            "interface", "set", "interface",
            "name=VPNRouter-TUN",
            "admin=disabled",
        }, primary.Arguments);
    }

    // ─── Test 3: no orphans found → direct CIM confirms absence ─────────

    [Fact]
    public async Task PreStartCleanupAsync_NoOrphans_ModuleUnavailable_CimConfirmsAbsence()
    {
        // Enumeration finds no owned row. The defence-in-depth direct-name
        // pass still asks CIM, which returns no InstanceId and therefore
        // proves idempotent absence without invoking pnputil.
        Assert.SkipUnless(OperatingSystem.IsWindows(),
            "PreStartCleanupAsync is Windows-only (netsh)");

        var fake = new FakeProcessRunner();
        fake.OnRun(IsNetshEnumeration,
            new ProcessResult(
                ExitCode: 0,
                Stdout:
                """
                Admin State    State          Type             Interface Name
                -------------------------------------------------------------------------
                Enabled        Connected      Dedicated        Ethernet
                Enabled        Connected      Dedicated        Wi-Fi
                """,
                Stderr: "",
                Duration: TimeSpan.FromMilliseconds(10),
                TimedOut: false));
        fake.OnRun(IsNetshDisable,
            new ProcessResult(
                ExitCode: 1,
                Stdout: "",
                Stderr: "not found",
                Duration: TimeSpan.FromMilliseconds(5),
                TimedOut: false));
        fake.OnRun(IsCimResolve,
            new ProcessResult(0, "", "", TimeSpan.FromMilliseconds(5), false));

        int removed = 0;
        await WithFakeAsync(fake, moduleAvailable: false, async () =>
        {
            removed = await TunAdapterDiagnostics.PreStartCleanupAsync(
                logger: null, context: "test.happy.no-orphan");
        });

        // The direct pass completed and confirmed that no removal was needed.
        Assert.Equal(1, removed);

        // Enumeration call fired exactly once.
        var enumCalls = fake.RunCalls.Where(IsNetshEnumeration).ToList();
        Assert.Single(enumCalls);

        Assert.Single(fake.RunCalls.Where(IsCimResolve));
        Assert.DoesNotContain(fake.RunCalls, IsPnpUtilRemove);
    }
}
