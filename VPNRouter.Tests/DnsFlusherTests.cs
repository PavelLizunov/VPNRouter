#nullable enable
// ============================================================================
// DnsFlusherTests.cs — Phase 2G Wave 7a-2 (MED priority service coverage)
// ============================================================================
//
// Covers VPNRouter.Core.Services.DnsFlusher — the ipconfig /flushdns wrapper
// called from VpnEngine before a VPN session starts. Failure mode is silent
// stale DNS cache, not a leak — but it's still worth pinning the call shape
// so a regression (wrong executable, wrong args, swallowed exit code) gets
// caught at unit-test time.
//
// As of Phase 2G (this brief) DnsFlusher was refactored to take an
// IProcessRunner via ctor for testability; the static Flush(ILogger)
// facade is preserved so VpnEngine doesn't need to change.
//
// Test shapes (6 cases):
//   1. Happy path: fake returns exit 0 → FlushInstance returns true and the
//      recorded ProcessRequest has args ["/flushdns"].
//   2. Nonzero exit: fake returns exit 1 → FlushInstance returns false,
//      does not throw.
//   3. Timeout: fake returns TimedOut=true → FlushInstance returns false,
//      does not throw.
//   4. Exception in runner: fake throws → FlushInstance returns false,
//      does not throw (defensive catch).
//   5. Idempotency: call twice → both succeed and produce 2 recorded calls.
//   6. Argument correctness: executable is "ipconfig.exe", no extra args.
//
// On non-Windows hosts the dispatch goes through FlushMac / no-op; we run
// most tests with the Windows guard since v3.0 desktop is still Windows-
// first. A separate "non-Windows skips DNS-flush quietly" test covers the
// Linux / unsupported-platform branch.
//
// Brief: plans/phase2-2G-untested-services-2026-05-17.md (sub-wave 7a-2)
// ============================================================================

using System.Runtime.InteropServices;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;

namespace VPNRouter.Tests;

/// <summary>
/// Unit tests for <see cref="DnsFlusher"/>. Pin the call shape going into
/// <see cref="IProcessRunner"/> so we don't regress on the executable name
/// (must be <c>ipconfig.exe</c>) or the args (<c>/flushdns</c> only).
/// </summary>
public sealed class DnsFlusherTests
{
    /// <summary>Most tests target the Windows branch — match its OS guard.</summary>
    private static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    [Fact]
    public void FlushInstance_HappyPath_ReturnsTrueAndRunsIpconfig()
    {
        if (!IsWindows) return;

        var fake = new FakeProcessRunner();
        fake.OnRun(
            r => r.ExecutablePath == "ipconfig.exe",
            new ProcessResult(
                ExitCode: 0,
                Stdout: "Windows IP Configuration\n\nSuccessfully flushed the DNS Resolver Cache.\n",
                Stderr: "",
                Duration: TimeSpan.FromMilliseconds(50),
                TimedOut: false));

        var sut = new DnsFlusher(fake);
        var ok = sut.FlushInstance();

        Assert.True(ok, "Exit 0 should return true");
        Assert.Single(fake.RunCalls);
        Assert.Equal("ipconfig.exe", fake.RunCalls[0].ExecutablePath);
    }

    [Fact]
    public void FlushInstance_ArgumentCorrectness_OnlyFlushdns()
    {
        if (!IsWindows) return;

        var fake = new FakeProcessRunner();
        fake.OnRun(
            r => r.ExecutablePath == "ipconfig.exe",
            new ProcessResult(0, "", "", TimeSpan.FromMilliseconds(10), false));

        var sut = new DnsFlusher(fake);
        sut.FlushInstance();

        // Exactly one arg, exactly "/flushdns" — no extras like "/all" or
        // "/release" which would mutate the network stack.
        Assert.Single(fake.RunCalls);
        var args = fake.RunCalls[0].Arguments;
        Assert.Single(args);
        Assert.Equal("/flushdns", args[0]);
    }

    [Fact]
    public void FlushInstance_NonzeroExit_ReturnsFalseDoesNotThrow()
    {
        if (!IsWindows) return;

        var fake = new FakeProcessRunner();
        fake.OnRun(
            r => r.ExecutablePath == "ipconfig.exe",
            new ProcessResult(
                ExitCode: 1,
                Stdout: "",
                Stderr: "The requested operation requires elevation.",
                Duration: TimeSpan.FromMilliseconds(20),
                TimedOut: false));

        var sut = new DnsFlusher(fake);

        // Must not throw — non-elevated runs in particular can fail and the
        // VPN start path should continue regardless.
        bool? result = null;
        var ex = Record.Exception(() => result = sut.FlushInstance());

        Assert.Null(ex);
        Assert.NotNull(result);
        Assert.False(result!.Value);
    }

    [Fact]
    public void FlushInstance_Timeout_ReturnsFalseDoesNotThrow()
    {
        if (!IsWindows) return;

        var fake = new FakeProcessRunner();
        fake.OnRun(
            r => r.ExecutablePath == "ipconfig.exe",
            new ProcessResult(
                ExitCode: -1,
                Stdout: "",
                Stderr: "",
                Duration: TimeSpan.FromSeconds(5),
                TimedOut: true));

        var sut = new DnsFlusher(fake);
        bool? result = null;
        var ex = Record.Exception(() => result = sut.FlushInstance());

        Assert.Null(ex);
        Assert.NotNull(result);
        Assert.False(result!.Value);
    }

    [Fact]
    public void FlushInstance_RunnerThrows_IsCaughtReturnsFalse()
    {
        if (!IsWindows) return;

        // Simulate a runner that throws — DnsFlusher should swallow,
        // log, and return false. (Defensive catch in FlushWindows + the
        // outer FlushInstance catch.)
        var fake = new FakeProcessRunner();
        fake.OnRun(
            r => r.ExecutablePath == "ipconfig.exe",
            _ => throw new InvalidOperationException("simulated runner failure"));

        var sut = new DnsFlusher(fake);
        bool? result = null;
        var ex = Record.Exception(() => result = sut.FlushInstance());

        Assert.Null(ex);
        Assert.NotNull(result);
        Assert.False(result!.Value);
    }

    [Fact]
    public void FlushInstance_Idempotent_CalledTwiceBothSucceed()
    {
        if (!IsWindows) return;

        // Calling Flush repeatedly during a startup retry loop must be
        // safe and produce 2 distinct ProcessRequest entries — i.e. there
        // is no hidden state ("already flushed this session") in DnsFlusher.
        var fake = new FakeProcessRunner();
        fake.OnRun(
            r => r.ExecutablePath == "ipconfig.exe",
            new ProcessResult(0, "", "", TimeSpan.FromMilliseconds(10), false));

        var sut = new DnsFlusher(fake);
        var first = sut.FlushInstance();
        var second = sut.FlushInstance();

        Assert.True(first);
        Assert.True(second);
        Assert.Equal(2, fake.RunCalls.Count);
        Assert.All(fake.RunCalls, r => Assert.Equal("ipconfig.exe", r.ExecutablePath));
    }

    [Fact]
    public void FlushInstance_OnWindows_HasReasonableTimeout()
    {
        if (!IsWindows) return;

        // Pin the call shape's Timeout — production sets a 5s ceiling so
        // a hung ipconfig can't block VPN start indefinitely.
        var fake = new FakeProcessRunner();
        fake.OnRun(
            r => r.ExecutablePath == "ipconfig.exe",
            new ProcessResult(0, "", "", TimeSpan.FromMilliseconds(10), false));

        var sut = new DnsFlusher(fake);
        sut.FlushInstance();

        Assert.Single(fake.RunCalls);
        var timeout = fake.RunCalls[0].Timeout;
        Assert.NotNull(timeout);
        // Concrete spec is 5s. Use a tolerant range that still pins the
        // intent ("a few seconds, not minutes, not none").
        Assert.True(timeout!.Value >= TimeSpan.FromSeconds(1),
            $"Expected ≥1s timeout, got {timeout.Value}");
        Assert.True(timeout.Value <= TimeSpan.FromSeconds(30),
            $"Expected ≤30s timeout, got {timeout.Value}");
    }

    [Fact]
    public void StaticFacade_Flush_NoThrowOnRealRuntime()
    {
        // The static Flush method dispatches to DefaultInstance which wraps
        // a real ProcessRunner. On Windows this actually shells out to
        // ipconfig.exe — that's fine in a unit test (read-only, sub-second).
        // On non-Windows the dispatch returns silently.
        //
        // We only assert it doesn't throw — the real run mutates the dev
        // box's DNS cache, but that's idempotent and isolated.
        var ex = Record.Exception(() => DnsFlusher.Flush());
        Assert.Null(ex);
    }
}
