using System;
using System.Linq;
using System.Threading.Tasks;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;
using Xunit;

namespace VPNRouter.Tests;

[Collection(SafeModeStateCollection.Name)]
public class TunAdapterDiagnosticsProcessRunnerWireShapeTests
{
    private static async Task WithFakeRunnerAsync(
        FakeProcessRunner fake,
        Func<Task> body)
    {
        var previous = TunAdapterDiagnostics.Runner;
        var previousDelay = TunAdapterDiagnostics.RemovalDelayAsync;
        var previousRequirement = TunAdapterDiagnostics.RequiresNativePnpApi;
        var previousRemove = TunAdapterDiagnostics.RemoveNativePnpDevice;
        var previousQuery = TunAdapterDiagnostics.QueryNativePnpPresence;
        var previousLookup = TunAdapterDiagnostics.ResolveNativePnpDeviceIds;
        TunAdapterDiagnostics.Runner = fake;
        TunAdapterDiagnostics.RemovalDelayAsync = static (_, _) => Task.CompletedTask;
        TunAdapterDiagnostics.RequiresNativePnpApi = static () => false;
        TunAdapterDiagnostics.RemoveNativePnpDevice =
            _ => new NativePnpRemovalResult(true, false, 0);
        TunAdapterDiagnostics.QueryNativePnpPresence =
            _ => new NativePnpPresenceResult(NativePnpPresence.Absent, 0x0D);
        TunAdapterDiagnostics.ResolveNativePnpDeviceIds =
            _ => new NativePnpLookupResult(true, Array.Empty<string>(), null);
        TunAdapterDiagnostics.ResetRemoveNetAdapterLatchForTests();
        try { await body(); }
        finally
        {
            TunAdapterDiagnostics.Runner = previous;
            TunAdapterDiagnostics.RemovalDelayAsync = previousDelay;
            TunAdapterDiagnostics.RequiresNativePnpApi = previousRequirement;
            TunAdapterDiagnostics.RemoveNativePnpDevice = previousRemove;
            TunAdapterDiagnostics.QueryNativePnpPresence = previousQuery;
            TunAdapterDiagnostics.ResolveNativePnpDeviceIds = previousLookup;
        }
    }

    private static void WithFakeRunner(FakeProcessRunner fake, Action body)
    {
        var previous = TunAdapterDiagnostics.Runner;
        var previousDelay = TunAdapterDiagnostics.RemovalDelayAsync;
        var previousRequirement = TunAdapterDiagnostics.RequiresNativePnpApi;
        var previousRemove = TunAdapterDiagnostics.RemoveNativePnpDevice;
        var previousQuery = TunAdapterDiagnostics.QueryNativePnpPresence;
        var previousLookup = TunAdapterDiagnostics.ResolveNativePnpDeviceIds;
        TunAdapterDiagnostics.Runner = fake;
        TunAdapterDiagnostics.RemovalDelayAsync = static (_, _) => Task.CompletedTask;
        TunAdapterDiagnostics.RequiresNativePnpApi = static () => false;
        TunAdapterDiagnostics.RemoveNativePnpDevice =
            _ => new NativePnpRemovalResult(true, false, 0);
        TunAdapterDiagnostics.QueryNativePnpPresence =
            _ => new NativePnpPresenceResult(NativePnpPresence.Absent, 0x0D);
        TunAdapterDiagnostics.ResolveNativePnpDeviceIds =
            _ => new NativePnpLookupResult(true, Array.Empty<string>(), null);
        TunAdapterDiagnostics.ResetRemoveNetAdapterLatchForTests();
        try { body(); }
        finally
        {
            TunAdapterDiagnostics.Runner = previous;
            TunAdapterDiagnostics.RemovalDelayAsync = previousDelay;
            TunAdapterDiagnostics.RequiresNativePnpApi = previousRequirement;
            TunAdapterDiagnostics.RemoveNativePnpDevice = previousRemove;
            TunAdapterDiagnostics.QueryNativePnpPresence = previousQuery;
            TunAdapterDiagnostics.ResolveNativePnpDeviceIds = previousLookup;
        }
    }

    [Fact]
    public void DisableOrphanedAdapter_EmitsNetshAdminDisabled()
    {
        if (!OperatingSystem.IsWindows()) return;

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
    public async Task PreStartCleanupAsync_NoAdapters_VerifiesDefaultNameThroughNativeLookup()
    {
        if (!OperatingSystem.IsWindows()) return;

        var fake = new FakeProcessRunner();
        fake.OnRun(r => r.ExecutablePath == "netsh" &&
                        r.Arguments.SequenceEqual(new[] { "interface", "show", "interface" }),
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
        fake.OnRun(r => r.ExecutablePath == "netsh" &&
                        r.Arguments.Contains("admin=disabled"),
            new ProcessResult(1, "not found", "", TimeSpan.FromMilliseconds(5), false));
        fake.OnRun(r => r.ExecutablePath == "powershell.exe" &&
                        r.Arguments.Count == 4 &&
                        r.Arguments[3].Contains("Get-Command Get-NetAdapter"),
            new ProcessResult(0, "0\r\n", "", TimeSpan.FromMilliseconds(5), false));
        await WithFakeRunnerAsync(fake, async () =>
        {
            var removed = await TunAdapterDiagnostics.PreStartCleanupAsync(
                logger: null, context: "test.no-adapters");

            Assert.Equal(1, removed);
        });

        Assert.NotEmpty(fake.RunCalls);
        var enumeration = fake.RunCalls[0];
        Assert.Equal("netsh", enumeration.ExecutablePath);
        Assert.Equal(new[] { "interface", "show", "interface" }, enumeration.Arguments);
        Assert.DoesNotContain(fake.RunCalls, c =>
            c.ExecutablePath == "powershell.exe" &&
            c.Arguments.Any(a => a.Contains("Get-CimInstance")));
        Assert.DoesNotContain(fake.RunCalls, c => c.ExecutablePath == "pnputil.exe");
    }

    [Fact]
    public async Task PreStartCleanupAsync_AdapterFound_DisableAndRemoveBoth()
    {
        if (!OperatingSystem.IsWindows()) return;

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
        fake.OnRun(r => r.ExecutablePath == "powershell.exe" &&
                       r.Arguments.Count == 4 &&
                       r.Arguments[3].Contains("Get-Command Get-NetAdapter"),
            new ProcessResult(0, "1\r\n", "", TimeSpan.FromMilliseconds(5), false));
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

        var disableCalls = fake.RunCalls.Where(c =>
            c.ExecutablePath == "netsh" &&
            c.Arguments.Contains("admin=disabled")).ToList();
        Assert.NotEmpty(disableCalls);
        Assert.Contains(disableCalls,
            c => c.Arguments.Contains("name=VPNRouter-TUN"));

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
        Assert.Contains("'VPNRouter-TUN'", psCall.Arguments[3]);

        var pnpCalls = fake.RunCalls.Where(c =>
            c.ExecutablePath == "pnputil.exe" &&
            c.Arguments.Contains("/remove-device")).ToList();
        Assert.NotEmpty(pnpCalls);
        Assert.Contains(@"ROOT\NET\0001", pnpCalls[0].Arguments);
    }
}
