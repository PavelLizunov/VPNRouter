// PinkuDani Fix #3 (2026-05-21) — TUN orphan crash signature detection +
// LastCrashWasTunOrphan property regression suite.
//
// SingBoxManager added stderr ring buffer + DetectTunOrphanCrashSignature
// scan in OnProcessExited. When the scan matches the "Cannot create a
// file when that file already exists" substring (or related TUN-config-
// failure prefixes), LastCrashWasTunOrphan flips true. HealthMonitor's
// AttemptRestart continuation reads this flag and fires a netsh disable
// on VPNRouter-TUN before the next launch attempt.
//
// These tests pin the property semantics + signature detection — they
// use FakeProcessRunner + FakeProcessHandle to drive crash scenarios
// without spawning real sing-box. The HealthMonitor's wire-up to the
// flag is covered separately in HealthMonitorTunOrphanRestartTests.
//
// Brief: plans/pinkudani-fix3-singbox-tun-orphan-recovery-2026-05-21.md
// Dependencies: Agent A's Fix #1+#4 commit 66e1407 (IsNetAdapterModuleAvailable
//   + TryDisableAdapterViaNetshAsync public surface).

#nullable enable

using System;
using System.IO;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// Behaviour pins for the PinkuDani Fix #3 stderr signature detection on
/// <see cref="SingBoxManager"/>. Each test exercises a different stderr
/// content scenario via <see cref="FakeProcessRunner"/> + <see cref="FakeProcessHandle"/>
/// + <see cref="FakeProcessHandle.EmitError"/>, then signals exit and
/// asserts the <see cref="SingBoxManager.LastCrashWasTunOrphan"/> flag
/// landed on the expected value.
/// </summary>
public sealed class SingBoxManagerTunOrphanRecoveryTests
{
    // ─── Helpers ─────────────────────────────────────────────────────────

    private static SingBoxSettings DefaultSettings(string exePath) => new()
    {
        ExecutablePath = exePath,
        ClashApi = "127.0.0.1:9090"
    };

    /// <summary>
    /// Construct a SingBoxManager wired to a FakeProcessRunner. No Clash
    /// API calls fire in these tests (the FakeHttpClient stub is enough
    /// to satisfy the ctor).
    /// </summary>
    private static SingBoxManager BuildManager(IProcessRunner runner, string exePath)
    {
        return new SingBoxManager(
            DefaultSettings(exePath),
            logger: null,
            http: new FakeHttpClient(),
            runner: runner);
    }

    /// <summary>
    /// Write a dummy sing-box binary so the manager's File.Exists guard
    /// passes. The fake runner never executes the path.
    /// </summary>
    private static string CreateStubExe()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"sbm-tun-orphan-{Guid.NewGuid():N}.exe");
        File.WriteAllText(tmp, "stub");
        return tmp;
    }

    // ─── 1. Fresh manager default state ─────────────────────────────────

    [Fact]
    public void LastCrashWasTunOrphan_FreshManager_IsFalse()
    {
        // A never-started manager has no crash signal of any kind. Pin
        // the default-state contract so the CLI / UI doesn't observe a
        // stale true on first read.
        var fake = new FakeProcessRunner();
        var exe = CreateStubExe();
        try
        {
            using var manager = BuildManager(fake, exe);
            Assert.False(manager.LastCrashWasTunOrphan);
        }
        finally
        {
            try { File.Delete(exe); } catch { /* best-effort */ }
        }
    }

    // ─── 2. Clean exit (no stderr) leaves flag false ────────────────────

    [Fact]
    public void LastCrashWasTunOrphan_AfterCleanExit_IsFalse()
    {
        // sing-box exits with code 0 and no stderr — there's no FATAL,
        // no warning, nothing for the signature scanner to match.
        // LastCrashWasTunOrphan must stay false.
        if (!OperatingSystem.IsWindows()) return;

        var fake = new FakeProcessRunner();
        var fakeHandle = new FakeProcessHandle(pid: 4001);
        fake.OnStart(_ => true, _ => fakeHandle);

        var exe = CreateStubExe();
        try
        {
            using var manager = BuildManager(fake, exe);
            manager.StartWithJson("{}");

            // Simulate clean exit — no stderr emit, exit code 0.
            fakeHandle.SignalExit(exitCode: 0);

            Assert.False(manager.LastCrashWasTunOrphan);
        }
        finally
        {
            try { File.Delete(exe); } catch { /* best-effort */ }
        }
    }

    // ─── 3. TUN orphan FATAL signature triggers the flag ────────────────

    [Fact]
    public void LastCrashWasTunOrphan_AfterTunConflictStderr_IsTrue()
    {
        // The exact substring from PinkuDani 2026-05-21 log line 124:
        // `FATAL configure tun interface: Cannot create a file when
        // that file already exists.`
        // Scanner must flip LastCrashWasTunOrphan to true after Exited
        // — and BEFORE the Crashed event fires (HealthMonitor reads the
        // flag inside its OnSingBoxCrashed → AttemptRestart chain).
        if (!OperatingSystem.IsWindows()) return;

        var fake = new FakeProcessRunner();
        var fakeHandle = new FakeProcessHandle(pid: 4002);
        fake.OnStart(_ => true, _ => fakeHandle);

        var exe = CreateStubExe();
        try
        {
            using var manager = BuildManager(fake, exe);
            bool crashedFlagAtEventFire = false;
            manager.Crashed += (_, _) => crashedFlagAtEventFire = manager.LastCrashWasTunOrphan;

            manager.StartWithJson("{}");

            // Emit the exact stderr line from the PinkuDani field log —
            // FakeProcessHandle.EmitError feeds it through the same
            // ErrorLine event SingBoxManager's LaunchProcess subscribed to,
            // which writes into the captured stderr ring buffer.
            fakeHandle.EmitError(
                "FATAL[0015] start service: start inbound/tun[tun-in]: " +
                "configure tun interface: Cannot create a file when that file already exists.");

            fakeHandle.SignalExit(exitCode: 1);

            Assert.True(manager.LastCrashWasTunOrphan,
                "Scanner should flip the flag after observing the FATAL stderr signature.");
            Assert.True(crashedFlagAtEventFire,
                "LastCrashWasTunOrphan must already be true when Crashed event fires — " +
                "HealthMonitor's AttemptRestart needs to read it in time.");
        }
        finally
        {
            try { File.Delete(exe); } catch { /* best-effort */ }
        }
    }

    // ─── 4. Unrelated crash leaves flag false ───────────────────────────

    [Fact]
    public void LastCrashWasTunOrphan_AfterUnrelatedCrash_IsFalse()
    {
        // sing-box stderr emits a typical non-TUN-orphan error line
        // (e.g. config parse failure, OOM, network error). Signature
        // scanner must NOT false-positive on these — the flag stays
        // false so HealthMonitor doesn't fire a needless netsh disable.
        if (!OperatingSystem.IsWindows()) return;

        var fake = new FakeProcessRunner();
        var fakeHandle = new FakeProcessHandle(pid: 4003);
        fake.OnStart(_ => true, _ => fakeHandle);

        var exe = CreateStubExe();
        try
        {
            using var manager = BuildManager(fake, exe);
            manager.StartWithJson("{}");

            // Unrelated error class — should NOT match any of the three
            // signature substrings.
            fakeHandle.EmitError(
                "FATAL[0001] start service: outbound[proxy]: " +
                "vless: dial: connection refused");

            fakeHandle.SignalExit(exitCode: 1);

            Assert.False(manager.LastCrashWasTunOrphan,
                "Scanner must NOT false-positive on unrelated sing-box errors.");
        }
        finally
        {
            try { File.Delete(exe); } catch { /* best-effort */ }
        }
    }

    // ─── 5. Successful Start clears a previous true ─────────────────────

    [Fact]
    public void LastCrashWasTunOrphan_ResetOnSuccessfulStart()
    {
        // After a crash flipped the flag to true, the next successful
        // StartWithJson clears it. Without this reset, HealthMonitor would
        // observe stale "true" on a fresh sing-box session that later
        // (unrelatedly) crashed.
        if (!OperatingSystem.IsWindows()) return;

        var fake = new FakeProcessRunner();
        var handles = new System.Collections.Generic.List<FakeProcessHandle>();
        fake.OnStart(_ => true, _ =>
        {
            var h = new FakeProcessHandle(pid: 4100 + handles.Count);
            handles.Add(h);
            return h;
        });

        var exe = CreateStubExe();
        try
        {
            using var manager = BuildManager(fake, exe);

            // First lifecycle: crash with the orphan signature.
            manager.StartWithJson("{}");
            handles[0].EmitError(
                "FATAL configure tun interface: Cannot create a file when that file already exists.");
            handles[0].SignalExit(exitCode: 1);
            Assert.True(manager.LastCrashWasTunOrphan,
                "Precondition: first crash flipped the flag.");

            // Second lifecycle: fresh Start clears the flag.
            manager.StartWithJson("{}");
            Assert.False(manager.LastCrashWasTunOrphan,
                "StartWithJson must clear LastCrashWasTunOrphan so " +
                "the new session doesn't inherit the previous crash's state.");
        }
        finally
        {
            try { File.Delete(exe); } catch { /* best-effort */ }
        }
    }

    // ─── 6. Explicit Stop clears the flag ───────────────────────────────

    [Fact]
    public void LastCrashWasTunOrphan_ResetOnStop()
    {
        // After a crash flipped the flag, user-initiated Stop() resets
        // it. Stop = user opted out of the recovery loop; the flag
        // shouldn't carry across into a manual Reconnect.
        if (!OperatingSystem.IsWindows()) return;

        var fake = new FakeProcessRunner();
        var fakeHandle = new FakeProcessHandle(pid: 4200);
        fake.OnStart(_ => true, _ => fakeHandle);

        var exe = CreateStubExe();
        try
        {
            using var manager = BuildManager(fake, exe);
            manager.StartWithJson("{}");
            fakeHandle.EmitError(
                "FATAL configure tun interface: Cannot create a file when that file already exists.");
            fakeHandle.SignalExit(exitCode: 1);
            Assert.True(manager.LastCrashWasTunOrphan, "Precondition: crash flipped the flag.");

            manager.Stop();

            Assert.False(manager.LastCrashWasTunOrphan,
                "Stop() must clear LastCrashWasTunOrphan — user opt-out.");
        }
        finally
        {
            try { File.Delete(exe); } catch { /* best-effort */ }
        }
    }

    // ─── 7. Broader configure-tun-interface prefix also matches ─────────

    [Fact]
    public void LastCrashWasTunOrphan_BroaderPrefixSubstring_AlsoMatches()
    {
        // The scanner matches three patterns. Pin that the `configure
        // tun interface:` prefix (broader than the full FATAL string)
        // ALSO triggers the flag, catching localised or future TUN-
        // config-failure modes that share the orphan-handle root cause
        // but vary in the trailing error text.
        if (!OperatingSystem.IsWindows()) return;

        var fake = new FakeProcessRunner();
        var fakeHandle = new FakeProcessHandle(pid: 4300);
        fake.OnStart(_ => true, _ => fakeHandle);

        var exe = CreateStubExe();
        try
        {
            using var manager = BuildManager(fake, exe);
            manager.StartWithJson("{}");

            // Synthetic line with only the broader prefix — no "Cannot
            // create a file" trailing text. The middle of the three
            // signature substrings should still match.
            fakeHandle.EmitError(
                "FATAL[0042] start service: start inbound/tun[tun-in]: " +
                "configure tun interface: some other failure mode here");

            fakeHandle.SignalExit(exitCode: 1);

            Assert.True(manager.LastCrashWasTunOrphan,
                "configure tun interface: prefix should match — catches " +
                "localised + future variants of the same root cause.");
        }
        finally
        {
            try { File.Delete(exe); } catch { /* best-effort */ }
        }
    }
}
