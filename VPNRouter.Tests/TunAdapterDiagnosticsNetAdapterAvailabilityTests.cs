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
/// built-in <c>pnputil /remove-device</c> with a SetupAPI fallback. The
/// availability probe is repointed from <c>Get-Module NetAdapter</c> to
/// <c>Get-Command Get-NetAdapter</c>. Verified on the dev VM (read-only):
/// Get-NetAdapter exposes PnPDeviceID and pnputil targets the same InstanceId.</para>
///
/// <para><b>2026-08-12 WINBRAT follow-up:</b> NetAdapter is optional on the
/// Windows LTSC test image. Module absence used to bypass pnputil entirely and
/// let sing-box crash with ERROR_FILE_EXISTS. The fallback now resolves the
/// same PNPDeviceID through Windows Network Connections and remains fail-closed.</para>
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
        FakeProcessRunner fake, bool removalAvailable, Func<Task> body,
        bool useNativePnp = false,
        Func<string, NativePnpRemovalResult>? nativeRemove = null,
        Func<string, NativePnpPresenceResult>? nativeQuery = null,
        Func<string, NativePnpLookupResult>? nativeLookup = null,
        Func<bool>? moduleProbe = null)
    {
        var previous = TunAdapterDiagnostics.Runner;
        var previousDelay = TunAdapterDiagnostics.RemovalDelayAsync;
        var previousRequirement = TunAdapterDiagnostics.RequiresNativePnpApi;
        var previousRemove = TunAdapterDiagnostics.RemoveNativePnpDevice;
        var previousQuery = TunAdapterDiagnostics.QueryNativePnpPresence;
        var previousLookup = TunAdapterDiagnostics.ResolveNativePnpDeviceIds;
        fake.OnRun(IsNetshDisable, Ok());
        fake.OnRun(IsPnpUtilScan, Ok());
        fake.OnRun(IsPnpUtilInstanceQuery, Ok("No devices were found.\r\n"));
        TunAdapterDiagnostics.Runner = fake;
        TunAdapterDiagnostics.RemovalDelayAsync = static (_, _) => Task.CompletedTask;
        TunAdapterDiagnostics.RequiresNativePnpApi = () => useNativePnp;
        TunAdapterDiagnostics.RemoveNativePnpDevice = nativeRemove ??
            (_ => new NativePnpRemovalResult(true, false, 0));
        TunAdapterDiagnostics.QueryNativePnpPresence = nativeQuery ??
            (_ => new NativePnpPresenceResult(NativePnpPresence.Absent, 0x0D));
        TunAdapterDiagnostics.ResolveNativePnpDeviceIds = nativeLookup ??
            (_ => new NativePnpLookupResult(true, Array.Empty<string>(), null));
        TunAdapterDiagnostics.ResetRemoveNetAdapterLatchForTests();
        if (moduleProbe != null)
            TunAdapterDiagnostics.SetNetAdapterModuleProbeForTests(moduleProbe);
        else
            TunAdapterDiagnostics.SetNetAdapterModuleAvailableForTests(removalAvailable);
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

    private static async Task WithFakeNoPresetAsync(FakeProcessRunner fake, Func<Task> body)
    {
        var previous = TunAdapterDiagnostics.Runner;
        var previousDelay = TunAdapterDiagnostics.RemovalDelayAsync;
        var previousRequirement = TunAdapterDiagnostics.RequiresNativePnpApi;
        var previousRemove = TunAdapterDiagnostics.RemoveNativePnpDevice;
        var previousQuery = TunAdapterDiagnostics.QueryNativePnpPresence;
        var previousLookup = TunAdapterDiagnostics.ResolveNativePnpDeviceIds;
        fake.OnRun(IsNetshDisable, Ok());
        fake.OnRun(IsPnpUtilScan, Ok());
        fake.OnRun(IsPnpUtilInstanceQuery, Ok("No devices were found.\r\n"));
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
            TunAdapterDiagnostics.ResetRemoveNetAdapterLatchForTests();
        }
    }

    private static async Task WithNativePnpAsync(
        FakeProcessRunner fake,
        bool netAdapterAvailable,
        Func<string, NativePnpRemovalResult> remove,
        Func<string, NativePnpPresenceResult> query,
        Func<Task> body,
        Func<string, NativePnpLookupResult>? lookup = null)
    {
        await WithFakeAsync(
            fake, netAdapterAvailable, body, useNativePnp: true,
            nativeRemove: remove, nativeQuery: query, nativeLookup: lookup);
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

    // ─── Test 3: pnputil plain refused → SetupAPI fallback ────────────────

    [Fact]
    public async Task Available_NoEnumeratedAdapter_ExitOne_AllowsPreStartAfterNativeConfirmation()
    {
        if (!OperatingSystem.IsWindows()) return;

        var fake = new FakeProcessRunner();
        fake.OnRun(IsNetshEnumeration, Ok(
            "Enabled  Connected  Dedicated  Ethernet\r\n"));
        fake.OnRun(IsGetNetAdapterResolve,
            new ProcessResult(1, "", "", TimeSpan.FromMilliseconds(5), false));
        var lookedUpNames = new List<string>();

        await WithFakeAsync(fake, removalAvailable: true, async () =>
        {
            var removed = await TunAdapterDiagnostics.PreStartCleanupAsync(
                logger: null, context: "test.gone-exit-one");
            Assert.Equal(1, removed);
        }, nativeLookup: name =>
        {
            lookedUpNames.Add(name);
            return new NativePnpLookupResult(true, Array.Empty<string>(), null);
        });

        Assert.Equal(new[] { "VPNRouter-TUN" }, lookedUpNames);
        Assert.DoesNotContain(fake.RunCalls, c => c.ExecutablePath == "pnputil.exe");
    }

    [Fact]
    public async Task Available_PnpUtilPlainRefused_RetriesWithSetupApi()
    {
        if (!OperatingSystem.IsWindows()) return;

        var fake = new FakeProcessRunner();
        fake.OnRun(IsGetNetAdapterResolve, Ok(@"ROOT\NET\0002" + "\r\n"));
        fake.OnRun(IsPnpUtilRemovePlain,
            new ProcessResult(1, "", "remove refused", TimeSpan.FromMilliseconds(5), false));
        var nativeRemovals = new List<string>();

        await WithFakeAsync(fake, removalAvailable: true, async () =>
        {
            var ok = await TunAdapterDiagnostics.TryRemoveAdapterAsync(
                logger: null, adapterName: "VPNRouter-TUN", context: "test.force");
            Assert.True(ok);
        }, nativeRemove: id =>
        {
            nativeRemovals.Add(id);
            return new NativePnpRemovalResult(true, false, 0);
        });

        Assert.Single(fake.RunCalls.Where(IsPnpUtilRemovePlain));
        Assert.DoesNotContain(fake.RunCalls, IsPnpUtilRemoveForce);
        Assert.Equal(new[] { @"ROOT\NET\0002" }, nativeRemovals);
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

    // ─── Test 5: NetAdapter unavailable → Network Connections exact removal ───

    [Fact]
    public async Task NetAdapterUnavailable_PreStartCleanup_UsesNativeLookupAndPnpRemoval()
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
        fake.OnRun(IsPnpUtilRemovePlain, Ok());
        var lookedUpNames = new List<string>();

        await WithFakeAsync(fake, removalAvailable: false, async () =>
        {
            _ = await TunAdapterDiagnostics.PreStartCleanupAsync(logger: null, context: "test.fallback");
        }, nativeLookup: name =>
        {
            lookedUpNames.Add(name);
            return new NativePnpLookupResult(true, new[] { @"ROOT\NET\0049" }, null);
        });

        Assert.Contains(fake.RunCalls.Where(IsNetshDisable),
            c => c.Arguments.Contains("name=VPNRouter-TUN"));
        Assert.Equal(new[] { "VPNRouter-TUN" }, lookedUpNames);
        Assert.Single(fake.RunCalls.Where(IsPnpUtilRemovePlain));
        Assert.DoesNotContain(fake.RunCalls, IsGetNetAdapterResolve);
        Assert.DoesNotContain(fake.RunCalls,
            c => c.ExecutablePath == "powershell.exe" &&
                 c.Arguments.Any(a => a.Contains("Get-CimInstance")));
    }

    // ─── Test 6: native fallback is announced once ──────────────────────

    [Fact]
    public async Task NetAdapterUnavailable_FirstCall_LogsNativeFallbackOnce()
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
            .Where(s => s.Contains("through Windows Network Connections"))
            .ToList();
        Assert.Single(infEvents);
        Assert.DoesNotContain(fake.RunCalls,
            c => c.ExecutablePath == "powershell.exe" &&
                 c.Arguments.Any(a => a.Contains("Get-CimInstance")));
    }

    [Fact]
    public async Task NativeLookupFailure_PreStartCleanup_FailsClosed()
    {
        if (!OperatingSystem.IsWindows()) return;

        var fake = new FakeProcessRunner();
        fake.OnRun(IsNetshEnumeration, Ok(
            "Disabled  Disconnected  Dedicated  VPNRouter-TUN\r\n"));
        fake.OnRun(IsNetshDisable, Ok());
        await WithFakeAsync(fake, removalAvailable: false, async () =>
        {
            await Assert.ThrowsAsync<TunAdapterNotReadyException>(() =>
                TunAdapterDiagnostics.PreStartCleanupAsync(null, "test.native-lookup-fail"));
        }, nativeLookup: _ =>
            new NativePnpLookupResult(false, Array.Empty<string>(), "registry query failed"));

        Assert.DoesNotContain(fake.RunCalls, c => c.ExecutablePath == "pnputil.exe");
    }

    [Fact]
    public async Task NetAdapterUnavailable_NativePnpPath_RemovesExactResolvedDevice()
    {
        if (!OperatingSystem.IsWindows()) return;

        const string instanceId = @"ROOT\NET\LTSC0049";
        var fake = new FakeProcessRunner();
        var removedIds = new List<string>();
        var queriedIds = new List<string>();

        await WithNativePnpAsync(
            fake,
            netAdapterAvailable: false,
            id =>
            {
                removedIds.Add(id);
                return new NativePnpRemovalResult(true, false, 0);
            },
            id =>
            {
                queriedIds.Add(id);
                return new NativePnpPresenceResult(NativePnpPresence.Absent, 0x0D);
            },
            async () => Assert.True(await TunAdapterDiagnostics.TryRemoveAdapterAsync(
                null, "VPNRouter-TUN", "test.ltsc-native")),
            lookup: _ => new NativePnpLookupResult(true, new[] { instanceId }, null));

        Assert.Equal(new[] { instanceId }, removedIds);
        Assert.Equal(4, queriedIds.Count);
        Assert.All(queriedIds, id => Assert.Equal(instanceId, id));
        Assert.DoesNotContain(fake.RunCalls, c => c.ExecutablePath == "pnputil.exe");
    }

    [Fact]
    public async Task LtscNativeLookup_BypassesPowerShellProbeAndPnpUtil()
    {
        if (!OperatingSystem.IsWindows()) return;

        const string instanceId = @"ROOT\NET\LTSC_BYPASS";
        var fake = NewRunner();
        var removedIds = new List<string>();

        await WithFakeAsync(
            fake,
            removalAvailable: true,
            async () => Assert.True(await TunAdapterDiagnostics.TryRemoveAdapterAsync(
                null, "VPNRouter-TUN", "test.ltsc-no-powershell")),
            useNativePnp: true,
            nativeRemove: id =>
            {
                removedIds.Add(id);
                return new NativePnpRemovalResult(true, false, 0);
            },
            nativeQuery: _ =>
                new NativePnpPresenceResult(NativePnpPresence.Absent, 0x0D),
            nativeLookup: _ =>
                new NativePnpLookupResult(true, new[] { instanceId }, null),
            moduleProbe: () => throw new InvalidOperationException(
                "LTSC must not evaluate the PowerShell module probe."));

        Assert.Equal(new[] { instanceId }, removedIds);
        Assert.DoesNotContain(fake.RunCalls, c => c.ExecutablePath == "powershell.exe");
        Assert.DoesNotContain(fake.RunCalls, c => c.ExecutablePath == "pnputil.exe");
    }

    [Fact]
    public async Task NativePnpPath_MultipleIds_AnyRemovalFailureFailsClosed()
    {
        if (!OperatingSystem.IsWindows()) return;

        const string firstId = @"ROOT\NET\LTSC0050";
        const string secondId = @"ROOT\NET\LTSC0051";
        var fake = new FakeProcessRunner();
        var removedIds = new List<string>();

        await WithNativePnpAsync(
            fake,
            netAdapterAvailable: false,
            id =>
            {
                removedIds.Add(id);
                return id == firstId
                    ? new NativePnpRemovalResult(true, false, 0)
                    : new NativePnpRemovalResult(false, false, 5);
            },
            _ => new NativePnpPresenceResult(NativePnpPresence.Absent, 0x0D),
            async () =>
            {
                var ex = await Assert.ThrowsAsync<TunAdapterNotReadyException>(() =>
                    TunAdapterDiagnostics.TryRemoveAdapterAsync(
                        null, "VPNRouter-TUN", "test.ltsc-multiple"));
                Assert.Equal(secondId, ex.InstanceId);
            },
            lookup: _ => new NativePnpLookupResult(
                true, new[] { firstId, secondId }, null));

        Assert.Equal(new[] { firstId, secondId }, removedIds);
    }

    [Fact]
    public async Task ObservedAdapter_NativeLookupReturnsNoId_FailsBeforeDisable()
    {
        if (!OperatingSystem.IsWindows()) return;

        var fake = new FakeProcessRunner();
        fake.OnRun(IsNetshEnumeration,
            Ok("Disabled  Disconnected  Dedicated  VPNRouter-TUN\r\n"));
        await WithFakeAsync(fake, removalAvailable: false, async () =>
            await Assert.ThrowsAsync<TunAdapterNotReadyException>(() =>
                TunAdapterDiagnostics.PreStartCleanupAsync(null, "test.observed-no-id")));

        Assert.DoesNotContain(fake.RunCalls, IsNetshDisable);
        Assert.DoesNotContain(fake.RunCalls, c => c.ExecutablePath == "pnputil.exe");
    }

    [Fact]
    public async Task NativeFallback_ReceivesExactOwnedNameAndResolvedId()
    {
        if (!OperatingSystem.IsWindows()) return;

        const string adapterName = "sing-box-tun-49";
        const string instanceId = @"ROOT\NET\FALLBACK0049";
        var fake = new FakeProcessRunner();
        var removedIds = new List<string>();
        var lookedUpNames = new List<string>();

        await WithNativePnpAsync(
            fake,
            netAdapterAvailable: false,
            id =>
            {
                removedIds.Add(id);
                return new NativePnpRemovalResult(true, false, 0);
            },
            _ => new NativePnpPresenceResult(NativePnpPresence.Absent, 0x0D),
            async () => Assert.True(await TunAdapterDiagnostics.TryRemoveAdapterAsync(
                null, adapterName, "test.fallback-name", requireInstanceId: true)),
            lookup: name =>
            {
                lookedUpNames.Add(name);
                return new NativePnpLookupResult(true, new[] { instanceId }, null);
            });

        Assert.Equal(new[] { adapterName }, lookedUpNames);
        Assert.Equal(new[] { instanceId }, removedIds);
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
