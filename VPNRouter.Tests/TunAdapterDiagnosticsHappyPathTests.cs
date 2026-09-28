#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;
using Xunit;

namespace VPNRouter.Tests;

public sealed class TunAdapterDiagnosticsHappyPathTests
{
    private static bool IsNetshEnumeration(ProcessRequest r)
    {
        return r.ExecutablePath == "netsh"
            && r.Arguments.Count == 3
            && r.Arguments[0] == "interface"
            && r.Arguments[1] == "show"
            && r.Arguments[2] == "interface";
    }

    private static bool IsNetshDisable(ProcessRequest r)
    {
        return r.ExecutablePath == "netsh"
            && r.Arguments.Contains("admin=disabled");
    }

    private static bool IsGetNetAdapterResolve(ProcessRequest r)
    {
        return r.ExecutablePath == "powershell.exe"
            && r.Arguments.Count == 4
            && r.Arguments[3].Contains("Get-NetAdapter -Name")
            && r.Arguments[3].Contains("PnPDeviceID");
    }

    private static bool IsPnpUtilRemove(ProcessRequest r)
    {
        return r.ExecutablePath == "pnputil.exe"
            && r.Arguments.Contains("/remove-device");
    }

    private static bool IsPnpScan(ProcessRequest r) =>
        r.ExecutablePath == "pnputil.exe" && r.Arguments.Contains("/scan-devices");

    private static bool IsPnpInstanceQuery(ProcessRequest r) =>
        r.ExecutablePath == "pnputil.exe" && r.Arguments.Contains("/enum-devices");

    private static async Task WithFakeAsync(
        FakeProcessRunner fake,
        bool moduleAvailable,
        Func<Task> body,
        Func<string, NativePnpLookupResult>? nativeLookup = null)
    {
        var previous = TunAdapterDiagnostics.Runner;
        var previousDelay = TunAdapterDiagnostics.RemovalDelayAsync;
        var previousRequirement = TunAdapterDiagnostics.RequiresNativePnpApi;
        var previousRemove = TunAdapterDiagnostics.RemoveNativePnpDevice;
        var previousQuery = TunAdapterDiagnostics.QueryNativePnpPresence;
        var previousLookup = TunAdapterDiagnostics.ResolveNativePnpDeviceIds;
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
        TunAdapterDiagnostics.ResolveNativePnpDeviceIds = nativeLookup ??
            (_ => new NativePnpLookupResult(true, Array.Empty<string>(), null));
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
            TunAdapterDiagnostics.ResolveNativePnpDeviceIds = previousLookup;
            TunAdapterDiagnostics.ResetRemoveNetAdapterLatchForTests();
        }
    }

    [Fact]
    public async Task PreStartCleanupAsync_OrphanFound_ModuleAvailable_RemoveNetAdapterFires()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(),
            "PreStartCleanupAsync is Windows-only (netsh + Remove-NetAdapter)");

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
        fake.OnRun(IsNetshDisable,
            new ProcessResult(0, "Ok.", "", TimeSpan.FromMilliseconds(5), false));
        fake.OnRun(IsGetNetAdapterResolve,
            new ProcessResult(0, @"ROOT\NET\0001" + "\r\n", "", TimeSpan.FromMilliseconds(5), false));
        fake.OnRun(IsPnpUtilRemove,
            new ProcessResult(0, "", "", TimeSpan.FromMilliseconds(5), false));

        int removed = 0;
        await WithFakeAsync(fake, moduleAvailable: true, async () =>
        {
            removed = await TunAdapterDiagnostics.PreStartCleanupAsync(
                logger: null, context: "test.happy.ps-remove");
        });

        Assert.Equal(1, removed);

        var enumCalls = fake.RunCalls.Where(IsNetshEnumeration).ToList();
        Assert.Single(enumCalls);

        var disableCalls = fake.RunCalls.Where(IsNetshDisable).ToList();
        Assert.NotEmpty(disableCalls);
        Assert.Contains(disableCalls,
            c => c.Arguments.Contains("name=VPNRouter-TUN"));

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

        var pnpCalls = fake.RunCalls.Where(IsPnpUtilRemove).ToList();
        Assert.NotEmpty(pnpCalls);
        Assert.Contains(@"ROOT\NET\0001", pnpCalls[0].Arguments);
    }

    [Fact]
    public async Task PreStartCleanupAsync_OrphanFound_ModuleUnavailable_NativeRemovalFires()
    {
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
        fake.OnRun(IsNetshDisable,
            new ProcessResult(0, "Ok.", "", TimeSpan.FromMilliseconds(5), false));
        fake.OnRun(IsPnpUtilRemove,
            new ProcessResult(0, "", "", TimeSpan.FromMilliseconds(5), false));

        int removed = 0;
        await WithFakeAsync(fake, moduleAvailable: false, async () =>
        {
            removed = await TunAdapterDiagnostics.PreStartCleanupAsync(
                logger: null, context: "test.happy.native-removal");
        }, nativeLookup: _ =>
            new NativePnpLookupResult(true, new[] { @"ROOT\NET\0049" }, null));

        Assert.Equal(1, removed);

        Assert.DoesNotContain(fake.RunCalls, IsGetNetAdapterResolve);
        Assert.DoesNotContain(fake.RunCalls,
            c => c.ExecutablePath == "powershell.exe" &&
                 c.Arguments.Any(a => a.Contains("Get-CimInstance")));
        Assert.Single(fake.RunCalls.Where(IsPnpUtilRemove));

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

    [Fact]
    public async Task PreStartCleanupAsync_NoOrphans_ModuleUnavailable_NativeLookupConfirmsAbsence()
    {
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
        int removed = 0;
        await WithFakeAsync(fake, moduleAvailable: false, async () =>
        {
            removed = await TunAdapterDiagnostics.PreStartCleanupAsync(
                logger: null, context: "test.happy.no-orphan");
        });

        Assert.Equal(1, removed);

        var enumCalls = fake.RunCalls.Where(IsNetshEnumeration).ToList();
        Assert.Single(enumCalls);

        Assert.DoesNotContain(fake.RunCalls,
            c => c.ExecutablePath == "powershell.exe" &&
                 c.Arguments.Any(a => a.Contains("Get-CimInstance")));
        Assert.DoesNotContain(fake.RunCalls, IsPnpUtilRemove);
    }

    [Fact]
    public async Task PreStartCleanupAsync_NumberedHistoricalRow_IsNeverRemoved()
    {
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
                Disabled       Disconnected   Dedicated        VPNRouter-TUN 46
                """,
                Stderr: "",
                Duration: TimeSpan.FromMilliseconds(10),
                TimedOut: false));

        var lookedUpNames = new List<string>();
        await WithFakeAsync(fake, moduleAvailable: false, async () =>
        {
            _ = await TunAdapterDiagnostics.PreStartCleanupAsync(
                logger: null, context: "test.numbered-history");
        }, nativeLookup: name =>
        {
            lookedUpNames.Add(name);
            return new NativePnpLookupResult(true, Array.Empty<string>(), null);
        });

        Assert.Equal(new[] { "VPNRouter-TUN" }, lookedUpNames);
        Assert.DoesNotContain(fake.RunCalls, IsNetshDisable);
        Assert.DoesNotContain(fake.RunCalls, IsPnpUtilRemove);
    }
}
