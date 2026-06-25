// Phase 2G (2026-05-21) — SingBoxManager state-machine characterization tests.
//
// Why: SingBoxManager is 1133 LOC — the process lifecycle owner (start /
// stop / restart / hot-reload via Clash API) — with the v2.31.x recovery
// work concentrated in it, but only one sibling test file
// (SingBoxManagerRestartTunHandshakeTests) which is source-string-pin
// only for the Wave-38 TUN-adapter hotfix. The state-machine surface
// + Clash API HTTP routing + the 3G-2 IHttpClient injection regression
// were completely untested at the unit-test level.
//
// Phase 3G-2 (commit 5370be3) unblocked this by adding an optional
// IHttpClient ctor parameter (default PolicyHttpClient.Shared). Tests
// can now inject FakeHttpClient to stub the 127.0.0.1:9090 Clash API
// without spawning a real sing-box process.
//
// What's STILL not unit-testable (deferred until SingBoxManager gains
// an IProcess seam): the actual LaunchProcess → Process.Start path,
// the Crash detection event timing (Exited → OnProcessExited →
// Crashed), and the Linux pkexec escalation chain (LinuxStopEscalationChain
// + TrySpawnAndWait — external-process-heavy). For those, see
// SingBoxManagerRestartTunHandshakeTests' source-string pins covering
// LaunchProcess → PreStartCleanup chain. This file uses reflection to
// poke the _process / _currentConfigPath fields when exercising the
// HTTP-routing surface (TryHotReload + IsClashApiAlive) because those
// methods guard on _process being non-null before reaching the HTTP
// path — without the poke the early return short-circuits before any
// HTTP call.
//
// Brief: plans/phase2G-singboxmanager-statemachine-2026-05-21.md.

#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;

namespace VPNRouter.Tests;

/// <summary>
/// Characterization tests for <see cref="SingBoxManager"/>'s state-machine
/// surface — construction state, idle Stop/Dispose semantics, default
/// metric / health probes, and the Clash API HTTP routing wired through
/// the Phase 3G-2 <see cref="IHttpClient"/> seam.
///
/// <para>The full LaunchProcess → Process.Start → Running state path is
/// intentionally NOT covered here because SingBoxManager spawns sing-box
/// via <see cref="Process.Start(ProcessStartInfo)"/> with no factory
/// interception. Phase 3+ introduces an IProcessRunner seam; that
/// lifecycle matrix will land then. Cross-references:
/// <see cref="SingBoxManagerRestartTunHandshakeTests"/> (Wave-38
/// LaunchProcess source pins),
/// <see cref="HealthMonitorRecoveryGapTests"/> (crash-then-restart path
/// via its own SingBoxManager stub).</para>
/// </summary>
public sealed class SingBoxManagerStateMachineTests
{
    // ─── Helpers ─────────────────────────────────────────────────────────

    private static SingBoxSettings DefaultSettings() => new()
    {
        ExecutablePath = @"C:\nonexistent\sing-box.exe",
        ClashApi = "127.0.0.1:9090"
    };

    private static SingBoxManager BuildManager(IHttpClient http) =>
        new SingBoxManager(DefaultSettings(), logger: null, http: http);

    /// <summary>
    /// Read a private/internal field via reflection. Returns the boxed
    /// value (caller casts).
    /// </summary>
    private static object? GetField(SingBoxManager m, string fieldName)
    {
        var f = typeof(SingBoxManager).GetField(fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new InvalidOperationException(
                $"SingBoxManager has no field '{fieldName}'");
        return f.GetValue(m);
    }

    /// <summary>
    /// Write a private/internal field via reflection. Used to poke the
    /// `_process` non-null so the HTTP-routing methods' early-return
    /// guard doesn't short-circuit before the HTTP call.
    /// </summary>
    private static void SetField(SingBoxManager m, string fieldName, object? value)
    {
        var f = typeof(SingBoxManager).GetField(fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new InvalidOperationException(
                $"SingBoxManager has no field '{fieldName}'");
        f.SetValue(m, value);
    }

    /// <summary>
    /// Invoke a private/internal method via reflection. Unwraps
    /// <see cref="TargetInvocationException"/> so inner exceptions reach
    /// the test as-is (xUnit's Assert.Throws works on the inner type).
    /// </summary>
    private static T InvokePrivate<T>(SingBoxManager m, string method, params object?[] args)
    {
        var mi = typeof(SingBoxManager).GetMethod(method,
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new InvalidOperationException(
                $"SingBoxManager has no method '{method}'");
        try
        {
            return (T)mi.Invoke(m, args)!;
        }
        catch (TargetInvocationException tie) when (tie.InnerException is not null)
        {
            throw tie.InnerException;
        }
    }

    /// <summary>
    /// Spawn a long-lived child process so reflection can poke <c>_process</c>
    /// non-null + <c>HasExited == false</c> WITHOUT touching the test host
    /// itself (a Stop call against the test host would kill xUnit).
    ///
    /// <para>Cross-platform: Windows uses <c>ping</c> with 60 echo requests
    /// (~ 60 s lifetime); Linux/macOS uses <c>sleep 60</c>. The returned
    /// <see cref="IDisposable"/> kills the child on dispose so the test
    /// teardown doesn't leak a worker process.</para>
    /// </summary>
    private static (Process p, IDisposable cleanup) SpawnLongLivedChild()
    {
        ProcessStartInfo psi = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new ProcessStartInfo("cmd.exe", "/c ping 127.0.0.1 -n 60 > NUL")
            : new ProcessStartInfo("/bin/sh", "-c \"sleep 60\"");
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;

        var p = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to spawn test child process");

        var cleanup = new ChildCleanup(p);
        return (p, cleanup);
    }

    private sealed class ChildCleanup : IDisposable
    {
        private readonly Process _p;
        private bool _disposed;
        public ChildCleanup(Process p) { _p = p; }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                if (!_p.HasExited) _p.Kill(entireProcessTree: true);
            }
            catch { /* best-effort */ }
            try { _p.Dispose(); } catch { /* best-effort */ }
        }
    }

    // ─── 1. Construction state ──────────────────────────────────────────

    [Fact]
    public void Construction_InitialState_IsStopped()
    {
        // Pin: a fresh manager has State=Stopped before any Start. Any
        // refactor that defers state init (e.g. lazy field setup) and
        // accidentally leaves State at the default(SingBoxState) MIGHT
        // also be Stopped == 0, so this also pins the enum ordering.
        using var manager = BuildManager(new FakeHttpClient());

        Assert.Equal(SingBoxState.Stopped, manager.State);
    }

    [Fact]
    public void Construction_Pid_ReturnsNull_WhenProcessIsNull()
    {
        // Pin: Pid property is null-safe — returns null when _process is
        // null (the "never started" case) AND when _process.HasExited
        // (the "post-crash" case). The CLI's StateFile writer relies on
        // this contract to wipe its persisted PID after a crash.
        using var manager = BuildManager(new FakeHttpClient());

        Assert.Null(manager.Pid);
    }

    // ─── 2. IsRunning / IsHealthy / GetMetrics defaults ─────────────────

    [Fact]
    public void IsRunning_OnWindows_WithoutStart_ReturnsFalse()
    {
        // Windows branch: IsRunning gates on State != Running first, so
        // even with a wonky Clash API stub, an idle manager returns
        // false. The Unix branch trusts the Clash API exclusively (see
        // v2.21.5 lesson in the doc comment) — pin that gating on the
        // OS we run unit tests on.
        if (!OperatingSystem.IsWindows()) return; // Linux/Mac branch covered by IsClashApiAlive tests

        using var manager = BuildManager(new FakeHttpClient());

        Assert.False(manager.IsRunning());
    }

    [Fact]
    public void IsHealthy_NullProcess_ReturnsFalse()
    {
        // Pin: IsHealthy short-circuits to false when _process is null
        // (the "never started" case). Without this guard the next line
        // (_process.Refresh()) would NRE on first call.
        if (!OperatingSystem.IsWindows()) return; // macOS branch checks Clash API, Linux not gated here

        using var manager = BuildManager(new FakeHttpClient());

        Assert.False(manager.IsHealthy());
    }

    [Fact]
    public void GetMetrics_NullProcess_ReturnsEmptyMetrics()
    {
        // Pin: GetMetrics returns a default ProcessMetrics (all zeros)
        // when _process is null. The UI's metrics-display loop calls
        // GetMetrics every ~1 s; pre-pinning the null-process branch
        // guarantees no NRE when sing-box hasn't started.
        using var manager = BuildManager(new FakeHttpClient());

        var metrics = manager.GetMetrics();

        Assert.NotNull(metrics);
        Assert.Equal(0, metrics.MemoryMb);
        Assert.Equal(TimeSpan.Zero, metrics.CpuTime);
        Assert.Null(metrics.StartTime);
    }

    // ─── 3. Idle Stop is no-op + idempotent ─────────────────────────────

    [Fact]
    public void Stop_OnIdleManager_IsNoOp_StateStaysStopped()
    {
        // Pin: Stop on a manager that never Started is safe. The Windows
        // path falls into the `_process == null || HasExited` cleanup
        // branch (line 221) and exits early. State stays Stopped.
        //
        // Windows-only because the Linux path shells out via
        // LinuxStopEscalationChain — external-process-heavy, intractable
        // for a unit test without an IProcessRunner seam.
        if (!OperatingSystem.IsWindows()) return;

        using var manager = BuildManager(new FakeHttpClient());

        manager.Stop();

        Assert.Equal(SingBoxState.Stopped, manager.State);
    }

    [Fact]
    public void Stop_IsIdempotent_SecondCallDoesNotThrow()
    {
        // Pin: calling Stop twice in a row is safe. The cleanup branch
        // is null-safe on _process and the orphan-adapter netsh call is
        // best-effort (try-wrapped). UI cleanup paths sometimes fire
        // Stop twice (window close + autostart cleanup) — both calls
        // must complete cleanly.
        if (!OperatingSystem.IsWindows()) return;

        using var manager = BuildManager(new FakeHttpClient());

        manager.Stop();
        manager.Stop();

        Assert.Equal(SingBoxState.Stopped, manager.State);
    }

    // ─── 4. Idle Dispose is no-op + idempotent ──────────────────────────

    [Fact]
    public void Dispose_OnIdleManager_DoesNotThrow()
    {
        // Pin: Disposing a never-Started manager is safe. Dispose calls
        // Stop internally; with _process null the Stop path is a no-op
        // and Dispose proceeds to the `_process?.Dispose()` chain.
        if (!OperatingSystem.IsWindows()) return;

        var manager = BuildManager(new FakeHttpClient());
        manager.Dispose();
        // No throw = pass.
        Assert.Equal(SingBoxState.Stopped, manager.State);
    }

    [Fact]
    public void Dispose_IsIdempotent_SecondCallIsNoOp()
    {
        // Pin: the `_disposed` guard at the top of Dispose() catches
        // a double-dispose. The `using` lifecycle in some VM paths
        // races with explicit Dispose() calls from autostart handlers;
        // both must complete without exception.
        if (!OperatingSystem.IsWindows()) return;

        var manager = BuildManager(new FakeHttpClient());
        manager.Dispose();
        manager.Dispose();

        Assert.Equal(SingBoxState.Stopped, manager.State);
    }

    // ─── 5. IsClashApiAlive HTTP routing ────────────────────────────────

    [Fact]
    public void IsClashApiAlive_HttpStub200_ReturnsTrue()
    {
        // Phase 3G-2 wire contract: IsClashApiAlive GETs
        // http://{ClashApi}/configs and trusts a 2xx response. The
        // FakeHttpClient.Setup(url, body) shorthand returns 200 by
        // default. The captured request lets us verify the URL shape.
        var http = new FakeHttpClient()
            .Setup("127.0.0.1:9090/configs", "{}");
        using var manager = BuildManager(http);

        var result = InvokePrivate<bool>(manager, "IsClashApiAlive");

        Assert.True(result);
        // The injected client should have been used (3G-2 invariant —
        // no static-HttpClient bypass).
        Assert.Single(http.SentRequests);
        var req = http.SentRequests[0];
        Assert.Equal(HttpMethod.Get, req.Method);
        Assert.Equal("http://127.0.0.1:9090/configs", req.Uri.ToString());
    }

    [Fact]
    public void IsClashApiAlive_HttpStub500_ReturnsFalse()
    {
        // Pin: non-2xx response = Clash API alive but in a bad state =
        // treat as "not alive enough for hot-reload" = return false.
        // The 5xx case used to bubble as a successful probe in pre-
        // 3G-2 code (IsSuccess() was inverted earlier); this pin
        // protects against a re-regression.
        var http = new FakeHttpClient()
            .Setup("127.0.0.1:9090/configs",
                new HttpResponse(
                    StatusCode: 500,
                    Headers: new Dictionary<string, string>(),
                    Body: Encoding.UTF8.GetBytes("internal error"),
                    Duration: TimeSpan.FromMilliseconds(1)));
        using var manager = BuildManager(http);

        var result = InvokePrivate<bool>(manager, "IsClashApiAlive");

        Assert.False(result);
    }

    [Fact]
    public void IsClashApiAlive_TransportException_ReturnsFalse()
    {
        // Pin: HTTP transport failure (timeout, connection refused, DNS
        // miss) is treated as "Clash API not alive" without escalating.
        // The catch-all in IsClashApiAlive swallows everything and
        // returns false — important so a transient network blip doesn't
        // throw out of HealthMonitor's probe loop.
        var http = new FakeHttpClient()
            .ThrowOn("127.0.0.1:9090/configs",
                new HttpRequestException("Connection refused"));
        using var manager = BuildManager(http);

        var result = InvokePrivate<bool>(manager, "IsClashApiAlive");

        Assert.False(result);
    }

    // ─── 6. TryHotReload guard short-circuits when process is null ──────

    [Fact]
    public void TryHotReload_ProcessNull_ReturnsFalseWithoutHttpCall()
    {
        // Pin the line-551 early-return guard: when _process is null,
        // TryHotReload returns false WITHOUT issuing an HTTP call. This
        // is the crash-recovery path — pre-guard, a debounced rescan
        // landing between Crashed and our state update dumped a 20-line
        // HttpRequestException stack into the log every time.
        //
        // FakeHttpClient has NO route registered, so any accidental HTTP
        // call would throw "InvalidOperationException: no route
        // registered". The test passes when the early return prevents
        // the call entirely.
        var http = new FakeHttpClient();
        using var manager = BuildManager(http);
        // _process stays null — the guard fires.

        var result = InvokePrivate<bool>(manager, "TryHotReload");

        Assert.False(result);
        Assert.Empty(http.SentRequests);
    }

    // ─── 7. TryReloadConfigJson returns false on null process ───────────

    [Fact]
    public void TryReloadConfigJson_NullProcess_ReturnsFalse()
    {
        // Pin the public hot-reload-only entry point: with _process null,
        // it writes JSON to disk THEN returns false at the TryHotReload
        // guard. Side-effect: %ProgramData%\VPNRouter\config\current.json
        // is updated. We only assert the return value to keep the test
        // hermetic on its filesystem coupling — the path write is a
        // characterization of the current behaviour, not a desired
        // outcome.
        var http = new FakeHttpClient();
        using var manager = BuildManager(http);

        // The write touches AppPaths.ConfigDir — pre-create to keep
        // partial CI checkouts from missing the directory.
        try
        {
            Directory.CreateDirectory(VPNRouter.Core.AppPaths.ConfigDir);
        }
        catch { /* best-effort; if dir creation fails the WriteAllText fallback raises a clearer error */ }

        var result = manager.TryReloadConfigJson("{\"log\":{\"level\":\"info\"}}");

        Assert.False(result);
        // The injected client must NOT have been touched (early return
        // gate before any HTTP call).
        Assert.Empty(http.SentRequests);
    }

    // ─── 8. IHttpClient injection regression pin (3G-2) ─────────────────

    [Fact]
    public void IHttpClient_IsInjected_NotStatic_RegressionPin_3G2()
    {
        // 3G-2 (commit 5370be3) replaced the per-class static readonly
        // HttpClient with an instance IHttpClient injected via ctor.
        // Pin: when a FakeHttpClient is supplied, the manager actually
        // uses it — NOT a hidden static HttpClient bypassing the seam.
        // This is the behaviour-test half of the regression pin; the
        // companion source-string pin (IHttpClient_FieldIsNonStatic
        // _SourcePin_3G2) protects against a static-field reintroduction
        // that fakes its way to using the instance ctor.
        var http = new FakeHttpClient()
            .Setup("127.0.0.1:9090/configs", "{}");
        using var manager = BuildManager(http);

        InvokePrivate<bool>(manager, "IsClashApiAlive");

        // Direct evidence that the injected fake — not a static
        // bypass — handled the call.
        Assert.NotEmpty(http.SentRequests);
        Assert.Equal("http://127.0.0.1:9090/configs",
            http.SentRequests[0].Uri.ToString());
    }

    // ─── 9. Clash API URL + body shape pin (3G-2 wire compat) ───────────

    [Fact]
    public void TryHotReload_PutShape_PreservedAfter_3G2_Migration()
    {
        // Phase 3G-2 migrated the Clash API PUT from
        // `static HttpClient.PutAsync(url, StringContent)` to the shared
        // `IHttpClient.SendAsync(HttpRequest)` seam. The migration must
        // preserve URL + body byte-for-byte — sing-box's Clash API
        // accepts ONLY `{"path":"<absolute-path>"}` JSON, anything else
        // returns 400. This pin catches a refactor that flips method to
        // POST, drops the ?force=true, or escapes the path differently.
        //
        // Phase 3+ (2026-05-21) IProcessRunner adoption: the legacy
        // `_process` Process field is gone — poke `_handle` with a
        // FakeProcessHandle (HasExited=false by default) so the line-551
        // guard doesn't short-circuit before reaching the HTTP layer.
        var http = new FakeHttpClient()
            .Setup("127.0.0.1:9090/configs",
                new HttpResponse(
                    StatusCode: 200,
                    Headers: new Dictionary<string, string>(),
                    Body: Array.Empty<byte>(),
                    Duration: TimeSpan.FromMilliseconds(1)));
        using var manager = BuildManager(http);

        var fakeHandle = new FakeProcessHandle(pid: 42424);
        try
        {
            SetField(manager, "_handle", fakeHandle);
            SetField(manager, "_currentConfigPath", @"C:\fake\current.json");

            var result = InvokePrivate<bool>(manager, "TryHotReload");

            Assert.True(result);
            Assert.Single(http.SentRequests);

            var req = http.SentRequests[0];
            Assert.Equal(HttpMethod.Put, req.Method);
            Assert.Equal("http://127.0.0.1:9090/configs?force=true",
                req.Uri.ToString());
            Assert.Equal("application/json", req.BodyContentType);
            Assert.NotNull(req.Body);
            // Body: {"path":"C:\\fake\\current.json"} — backslashes
            // are escaped in the JSON string (sing-box parses JSON
            // before passing to its path loader on Windows).
            var bodyJson = Encoding.UTF8.GetString(req.Body!);
            Assert.Contains("\"path\":", bodyJson);
            Assert.Contains("C:\\\\fake\\\\current.json", bodyJson);
        }
        finally
        {
            // CRITICAL: clear the _handle field BEFORE the manager's
            // `using` disposes. The fake handle is harmless on Dispose
            // (no real process to kill) but clearing keeps the
            // Stop's branch falling to the cleanup-only path which is
            // hermetic.
            SetField(manager, "_handle", null);
            fakeHandle.Dispose();
        }
    }

    // ─── 10. Source-string pins (intentional-stop ordering moved to
    //         ProcessHandleDisposeOrderingTests after Phase 3+ migration;
    //         3G-2 IHttpClient field shape stays here) ────────────────────

    // Note: the v2.35.x-era `Stop_DisablesEventsBeforeKill_SourcePin` test
    // is gone — the EnableRaisingEvents=false-before-Kill pattern moved
    // out of SingBoxManager.cs into ProcessHandle.Dispose
    // (ProcessRunner.cs:288-290) during the Phase 3+ IProcessRunner
    // migration. The pin lives in
    // <see cref="ProcessHandleDisposeOrderingTests"/> now — cleaner
    // separation of concerns, since the invariant belongs to the seam
    // implementation, not to one of its consumers.

    [Fact]
    public void Restart_PreservesTunLock_SourcePin()
    {
        // The Restart() method MUST call StopInternal(releaseLock: false)
        // — NOT Stop() — so the TUN ownership semaphore isn't released
        // during the brief Stop→LaunchProcess window. Without this,
        // another VPNRouter instance (e.g. the Service while UI is
        // restarting) could slip in and grab the TUN adapter, leaving
        // the UI's Restart() to fail with TunOwnershipException.
        var src = LoadSingBoxManagerSource();
        Assert.SkipUnless(src != null, "SingBoxManager.cs source not reachable from test cwd — source-pin skipped");

        // Locate the Restart method body.
        var restartIdx = src!.IndexOf("public void Restart()",
            StringComparison.Ordinal);
        Assert.True(restartIdx >= 0, "Source must contain 'public void Restart()'");

        // Within the next ~2 KB (the Restart body fits comfortably),
        // there must be a StopInternal(releaseLock: false) call —
        // the public Stop() would release the lock.
        var restartTail = src.Substring(restartIdx,
            Math.Min(2000, src.Length - restartIdx));
        Assert.Contains("StopInternal(releaseLock: false)", restartTail);
        // Belt-and-braces: Restart must NOT call the public Stop() which
        // forces lock release.
        Assert.DoesNotContain("Stop();", restartTail);
    }

    [Fact]
    public void IHttpClient_FieldIsNonStatic_SourcePin_3G2()
    {
        // Source-string regression pin for the Phase 3G-2 IHttpClient
        // migration. Companion to the behaviour test
        // (IHttpClient_IsInjected_NotStatic_RegressionPin_3G2).
        //
        // Pre-3G-2 the class held a per-class `private static readonly
        // HttpClient _http = new(...)` field. Anyone re-adding a static
        // HTTP client field would bypass the IHttpClient seam and
        // re-introduce the test gap. Pin the field declaration shape:
        //   - Must be `private readonly IHttpClient _http;` (no static).
        //   - Must NOT contain `static readonly HttpClient`.
        //   - Must NOT contain `static readonly IHttpClient`.
        var src = LoadSingBoxManagerSource();
        Assert.SkipUnless(src != null, "SingBoxManager.cs source not reachable from test cwd — source-pin skipped");

        // Strip line comments so commentary doesn't muddy the match.
        var stripped = StripLineComments(src!);

        Assert.Contains("private readonly IHttpClient _http;", stripped);
        Assert.DoesNotContain("static readonly HttpClient", stripped);
        Assert.DoesNotContain("static readonly IHttpClient", stripped);
        // Belt-and-braces: also no `static HttpClient` field of any kind.
        Assert.DoesNotContain("private static HttpClient", stripped);
    }

    // ─── helpers (source-pin loader, mirrored from sibling tests) ───────

    /// <summary>Load SingBoxManager.cs source for source-string pinning.
    /// Returns null on partial CI checkouts.</summary>
    private static string? LoadSingBoxManagerSource()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        for (var i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(
                dir.FullName,
                "VPNRouter.Core", "Services", "SingBoxManager.cs");
            if (File.Exists(candidate)) return SingBoxSourceText.ReadAll(candidate);
        }
        return null;
    }

    /// <summary>Strip // line comments so commentary about a pattern
    /// doesn't fool Contains/DoesNotContain into reporting a phantom
    /// match.</summary>
    private static string StripLineComments(string src)
    {
        return string.Join('\n',
            src.Split('\n').Select(l =>
                l.Contains("//") ? l[..l.IndexOf("//")] : l));
    }
}
