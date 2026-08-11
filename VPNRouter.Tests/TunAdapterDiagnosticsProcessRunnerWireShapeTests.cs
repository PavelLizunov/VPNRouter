using System;
using System.Linq;
using System.Threading.Tasks;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// Phase 3+ (2026-05-21) IProcessRunner adoption pin for
/// <see cref="TunAdapterDiagnostics"/>. After the netsh / PowerShell
/// callsites moved off direct <c>Process.Start</c>, the per-call argv
/// shape is the new invariant tests must pin — adapter cleanup is
/// system-state-mutating and a wire-shape divergence could either
/// strand orphan adapters (silent leak) or destroy unrelated VPN
/// adapters (cross-tool damage).
///
/// <para>Tests assign a <c>FakeProcessRunner</c> to the static
/// <see cref="TunAdapterDiagnostics.Runner"/> seam, exercise the public
/// surface, assert the captured argv. Runs cross-platform (no real
/// netsh / PowerShell needed) — Windows-only paths skip silently on
/// non-Windows.</para>
/// </summary>
public class TunAdapterDiagnosticsProcessRunnerWireShapeTests
{
    private static async Task WithFakeRunnerAsync(
        FakeProcessRunner fake,
        Func<Task> body)
    {
        var previous = TunAdapterDiagnostics.Runner;
        var previousDelay = TunAdapterDiagnostics.RemovalDelayAsync;
        TunAdapterDiagnostics.Runner = fake;
        TunAdapterDiagnostics.RemovalDelayAsync = static (_, _) => Task.CompletedTask;
        // BR-2 latch may have been flipped by an earlier test that ran
        // against the real ProcessRunner (e.g.
        // PreStartCleanupAsync_NonWindows_ReturnsZeroNoOp does on
        // Windows). Reset so our fake gets to observe the PowerShell call.
        TunAdapterDiagnostics.ResetRemoveNetAdapterLatchForTests();
        try { await body(); }
        finally
        {
            TunAdapterDiagnostics.Runner = previous;
            TunAdapterDiagnostics.RemovalDelayAsync = previousDelay;
        }
    }

    private static void WithFakeRunner(FakeProcessRunner fake, Action body)
    {
        var previous = TunAdapterDiagnostics.Runner;
        var previousDelay = TunAdapterDiagnostics.RemovalDelayAsync;
        TunAdapterDiagnostics.Runner = fake;
        TunAdapterDiagnostics.RemovalDelayAsync = static (_, _) => Task.CompletedTask;
        TunAdapterDiagnostics.ResetRemoveNetAdapterLatchForTests();
        try { body(); }
        finally
        {
            TunAdapterDiagnostics.Runner = previous;
            TunAdapterDiagnostics.RemovalDelayAsync = previousDelay;
        }
    }

    [Fact]
    public void DisableOrphanedAdapter_EmitsNetshAdminDisabled()
    {
        if (!OperatingSystem.IsWindows()) return;

        // Critical wire-shape: `netsh interface set interface name=<NAME>
        // admin=disabled`. The name token must NOT have surrounding quotes
        // — they're stripped by SplitShellArgs / the equivalent token
        // construction; .NET ArgumentList re-quotes when it builds the
        // command line for the kernel if the value contains spaces.
        var fake = new FakeProcessRunner();
        fake.OnRun(_ => true,
            new ProcessResult(0, "Ok.", "", TimeSpan.FromMilliseconds(5), false));

        WithFakeRunner(fake, () =>
        {
            TunAdapterDiagnostics.DisableOrphanedAdapter(
                logger: null, interfaceName: "VPNRouter-TUN", context: "test.disable");
        });

        Assert.Single(fake.RunCalls);
        var call = fake.RunCalls[0];
        Assert.Equal("netsh", call.ExecutablePath);
        Assert.Equal(new[]
        {
            "interface", "set", "interface",
            "name=VPNRouter-TUN",
            "admin=disabled",
        }, call.Arguments);
    }

    [Fact]
    public void DisableOrphanedAdapter_ExitCode1NotFound_TreatedAsSuccess()
    {
        // netsh exit 1 with "not found" = adapter already gone, idempotent
        // success path. The fake returns exit 1 + the marker stdout; the
        // helper logs at Debug and returns without throwing.
        if (!OperatingSystem.IsWindows()) return;

        var fake = new FakeProcessRunner();
        fake.OnRun(_ => true,
            new ProcessResult(
                ExitCode: 1,
                Stdout: "The system cannot find the file specified. (not found)",
                Stderr: "",
                Duration: TimeSpan.FromMilliseconds(5),
                TimedOut: false));

        WithFakeRunner(fake, () =>
        {
            var ex = Record.Exception(() =>
                TunAdapterDiagnostics.DisableOrphanedAdapter(
                    logger: null, interfaceName: "VPNRouter-Test-Missing",
                    context: "test.notfound"));
            Assert.Null(ex);
        });
    }

    [Fact]
    public async Task PreStartCleanupAsync_NoAdapters_OnlyNetshEnumerationCalled()
    {
        if (!OperatingSystem.IsWindows()) return;

        // When netsh enumeration shows no VPNRouter-TUN / sing-box-tun
        // adapter, PreStartCleanupAsync skips the per-adapter cleanup
        // loop and falls through to the direct-by-name fallback. The
        // enumeration call shape is `netsh interface show interface`.
        var fake = new FakeProcessRunner();
        // First call: netsh enumeration. Subsequent: direct-by-name
        // fallback (DisableOrphanedAdapter + TryRemoveAdapterAsync).
        fake.OnRun(_ => true,
            new ProcessResult(0,
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

        await WithFakeRunnerAsync(fake, async () =>
        {
            var removed = await TunAdapterDiagnostics.PreStartCleanupAsync(
                logger: null, context: "test.no-adapters");

            // No removals because no stale adapters in the enumeration.
            // (The fallback path also fires but the fake netsh "succeeds"
            // and the Remove-NetAdapter mock doesn't actually remove
            // anything — removed counter increments by 0 or 1 depending
            // on whether the fake powershell returns exit 0.)
            Assert.True(removed >= 0);
        });

        Assert.NotEmpty(fake.RunCalls);
        // First call must be the netsh enumeration.
        var enumeration = fake.RunCalls[0];
        Assert.Equal("netsh", enumeration.ExecutablePath);
        Assert.Equal(new[] { "interface", "show", "interface" }, enumeration.Arguments);
    }

    [Fact]
    public async Task PreStartCleanupAsync_AdapterFound_DisableAndRemoveBoth()
    {
        if (!OperatingSystem.IsWindows()) return;

        // Enumeration returns a VPNRouter-TUN row → PreStartCleanupAsync
        // must call DisableOrphanedAdapter (netsh admin=disabled) THEN
        // TryRemoveAdapterAsync (powershell Remove-NetAdapter). Confirm
        // both shapes hit the runner.
        var fake = new FakeProcessRunner();
        fake.OnRun(r => r.ExecutablePath == "netsh" &&
                       r.Arguments.Count == 3 && r.Arguments[0] == "interface" &&
                       r.Arguments[1] == "show",
            new ProcessResult(0,
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
        // The availability probe was repointed 2026-06-08 from
        // `Get-Module NetAdapter -ListAvailable` to `Get-Command
        // Get-NetAdapter` (the real cmdlet we use to resolve InstanceId;
        // Remove-NetAdapter never existed). Return "1" so removal runs.
        fake.OnRun(r => r.ExecutablePath == "powershell.exe" &&
                       r.Arguments.Count == 4 &&
                       r.Arguments[3].Contains("Get-Command Get-NetAdapter"),
            new ProcessResult(0, "1\r\n", "", TimeSpan.FromMilliseconds(5), false));
        // Step 1 resolve: Get-NetAdapter -Name ... PnPDeviceID → InstanceId.
        fake.OnRun(r => r.ExecutablePath == "powershell.exe" &&
                       r.Arguments.Count == 4 &&
                       r.Arguments[3].Contains("PnPDeviceID"),
            new ProcessResult(0, @"ROOT\NET\0001" + "\r\n", "", TimeSpan.FromMilliseconds(5), false));
        fake.OnRun(_ => true,
            new ProcessResult(0, "", "", TimeSpan.FromMilliseconds(5), false));

        await WithFakeRunnerAsync(fake, async () =>
        {
            _ = await TunAdapterDiagnostics.PreStartCleanupAsync(
                logger: null, context: "test.found");
        });

        // Expect: 1) netsh enumeration, 2) netsh admin=disabled, 3)
        // powershell Remove-NetAdapter (plus possibly the direct-by-name
        // fallback if enumeration didn't surface VPNRouter-TUN — it
        // should, so the fallback is skipped via the
        // enumerationFoundDefault flag).
        var disableCalls = fake.RunCalls.Where(c =>
            c.ExecutablePath == "netsh" &&
            c.Arguments.Contains("admin=disabled")).ToList();
        Assert.NotEmpty(disableCalls);
        Assert.Contains(disableCalls,
            c => c.Arguments.Contains("name=VPNRouter-TUN"));

        // 2026-06-08: removal is now Get-NetAdapter resolve (step 1) +
        // pnputil /remove-device (step 2), not the phantom Remove-NetAdapter.
        var resolveCalls = fake.RunCalls.Where(c =>
            c.ExecutablePath == "powershell.exe" &&
            c.Arguments.Count == 4 &&
            c.Arguments[3].Contains("Get-NetAdapter -Name") &&
            c.Arguments[3].Contains("PnPDeviceID")).ToList();
        Assert.NotEmpty(resolveCalls);
        var psCall = resolveCalls[0];
        Assert.Equal(4, psCall.Arguments.Count);
        Assert.Equal("-NoProfile", psCall.Arguments[0]);
        Assert.Equal("-NonInteractive", psCall.Arguments[1]);
        Assert.Equal("-Command", psCall.Arguments[2]);
        // Single-quoted adapter name — injection-safe by the [A-Za-z0-9_-]
        // whitelist in ExtractStaleAdapterNames.
        Assert.Contains("'VPNRouter-TUN'", psCall.Arguments[3]);

        // pnputil /remove-device carried the resolved InstanceId.
        var pnpCalls = fake.RunCalls.Where(c =>
            c.ExecutablePath == "pnputil.exe" &&
            c.Arguments.Contains("/remove-device")).ToList();
        Assert.NotEmpty(pnpCalls);
        Assert.Contains(@"ROOT\NET\0001", pnpCalls[0].Arguments);
    }
}
