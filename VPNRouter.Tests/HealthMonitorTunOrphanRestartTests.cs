// PinkuDani Fix #3 (2026-05-21) — HealthMonitor TUN orphan recovery
// regression suite.
//
// HealthMonitor's AttemptRestart now consults SingBoxManager's new
// LastCrashWasTunOrphan flag. When set, it fires a netsh disable on
// VPNRouter-TUN before relaunching sing-box — closing the gap where
// Fix #1+#4's PreStartCleanupAsync didn't find the orphan via netsh
// enumeration (PinkuDani 2026-05-21: enumeration timing unreliable
// mid-restart-loop).
//
// These tests pin the AttemptRestart cleanup hook. The cleanup logic
// lives in an internal helper RunTunOrphanRecoveryCleanup so tests
// can invoke it without waiting 5+ s for the exponential-backoff
// Task.Delay continuation.
//
// Brief: plans/pinkudani-fix3-singbox-tun-orphan-recovery-2026-05-21.md
// Companion: SingBoxManagerTunOrphanRecoveryTests pins the flag's
//   stderr-detection + reset semantics on the SingBoxManager side.

#nullable enable

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// Behaviour pins for the HealthMonitor side of PinkuDani Fix #3.
/// <see cref="HealthMonitor.RunTunOrphanRecoveryCleanup"/> reads
/// <see cref="SingBoxManager.LastCrashWasTunOrphan"/> and calls
/// <see cref="TunAdapterDiagnostics.TryDisableAdapterViaNetshAsync"/>
/// when true. Tests swap <see cref="TunAdapterDiagnostics.Runner"/>
/// for a <see cref="FakeProcessRunner"/> and assert the netsh call
/// observed there.
/// </summary>
public sealed class HealthMonitorTunOrphanRestartTests
{
    private sealed class StubProcessScanner : VPNRouter.Core.Interfaces.IProcessScanner
    {
        public ScanResult ScanForProfile(Profile profile) => new();
    }

    private sealed class StubFirewallManager : VPNRouter.Core.Interfaces.IFirewallManager
    {
        public void CreateBlockRules(System.Collections.Generic.IEnumerable<string> processNames, bool isFullTunnel = true) { }
        public void EnableBlockRules() { }
        public void DisableBlockRules() { }
        public void DeleteAllRules() { }
        public void Dispose() { }
    }

    // ─── Helpers ─────────────────────────────────────────────────────────

    /// <summary>
    /// Build a SingBoxManager wired to a FakeProcessRunner. Used so we
    /// can drive the LastCrashWasTunOrphan flag via the real signature-
    /// detection path (stderr emit → SignalExit → DetectTunOrphanCrashSignature).
    /// </summary>
    private static SingBoxManager BuildSingBox(IProcessRunner runner, string exePath)
    {
        var settings = new SingBoxSettings
        {
            ExecutablePath = exePath,
            ClashApi = "127.0.0.1:9090"
        };
        return new SingBoxManager(settings, logger: null,
            http: new FakeHttpClient(), runner: runner);
    }

    /// <summary>Build a HealthMonitor around the given SingBoxManager.</summary>
    private static HealthMonitor BuildHm(SingBoxManager sb)
    {
        var scanner = new StubProcessScanner();
        var fw = new StubFirewallManager();
        var monSettings = new MonitoringSettings
        {
            HealthCheckInterval = 3600,
            MaxRestartAttempts = 5,
            RestartOnFailure = true,
        };
        return new HealthMonitor(sb, scanner, fw, monSettings);
    }

    private static string CreateStubExe()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"sbm-hm-tun-{Guid.NewGuid():N}.exe");
        File.WriteAllText(tmp, "stub");
        return tmp;
    }

    /// <summary>
    /// Invoke the internal RunTunOrphanRecoveryCleanup helper directly
    /// via reflection. This bypasses the 5-second Task.Delay timer in
    /// AttemptRestart and lets us assert the netsh call behaviour
    /// synchronously inside the test.
    /// </summary>
    private static bool InvokeRunTunOrphanRecoveryCleanup(
        HealthMonitor hm, System.Threading.CancellationToken ct)
    {
        var m = typeof(HealthMonitor).GetMethod(
            "RunTunOrphanRecoveryCleanup",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!;
        return (bool)m.Invoke(hm, new object[] { ct })!;
    }

    // ─── 1. Flag set → netsh disable fires ──────────────────────────────

    [Fact]
    public void AttemptRestart_TunOrphanFlag_TriggersNetshDisable()
    {
        // Pin: when SingBoxManager.LastCrashWasTunOrphan == true, the
        // HealthMonitor.AttemptRestart cleanup hook calls
        // TunAdapterDiagnostics.TryDisableAdapterViaNetshAsync with the
        // well-known "VPNRouter-TUN" adapter name BEFORE the next launch
        // attempt. The FakeProcessRunner records the netsh call so we
        // can assert its argv shape.
        if (!OperatingSystem.IsWindows()) return;

        // Set up the SingBoxManager with a real signature-detection
        // path: a FakeProcessHandle emits the TUN orphan FATAL stderr
        // line, then signals exit. The scanner flips LastCrashWasTunOrphan
        // to true.
        var sbFake = new FakeProcessRunner();
        var sbHandle = new FakeProcessHandle(pid: 5001);
        sbFake.OnStart(_ => true, _ => sbHandle);

        var exe = CreateStubExe();
        var previousTunRunner = TunAdapterDiagnostics.Runner;
        var tunFake = new FakeProcessRunner();
        try
        {
            using var sb = BuildSingBox(sbFake, exe);
            sb.StartWithJson("{}");

            // Emit the FATAL signature + signal exit so the scanner
            // observes the orphan crash.
            sbHandle.EmitError(
                "FATAL configure tun interface: Cannot create a file when that file already exists.");
            sbHandle.SignalExit(exitCode: 1);
            Assert.True(sb.LastCrashWasTunOrphan,
                "Precondition: SingBoxManager flagged the previous crash as TUN orphan.");

            // Swap the TunAdapterDiagnostics process runner so we capture
            // the netsh call without spawning real netsh.exe.
            TunAdapterDiagnostics.Runner = tunFake;
            tunFake.OnRun(
                r => r.ExecutablePath == "netsh"
                  && r.Arguments.Count >= 2
                  && r.Arguments[0] == "interface"
                  && r.Arguments[1] == "set",
                new ProcessResult(0, "Ok.", "", TimeSpan.FromMilliseconds(5), false));

            using var hm = BuildHm(sb);
            var result = InvokeRunTunOrphanRecoveryCleanup(
                hm, System.Threading.CancellationToken.None);

            Assert.True(result,
                "Cleanup should report true when caller cancellation didn't fire.");

            // Verify the netsh call shape:
            //   netsh interface set interface name=VPNRouter-TUN admin=disabled
            var netshCalls = tunFake.RunCalls
                .Where(r => r.ExecutablePath == "netsh"
                            && r.Arguments.Count >= 2
                            && r.Arguments[0] == "interface"
                            && r.Arguments[1] == "set")
                .ToList();
            Assert.NotEmpty(netshCalls);
            var call = netshCalls[0];
            Assert.Contains("interface", call.Arguments);
            Assert.Contains("set", call.Arguments);
            Assert.Contains("name=VPNRouter-TUN", call.Arguments);
            Assert.Contains("admin=disabled", call.Arguments);
        }
        finally
        {
            TunAdapterDiagnostics.Runner = previousTunRunner;
            try { File.Delete(exe); } catch { /* best-effort */ }
        }
    }

    // ─── 2. Flag clear → netsh disable skipped ──────────────────────────

    [Fact]
    public void AttemptRestart_NoTunOrphanFlag_SkipsNetshDisable()
    {
        // Pin: when LastCrashWasTunOrphan == false, the cleanup hook is a
        // no-op — no netsh call observed. We don't want to pay the netsh
        // cost on unrelated restarts (the common case for non-TUN-orphan
        // crashes).
        if (!OperatingSystem.IsWindows()) return;

        var sbFake = new FakeProcessRunner();
        var sbHandle = new FakeProcessHandle(pid: 5002);
        sbFake.OnStart(_ => true, _ => sbHandle);

        var exe = CreateStubExe();
        var previousTunRunner = TunAdapterDiagnostics.Runner;
        var tunFake = new FakeProcessRunner();
        try
        {
            using var sb = BuildSingBox(sbFake, exe);
            sb.StartWithJson("{}");

            // Unrelated stderr — flag stays false.
            sbHandle.EmitError("FATAL outbound[proxy]: vless dial: connection refused");
            sbHandle.SignalExit(exitCode: 1);
            Assert.False(sb.LastCrashWasTunOrphan,
                "Precondition: unrelated crash leaves the flag false.");

            TunAdapterDiagnostics.Runner = tunFake;
            // No matchers registered — any unmocked call would throw,
            // which is what we want to assert "no call happens".
            using var hm = BuildHm(sb);

            var result = InvokeRunTunOrphanRecoveryCleanup(
                hm, System.Threading.CancellationToken.None);
            Assert.True(result,
                "Cleanup returns true when there's nothing to do.");

            // RunTunOrphanRecoveryCleanup short-circuits on the false flag, so
            // it makes NO `netsh … admin=disabled` call — that's the contract
            // under test. Assert the SPECIFIC netsh-disable absence rather than
            // global emptiness: the `SignalExit` above schedules a fire-and-forget
            // OnProcessExited crash-cleanup that can independently spawn
            // Get-NetAdapter/pnputil probe calls on the shared TunAdapterDiagnostics
            // Runner — unrelated to this hook and not a netsh disable. (Pre-2026-06-08
            // the global Assert.Empty passed only by async-timing luck; the orphan
            // removal rewrite made that race observable.)
            var netshDisableCalls = tunFake.RunCalls
                .Where(r => r.ExecutablePath == "netsh"
                            && r.Arguments.Contains("admin=disabled"))
                .ToList();
            Assert.Empty(netshDisableCalls);
        }
        finally
        {
            TunAdapterDiagnostics.Runner = previousTunRunner;
            try { File.Delete(exe); } catch { /* best-effort */ }
        }
    }
}
