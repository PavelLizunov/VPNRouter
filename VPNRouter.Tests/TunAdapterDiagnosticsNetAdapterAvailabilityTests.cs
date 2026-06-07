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
/// PinkuDani Fix #1 + #4 (2026-05-21) regression suite.
///
/// <para><b>Trigger:</b> PinkuDani's log
/// (<c>Z:\PinkuDani\vpnrouter20260521_002.log</c>) showed a Windows 10
/// LTSC install missing the PowerShell <c>NetAdapter</c> module. Every
/// <see cref="TunAdapterDiagnostics.TryRemoveAdapterAsync"/> call burned
/// ~1.5-2 s before failing with CommandNotFoundException, 5 times per
/// connect cycle — total cumulative delay caused the VM 30 s timeout
/// to fire and force-stop a connecting VPN.</para>
///
/// <para>BR-2's reactive latch (Wave 39 follow-up, brat 2026-05-19)
/// failed to catch this on Russian Windows because CP-866 OEM stderr
/// mangled "не распознано" into "­Ґ а бЇ®§­ ­®" — the literal-string
/// substring search never matched, latch stayed at 0, every callsite
/// kept spawning PowerShell.</para>
///
/// <para><b>Fix:</b> proactive Lazy probe via <c>Get-Module NetAdapter
/// -ListAvailable | Measure-Object | Select -ExpandProperty Count</c>.
/// Locale-independent (parses an integer count, not an error string).
/// Latched for the process lifetime. When unavailable, every
/// <see cref="TunAdapterDiagnostics.TryRemoveAdapterAsync"/> returns
/// false immediately and <see cref="TunAdapterDiagnostics.PreStartCleanupAsync"/>
/// falls back to <see cref="TunAdapterDiagnostics.TryDisableAdapterViaNetshAsync"/>
/// (Fix #4) so the wintun kernel handle is still released.</para>
///
/// <para>Tests assign a <see cref="FakeProcessRunner"/> to the static
/// <see cref="TunAdapterDiagnostics.Runner"/> seam, exercise the
/// availability + cache + fallback paths, assert observed behaviour.
/// Runs cross-platform — Windows-only helpers silently skip on
/// non-Windows so the test class itself stays portable.</para>
/// </summary>
public sealed class TunAdapterDiagnosticsNetAdapterAvailabilityTests
{
    // ─── helpers ────────────────────────────────────────────────────────

    /// <summary>Convenience: build a FakeProcessRunner with reasonable
    /// defaults (any unmocked netsh = empty success, any unmocked PS =
    /// empty success). Tests override specific predicates above this.</summary>
    private static FakeProcessRunner NewRunner()
    {
        var fake = new FakeProcessRunner();
        // Default: anything not specifically matched gets empty success.
        fake.OnRun(_ => true,
            new ProcessResult(0, "", "", TimeSpan.FromMilliseconds(5), false));
        return fake;
    }

    /// <summary>Set up a fresh test environment: swap Runner, clear BR-2
    /// latch, clear Lazy. <paramref name="moduleAvailable"/> dictates the
    /// cached availability outcome (skips the real probe).</summary>
    private static async Task WithFakeAsync(
        FakeProcessRunner fake,
        bool moduleAvailable,
        Func<Task> body)
    {
        var previous = TunAdapterDiagnostics.Runner;
        TunAdapterDiagnostics.Runner = fake;
        TunAdapterDiagnostics.ResetRemoveNetAdapterLatchForTests();
        TunAdapterDiagnostics.SetNetAdapterModuleAvailableForTests(moduleAvailable);
        try { await body(); }
        finally
        {
            TunAdapterDiagnostics.Runner = previous;
            TunAdapterDiagnostics.ResetRemoveNetAdapterLatchForTests();
        }
    }

    /// <summary>Like WithFakeAsync but does NOT pre-set the Lazy — used
    /// by the "cache works" test which wants to observe a real Lazy
    /// resolution against the fake's Get-Module match.</summary>
    private static async Task WithFakeNoPresetAsync(
        FakeProcessRunner fake,
        Func<Task> body)
    {
        var previous = TunAdapterDiagnostics.Runner;
        TunAdapterDiagnostics.Runner = fake;
        TunAdapterDiagnostics.ResetRemoveNetAdapterLatchForTests();
        try { await body(); }
        finally
        {
            TunAdapterDiagnostics.Runner = previous;
            TunAdapterDiagnostics.ResetRemoveNetAdapterLatchForTests();
        }
    }

    /// <summary>Identify a Get-Module probe request shape — used in
    /// matchers and assertions to differentiate it from
    /// Remove-NetAdapter calls (both spawn powershell.exe).</summary>
    private static bool IsGetModuleProbe(ProcessRequest r)
    {
        return r.ExecutablePath == "powershell.exe"
            && r.Arguments.Count == 4
            && r.Arguments[0] == "-NoProfile"
            && r.Arguments[1] == "-NonInteractive"
            && r.Arguments[2] == "-Command"
            && r.Arguments[3].Contains("Get-Module NetAdapter -ListAvailable");
    }

    /// <summary>Identify a Remove-NetAdapter call shape.</summary>
    private static bool IsRemoveNetAdapter(ProcessRequest r)
    {
        return r.ExecutablePath == "powershell.exe"
            && r.Arguments.Count == 4
            && r.Arguments[3].Contains("Remove-NetAdapter");
    }

    /// <summary>Identify a netsh disable call shape.</summary>
    private static bool IsNetshDisable(ProcessRequest r)
    {
        return r.ExecutablePath == "netsh"
            && r.Arguments.Contains("admin=disabled");
    }

    /// <summary>Identify a netsh enumeration call shape.</summary>
    private static bool IsNetshEnumeration(ProcessRequest r)
    {
        return r.ExecutablePath == "netsh"
            && r.Arguments.Count == 3
            && r.Arguments[0] == "interface"
            && r.Arguments[1] == "show"
            && r.Arguments[2] == "interface";
    }

    // ─── Test 1: module available → Remove-NetAdapter fires ─────────────

    [Fact]
    public async Task NetAdapterAvailable_TrueResult_TryRemoveCallsPowerShell()
    {
        if (!OperatingSystem.IsWindows()) return;

        // Module reported available via the test-only setter — the
        // TryRemoveAdapterAsync call should spawn powershell.exe with the
        // Remove-NetAdapter script. (We bypass the probe entirely via
        // SetNetAdapterModuleAvailableForTests(true) so no Get-Module
        // call appears in the run log.)
        var fake = NewRunner();

        await WithFakeAsync(fake, moduleAvailable: true, async () =>
        {
            var result = await TunAdapterDiagnostics.TryRemoveAdapterAsync(
                logger: null, adapterName: "VPNRouter-TUN",
                context: "test.available.remove");
            Assert.True(result); // fake returns exit 0
        });

        // Exactly one powershell.exe call, matching Remove-NetAdapter shape.
        var psCalls = fake.RunCalls.Where(IsRemoveNetAdapter).ToList();
        Assert.Single(psCalls);
        Assert.Contains("'VPNRouter-TUN'", psCalls[0].Arguments[3]);
    }

    // ─── Test 2: module unavailable → Remove-NetAdapter NOT called ──────

    [Fact]
    public async Task NetAdapterAvailable_FalseResult_TryRemoveSkipsPowerShell()
    {
        if (!OperatingSystem.IsWindows()) return;

        // Module reported unavailable → TryRemoveAdapterAsync should
        // return false immediately without spawning any PowerShell.
        var fake = NewRunner();

        await WithFakeAsync(fake, moduleAvailable: false, async () =>
        {
            var result = await TunAdapterDiagnostics.TryRemoveAdapterAsync(
                logger: null, adapterName: "VPNRouter-TUN",
                context: "test.unavailable.skip");
            Assert.False(result);
        });

        // No Remove-NetAdapter PowerShell call should appear.
        Assert.DoesNotContain(fake.RunCalls, IsRemoveNetAdapter);
        // And no Get-Module probe either — we pre-set the Lazy via
        // SetNetAdapterModuleAvailableForTests so the probe is skipped.
        Assert.DoesNotContain(fake.RunCalls, IsGetModuleProbe);
    }

    // ─── Test 3: cache works across multiple calls ──────────────────────

    [Fact]
    public async Task NetAdapterAvailable_CachedAcrossCalls()
    {
        if (!OperatingSystem.IsWindows()) return;

        // Don't pre-set the Lazy — let the fake's Get-Module match drive
        // the real Lazy resolution. The probe should fire exactly once
        // even though TryRemoveAdapterAsync is called 5 times.
        var fake = new FakeProcessRunner();
        fake.OnRun(IsGetModuleProbe,
            new ProcessResult(0, "1\r\n", "", TimeSpan.FromMilliseconds(5), false));
        fake.OnRun(IsRemoveNetAdapter,
            new ProcessResult(0, "", "", TimeSpan.FromMilliseconds(5), false));

        await WithFakeNoPresetAsync(fake, async () =>
        {
            for (var i = 0; i < 5; i++)
            {
                _ = await TunAdapterDiagnostics.TryRemoveAdapterAsync(
                    logger: null, adapterName: "VPNRouter-TUN",
                    context: $"test.cache.iter{i}");
            }
        });

        // Exactly one Get-Module probe should appear in the run log.
        var probes = fake.RunCalls.Where(IsGetModuleProbe).ToList();
        Assert.Single(probes);

        // And five Remove-NetAdapter calls (one per iteration, since
        // module is available and BR-2 latch never fires on fake exit 0).
        var removes = fake.RunCalls.Where(IsRemoveNetAdapter).ToList();
        Assert.Equal(5, removes.Count);
    }

    // ─── Test 4: PreStartCleanup falls back to netsh disable ────────────

    [Fact]
    public async Task NetAdapterUnavailable_PreStartCleanup_FallsBackToNetshDisable()
    {
        if (!OperatingSystem.IsWindows()) return;

        // Enumeration returns VPNRouter-TUN; module unavailable; cleanup
        // must call netsh admin=disabled (Fix #4 fallback) and skip
        // Remove-NetAdapter entirely.
        var fake = new FakeProcessRunner();
        fake.OnRun(IsNetshEnumeration,
            new ProcessResult(0,
                Stdout: """
                Admin State    State          Type             Interface Name
                -------------------------------------------------------------------------
                Enabled        Connected      Dedicated        Ethernet
                Disabled       Disconnected   Dedicated        VPNRouter-TUN
                """,
                Stderr: "",
                Duration: TimeSpan.FromMilliseconds(10),
                TimedOut: false));
        // netsh admin=disabled responds with success
        fake.OnRun(IsNetshDisable,
            new ProcessResult(0, "Ok.", "", TimeSpan.FromMilliseconds(5), false));

        await WithFakeAsync(fake, moduleAvailable: false, async () =>
        {
            _ = await TunAdapterDiagnostics.PreStartCleanupAsync(
                logger: null, context: "test.fallback");
        });

        // netsh disable invocation captured for VPNRouter-TUN.
        var disableCalls = fake.RunCalls.Where(IsNetshDisable).ToList();
        Assert.NotEmpty(disableCalls);
        Assert.Contains(disableCalls,
            c => c.Arguments.Contains("name=VPNRouter-TUN"));

        // No Remove-NetAdapter call — Fix #4 path bypasses PowerShell.
        Assert.DoesNotContain(fake.RunCalls, IsRemoveNetAdapter);
    }

    // ─── Test 5: first call logs the actionable INF ─────────────────────

    [Fact]
    public async Task NetAdapterUnavailable_FirstCall_LogsActionableInfo()
    {
        if (!OperatingSystem.IsWindows()) return;

        // First TryRemoveAdapterAsync after probe says "unavailable" must
        // emit a single Information-level message with the user-actionable
        // hint about RSAT / Pro SKU. Subsequent calls must NOT re-emit it
        // (Debug level only).
        var fake = NewRunner();
        var sink = new InMemorySink();
        var logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(sink)
            .CreateLogger();

        await WithFakeAsync(fake, moduleAvailable: false, async () =>
        {
            _ = await TunAdapterDiagnostics.TryRemoveAdapterAsync(
                logger, "VPNRouter-TUN", "test.actionable.first");
            _ = await TunAdapterDiagnostics.TryRemoveAdapterAsync(
                logger, "VPNRouter-TUN", "test.actionable.second");
            _ = await TunAdapterDiagnostics.TryRemoveAdapterAsync(
                logger, "VPNRouter-TUN", "test.actionable.third");
        });

        var infEvents = sink.Events(LogEventLevel.Information)
            .Where(s => s.Contains("NetAdapter module unavailable"))
            .ToList();
        // Exactly one Information-level actionable message — first call
        // wins, subsequent calls log at Debug.
        Assert.Single(infEvents);
        // The message mentions RSAT or Pro/Enterprise (user-actionable
        // hint) so the user knows what to do.
        Assert.Contains("RSAT", infEvents[0]);
    }

    // ─── Test 6: cache stays "available" even if a call fails ───────────

    [Fact]
    public async Task NetAdapterAvailable_Cache_DoesNotInvalidateOnRemoveFailure()
    {
        if (!OperatingSystem.IsWindows()) return;

        // Module probe returns "available" (1). First Remove-NetAdapter
        // call returns exit 5 (e.g. permissions / adapter busy / locked) —
        // BR-2 latch sees stderr without "is not recognized" so it stays
        // at 0. Second call must still attempt Remove-NetAdapter.
        // Cache (Fix #1) should stay "available" since only Get-Module
        // can flip it.
        var fake = new FakeProcessRunner();
        fake.OnRun(IsRemoveNetAdapter,
            new ProcessResult(
                ExitCode: 5,
                Stdout: "",
                Stderr: "Access is denied (this is not a missing-cmdlet error)",
                Duration: TimeSpan.FromMilliseconds(10),
                TimedOut: false));

        await WithFakeAsync(fake, moduleAvailable: true, async () =>
        {
            var r1 = await TunAdapterDiagnostics.TryRemoveAdapterAsync(
                logger: null, adapterName: "VPNRouter-TUN",
                context: "test.no-invalidate.first");
            Assert.False(r1); // exit 5 fails

            var r2 = await TunAdapterDiagnostics.TryRemoveAdapterAsync(
                logger: null, adapterName: "VPNRouter-TUN",
                context: "test.no-invalidate.second");
            Assert.False(r2); // exit 5 again
        });

        // Both attempts must have actually spawned Remove-NetAdapter —
        // the cache should not have downgraded to "unavailable" just
        // because the first call failed with exit 5.
        var removes = fake.RunCalls.Where(IsRemoveNetAdapter).ToList();
        Assert.Equal(2, removes.Count);

        // Cache remains "available" — explicit assertion via the public
        // accessor so the contract is pinned for Fix #3 consumers.
        Assert.True(TunAdapterDiagnostics.IsNetAdapterModuleAvailable());
    }

    // ─── Test 7: Alena CP-866 mojibake → latch via CommandNotFoundException ─

    [Fact]
    public async Task RemoveFails_Cp866MojibakeWithCommandNotFoundException_LatchesAndSkipsSecondSpawn()
    {
        if (!OperatingSystem.IsWindows()) return;

        // Alena (2026-06-05, v2.41.0, Russian Windows): the proactive
        // Get-Module probe reports the NetAdapter module AVAILABLE (manifest
        // present), so TryRemoveAdapterAsync runs Remove-NetAdapter — but the
        // cmdlet itself throws CommandNotFoundException. The human-readable
        // stderr is CP-866 OEM mojibake of "не распознано" (renders as
        // "­Ґ а бЇ®§­ ­®"), which matches NONE of the three localized-message
        // substrings, so the BR-2 latch never set and EVERY connect re-spawned
        // + re-logged the failed Remove-NetAdapter (2x/connect, accumulating).
        //
        // After the fix, the latch keys on the locale-INDEPENDENT
        // "CommandNotFoundException" type name in the CategoryInfo line: the
        // first failure latches, and the second call must short-circuit
        // BEFORE spawning a second Remove-NetAdapter.
        //
        // The stderr below deliberately contains neither "is not recognized"
        // nor the UTF-8 "не распознано" nor "nicht erkannt" — only the garbled
        // message + the untranslated CommandNotFoundException CategoryInfo.
        const string mojibakeStderr =
            "Remove-NetAdapter : <CP-866 mojibake of the localized not-found message>\r\n" +
            "+ ... Router-TUN' -ErrorAction SilentlyContinue | Remove-NetAdapter -Confir ...\r\n" +
            "    + CategoryInfo          : ObjectNotFound: (Remove-NetAdapter:String) [], CommandNotFoundException\r\n" +
            "    + FullyQualifiedErrorId : CommandNotFoundException";

        var fake = new FakeProcessRunner();
        fake.OnRun(IsRemoveNetAdapter,
            new ProcessResult(
                ExitCode: 1,
                Stdout: "",
                Stderr: mojibakeStderr,
                Duration: TimeSpan.FromMilliseconds(10),
                TimedOut: false));

        // moduleAvailable: true mirrors Alena's machine — Get-Module finds the
        // manifest, so the proactive fast-fail does NOT trigger and we reach
        // the actual Remove-NetAdapter call.
        await WithFakeAsync(fake, moduleAvailable: true, async () =>
        {
            var r1 = await TunAdapterDiagnostics.TryRemoveAdapterAsync(
                logger: null, adapterName: "VPNRouter-TUN",
                context: "test.alena.mojibake.first");
            Assert.False(r1);

            var r2 = await TunAdapterDiagnostics.TryRemoveAdapterAsync(
                logger: null, adapterName: "VPNRouter-TUN",
                context: "test.alena.mojibake.second");
            Assert.False(r2);
        });

        // The latch must have fired on the first failure: exactly ONE
        // Remove-NetAdapter spawn across both calls (the second short-circuits).
        // Pre-fix this would be 2 — one noisy WRN per connect.
        var removes = fake.RunCalls.Where(IsRemoveNetAdapter).ToList();
        Assert.Single(removes);
    }

    // ─── Serilog test sink ──────────────────────────────────────────────

    /// <summary>In-memory Serilog sink for capturing rendered log events.
    /// Mirrors the pattern from <c>TgProxyAutostartLoggingTests</c>.</summary>
    private sealed class InMemorySink : ILogEventSink
    {
        private readonly List<(LogEventLevel Level, string Rendered)> _events = new();

        public void Emit(LogEvent logEvent)
        {
            var rendered = logEvent.RenderMessage();
            lock (_events)
                _events.Add((logEvent.Level, rendered));
        }

        public IReadOnlyList<string> Events(LogEventLevel level)
        {
            lock (_events)
                return _events.Where(e => e.Level == level)
                    .Select(e => e.Rendered).ToList();
        }
    }
}
