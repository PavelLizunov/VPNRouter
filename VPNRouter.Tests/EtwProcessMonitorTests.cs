#nullable enable
// ═══════════════════════════════════════════════════════════════════════════════
// v3.0 Phase 2G — sub-wave 7b-1: EtwProcessMonitor coverage (HIGH priority)
// ═══════════════════════════════════════════════════════════════════════════════
//
// Brief: plans/phase2-2G-untested-services-2026-05-17.md
//
// EtwProcessMonitor is a Windows-only (PLATFORM_WINDOWS) service that uses
// `Microsoft.Diagnostics.Tracing` to watch the kernel process provider on a
// dedicated background thread. The live ETW subscription path requires:
//   - Administrator privileges
//   - A running process actually emitting Start/Stop events
//   - The TraceEventSession kernel binding being exclusive (one per machine)
// Combined those make the subscription path unsuitable for unit tests in CI.
//
// Per the brief: "Test the parser surface + event-translation logic instead,
// not the ETW subscription itself." We exercise:
//   1. The event-translation helper (TranslateProcessEvent) — pinned via an
//      internal seam Phase 2G added so the lambdas inside RunSession can
//      be verified without a real TraceEvent payload.
//   2. The constructor seam (IProcessRunner injection from Wave 6 Phase 2D)
//      — verify the optional dependency doesn't break existing call sites.
//   3. Dispose semantics — calling Dispose without Start, double-Dispose,
//      and Dispose-after-Stop must all be safe.
//   4. Smoke: Start+Stop on a Windows admin runner ≠ this test environment,
//      so we don't actually exercise the subscription. We *do* verify that
//      construction + Dispose are non-throwing in the unit-test environment.
//
// All tests gate `if (!OperatingSystem.IsWindows()) return;` so the test
// class compiles and runs on Linux/macOS CI but no-ops there. EtwProcessMonitor
// itself is wrapped in `#if PLATFORM_WINDOWS` so non-Windows hosts won't even
// have the type at runtime — the gate short-circuits before the type touch.
// ═══════════════════════════════════════════════════════════════════════════════

using VPNRouter.Core.Interfaces;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;

namespace VPNRouter.Tests;

/// <summary>
/// Unit tests for <see cref="EtwProcessMonitor"/>. Focused on the
/// translatable surface (event-arg construction, ctor seam, dispose
/// invariants) — the kernel ETW subscription itself is verified by
/// manual smoke (start VPNRouter as admin, launch a process, see log
/// "[ETW] ProcessStarted") and is not covered here.
/// </summary>
public sealed class EtwProcessMonitorTests
{
#if PLATFORM_WINDOWS
    [Fact]
    public void TranslateProcessEvent_HappyPath_ConstructsArgsVerbatim()
    {
        // Sanity: typical kernel event for a userspace process — non-zero
        // pid, real exe basename, valid parent. The args must mirror the
        // input fields verbatim (no normalisation needed in the happy case).
        var args = EtwProcessMonitor.TranslateProcessEvent(
            processId: 4321,
            imageFileName: "Discord.exe",
            parentProcessId: 1234);

        Assert.Equal(4321, args.ProcessId);
        Assert.Equal("Discord.exe", args.ProcessName);
        Assert.Equal(1234, args.ParentProcessId);
    }

    [Fact]
    public void TranslateProcessEvent_NullImageFileName_NormalisesToEmptyString()
    {
        // Defensive: the kernel may emit a partial event mid-process-slot
        // setup where ImageFileName is null. We normalise to an empty
        // string so downstream consumers (ProcessScanner, HealthMonitor
        // debounce) can call `.Equals` / `.Contains` without NRE.
        var args = EtwProcessMonitor.TranslateProcessEvent(
            processId: 9999,
            imageFileName: null,
            parentProcessId: 1);

        Assert.Equal(9999, args.ProcessId);
        Assert.NotNull(args.ProcessName);
        Assert.Equal(string.Empty, args.ProcessName);
    }

    [Fact]
    public void TranslateProcessEvent_PidZero_PassesThroughForCallerFilter()
    {
        // PID 0 = idle/system. ETW occasionally surfaces it on
        // KernelTraceEventParser.Keywords.Process when the loader
        // initialises a slot. We pass it through rather than dropping
        // here — the caller (HealthMonitor) is the right place to
        // decide whether PID 0 matters (it doesn't, since the user
        // can't add "system idle" to a profile anyway).
        var args = EtwProcessMonitor.TranslateProcessEvent(
            processId: 0,
            imageFileName: "Idle",
            parentProcessId: 0);

        Assert.Equal(0, args.ProcessId);
        Assert.Equal("Idle", args.ProcessName);
        Assert.Equal(0, args.ParentProcessId);
    }

    [Fact]
    public void TranslateProcessEvent_NegativePid_PassesThroughForCallerFilter()
    {
        // Negative PID can show up briefly when ETW reports a deleted/
        // pre-fork transient slot. Same policy as PID 0 — pass through
        // and let the caller filter. We mainly want to assert this
        // doesn't throw (e.g. via signed/unsigned arithmetic) and the
        // value is preserved.
        var args = EtwProcessMonitor.TranslateProcessEvent(
            processId: -1,
            imageFileName: "TransientSlot.exe",
            parentProcessId: -1);

        Assert.Equal(-1, args.ProcessId);
        Assert.Equal("TransientSlot.exe", args.ProcessName);
        Assert.Equal(-1, args.ParentProcessId);
    }

    [Fact]
    public void TranslateProcessEvent_EmptyImageFileName_PreservedAsEmpty()
    {
        // Distinct from null: caller already gave us "". We don't
        // double-coalesce — empty stays empty.
        var args = EtwProcessMonitor.TranslateProcessEvent(
            processId: 5,
            imageFileName: string.Empty,
            parentProcessId: 4);

        Assert.Equal(string.Empty, args.ProcessName);
    }

    [Fact]
    public void TranslateProcessEvent_PreservesCaseExactly()
    {
        // CRITICAL: sing-box process_name matching is case-sensitive
        // (Go map lookup via filepath.Base on Windows). The kernel
        // returns filesystem casing (e.g. "Discord.exe" not "discord.exe").
        // If we ever ToLowerInvariant() at this seam we break matching.
        // Pin the invariant.
        var args = EtwProcessMonitor.TranslateProcessEvent(
            processId: 1,
            imageFileName: "MiXeDcAsE.ExE",
            parentProcessId: 0);

        Assert.Equal("MiXeDcAsE.ExE", args.ProcessName);
    }

    [Fact]
    public void Constructor_OptionalProcessRunnerSeam_AcceptsCustomFake()
    {
        // v3.0 Phase 2D (commit 98ed9dd) added an optional IProcessRunner
        // parameter for future Phase 2G tasklist/wmic shell-outs (currently
        // unused inside EtwProcessMonitor itself). Verify the existing
        // ctor signature accepts a FakeProcessRunner without breaking
        // existing call sites (`new EtwProcessMonitor(logger)`).
        if (!OperatingSystem.IsWindows()) return;

        var fake = new FakeProcessRunner();

        // Both flavours must work — explicit fake and default.
        using var monitorWithFake = new EtwProcessMonitor(
            logger: null,
            processRunner: fake);
        using var monitorDefault = new EtwProcessMonitor();

        // Pin: no records of any Run/Start calls — EtwProcessMonitor
        // doesn't currently shell out, so the seam should be untouched
        // by mere construction.
        Assert.Empty(fake.RunCalls);
        Assert.Empty(fake.StartCalls);
    }

    [Fact]
    public void Dispose_BeforeStart_DoesNotThrow()
    {
        // Symmetric with PowerEventListenerTests.Dispose_BeforeStart_DoesNotThrow.
        // The user can construct + dispose without ever calling Start
        // (e.g. early-failure path in VpnEngine ctor) and we must not
        // NRE on the ManualResetEventSlim or session-thread fields.
        if (!OperatingSystem.IsWindows()) return;

        var monitor = new EtwProcessMonitor();
        monitor.Dispose(); // never started — must not throw
    }

    [Fact]
    public void Dispose_TwiceIsSafe()
    {
        // Idempotency: a double-Dispose path can come from `using` +
        // explicit Dispose, or from a finalizer + manual call. We
        // gate the second pass via `_disposed` so the kernel handle
        // for _sessionReady isn't double-disposed (would throw
        // ObjectDisposedException pre-v2.31.1).
        if (!OperatingSystem.IsWindows()) return;

        var monitor = new EtwProcessMonitor();
        monitor.Dispose();
        monitor.Dispose(); // second call must be a no-op
    }

    [Fact]
    public void Stop_BeforeStart_DoesNotThrow()
    {
        // The CO-6 audit fix added a 1s bounded wait for _sessionReady
        // before Stop touches the session. Verify Stop is safe when
        // Start was never called — the wait should time out, log a
        // warning, and return cleanly without crashing.
        if (!OperatingSystem.IsWindows()) return;

        var monitor = new EtwProcessMonitor();
        try
        {
            monitor.Stop(); // ready-wait expires at 1s, no session, no thread — clean return
        }
        finally
        {
            monitor.Dispose();
        }
    }

    [Fact]
    public void Events_RemainUnsubscribed_NoFireWithoutSession()
    {
        // Without an active ETW session there's no source of events,
        // so neither ProcessStarted nor ProcessStopped should fire
        // just from construction. Pin this by attaching handlers and
        // verifying they never run during the test lifetime.
        if (!OperatingSystem.IsWindows()) return;

        var startedCount = 0;
        var stoppedCount = 0;

        using var monitor = new EtwProcessMonitor();
        monitor.ProcessStarted += (_, _) => Interlocked.Increment(ref startedCount);
        monitor.ProcessStopped += (_, _) => Interlocked.Increment(ref stoppedCount);

        // No Start() — no session — no events.
        Assert.Equal(0, startedCount);
        Assert.Equal(0, stoppedCount);
    }
#endif

    [Fact]
    public void TestClassCompilesOnAllPlatforms()
    {
        // EtwProcessMonitor is wrapped in #if PLATFORM_WINDOWS so the
        // test bodies above only compile on Windows. This is a
        // platform-portable smoke that this test class itself is
        // included in the build assembly on every host — guarantees
        // test discovery sees us. (Without this fact the assembly's
        // discovered-test count would drop to 0 on non-Windows.)
        Assert.True(true);
    }
}
