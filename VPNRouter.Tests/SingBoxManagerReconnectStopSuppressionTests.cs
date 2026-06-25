// v2.41.2-r4 (2026-06-09 — reconnect-stop false-crash suppression) pins.
//
// Bug report: Pavel's diagnostics 2026-06-09 (running v2.41.2-r1) showed that
// switching subscription / VLESS server from the GUI logged a misleading
//   [ERR] [SingBoxManager] sing-box crashed (exit code: -1)
// on EVERY server switch — and could fire the Crashed event, prompting
// HealthMonitor to launch a redundant recovery restart on top of the reconnect
// (churn + a brief extra outage).
//
// Root cause: switching server → MainWindowViewModel.ReconnectAsync stops the
// old sing-box (VpnEngine.Stop → SingBoxManager.Stop → StopInternal) and starts
// a fresh one (VpnEngine.StartAsync) — i.e. Stop()+Start, NOT Restart(). The
// intentional Windows Kill exits with code -1. The existing belt-and-braces
// guard (v2.37.0-r52) only converts that -1 into an "expected exit" INF line +
// suppresses Crashed when `_restartInProgress` is true — and that flag is set
// ONLY inside SingBoxManager.Restart(). The reconnect path never sets it, so
// when SuppressExitedEvent loses its ~14-33ms race, the late Exited callback
// fell through to the ERR "crashed" branch and fired Crashed.
//
// Fix (extends, not fights, the _restartInProgress design): a sibling
// `_stopInProgress` volatile flag set across StopInternal's kill+wait+cleanup
// body. OnProcessExited's suppression guard becomes
// `(_restartInProgress || _stopInProgress) && exitCode in {-1,137,143}`, so an
// intentional Stop is recognised too. The flag is true ONLY during a stop, so
// it cannot mask a GENUINE crash (process dies on its own → no teardown in
// flight → both flags false → Crashed still fires + HealthMonitor still
// recovers). The exit-code gate is a second discriminator (a real sing-box
// FATAL exits with code 1, never the Kill-signal codes).
//
// What this file pins:
//   Source (OS-agnostic, runs on Linux CI):
//     1. `_stopInProgress` declared volatile.
//     2. StopInternal sets it true BEFORE the first Kill.
//     3. StopInternal clears it in the finally (paired with the _stopState reset).
//     4. OnProcessExited's suppression guard ORs-in `_stopInProgress` and still
//        gates on the -1/137/143 intentional-kill exit codes.
//   Behavioural (Windows-gated — StartWithJson uses the Windows TUN-lock +
//   netsh pre-launch cleanup; mirrors SingBoxManagerProcessRunnerTests):
//     5. A reconnect-stop with the late-Exited race LOST does NOT fire Crashed
//        and logs the "expected exit" line at INF (not ERR "crashed").
//     6. A GENUINE crash (exit 1, no teardown in flight) STILL fires Crashed +
//        State=Failed + logs ERR "crashed" — the HealthMonitor recovery trigger.
//     7. A spontaneous -1 with NO teardown in flight (e.g. Task-Manager kill)
//        STILL fires Crashed — proving the exit code alone is never suppressed.
//
// Brief: continuation of the brat (v2.36.0-r4 SuppressExitedEvent) / ekko
// (v2.37.0-r52 _restartInProgress) intentional-stop regression lineage.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// Pins the v2.41.2-r4 reconnect-stop false-crash suppression. See file-header.
/// </summary>
public sealed class SingBoxManagerReconnectStopSuppressionTests
{
    // ─── Source pins (OS-agnostic — run on Linux CI) ─────────────────────────

    [Fact]
    public void Source_StopInProgressFlag_DeclaredVolatile()
    {
        // Read on the ThreadPool dispatcher thread (OnProcessExited), written on
        // the caller thread (StopInternal). Volatile gives the cross-thread
        // memory-barrier without a lock — same rationale as _restartInProgress.
        var src = ReadSingBoxManagerSource();
        Assert.Contains("private volatile bool _stopInProgress", src);
    }

    [Fact]
    public void Source_StopInternal_SetsStopInProgressTrueBeforeFirstKill()
    {
        // The flag must be set BEFORE any Kill — once Kill runs the OS may
        // dispatch Exited concurrently, so the flag-write has to win by being
        // first. Setting it right after the concurrent-stop guard covers the
        // whole kill+wait+cleanup body.
        var (body, _) = StopInternalBody();

        var setIdx = body.IndexOf("_stopInProgress = true", StringComparison.Ordinal);
        var firstKillIdx = body.IndexOf("Kill(entireProcessTree: true)", StringComparison.Ordinal);

        Assert.True(setIdx >= 0, "Expected `_stopInProgress = true` inside StopInternal.");
        Assert.True(firstKillIdx >= 0, "Expected a `Kill(entireProcessTree: true)` inside StopInternal.");
        Assert.True(setIdx < firstKillIdx,
            "`_stopInProgress = true` must be set BEFORE the first Kill in StopInternal — otherwise a " +
            "late OS Exited callback can race the flag-write and leak through to Crashed. " +
            $"setIdx={setIdx}, firstKillIdx={firstKillIdx}");
    }

    [Fact]
    public void Source_StopInternal_ClearsStopInProgressInFinally()
    {
        // The clear must be paired with the existing _stopState reset finally so
        // an exception mid-Stop can't leave the flag stuck TRUE — which would
        // wrongly suppress a LATER genuine crash (the exact failure mode the
        // _restartInProgress finally also guards against).
        var (body, _) = StopInternalBody();

        var clearIdx = body.IndexOf("_stopInProgress = false", StringComparison.Ordinal);
        var stopStateResetIdx = body.IndexOf("Volatile.Write(ref _stopState, 0)", StringComparison.Ordinal);

        Assert.True(clearIdx >= 0, "Expected `_stopInProgress = false` inside StopInternal's finally.");
        Assert.True(stopStateResetIdx >= 0, "Expected the `Volatile.Write(ref _stopState, 0)` finally reset.");
        Assert.True(clearIdx < stopStateResetIdx,
            "`_stopInProgress = false` should sit in the same finally as (and before) the _stopState reset, " +
            $"so both clear together at the end of a stop. clearIdx={clearIdx}, stopStateResetIdx={stopStateResetIdx}");
    }

    [Fact]
    public void Source_OnProcessExited_SuppressionGuardOrsInStopInProgress_AndKeepsKillExitCodes()
    {
        // The suppression guard must OR-in _stopInProgress so the reconnect
        // Stop() path (which never sets _restartInProgress) is covered — AND
        // still gate on the intentional-kill exit codes so a genuine FATAL
        // (exit 1) is never suppressed.
        var src = ReadSingBoxManagerSource();
        var handler = src.IndexOf("private void OnProcessExited()", StringComparison.Ordinal);
        Assert.True(handler >= 0, "OnProcessExited not found");
        var window = src.Substring(handler, Math.Min(8000, src.Length - handler));

        Assert.Contains("_restartInProgress || _stopInProgress", window);

        var guardIdx = window.IndexOf("_restartInProgress || _stopInProgress", StringComparison.Ordinal);
        var after = window.Substring(guardIdx, Math.Min(400, window.Length - guardIdx));
        Assert.Contains("-1", after);
        Assert.Contains("137", after);
        Assert.Contains("143", after);
    }

    // ─── Behavioural pins (Windows-gated — see file header) ──────────────────

    [Fact]
    public void ReconnectStop_LateExitedRaceLost_DoesNotFireCrashed_LogsExpectedExitAtInfo()
    {
        // The fix, end-to-end. Drive the EXACT reconnect teardown
        // (Stop → StopInternal: SuppressExitedEvent then Kill) with the
        // late-Exited race LOST (SimulateExitedRaceLost) so the OS callback fires
        // synchronously WHILE _stopInProgress is true — reproducing the
        // ~14-33ms window from Pavel's logs deterministically.
        if (!OperatingSystem.IsWindows()) return;

        var (logger, sink) = BuildCapturingLogger();
        var fake = new FakeProcessRunner();
        var handle = new FakeProcessHandle(pid: 42001) { SimulateExitedRaceLost = true };
        fake.OnStart(_ => true, _ => handle);

        var exe = CreateStubExe();
        try
        {
            using var manager = BuildManager(fake, exe, logger);
            var crashedCount = 0;
            manager.Crashed += (_, _) => Interlocked.Increment(ref crashedCount);

            manager.StartWithJson("{}");
            Assert.Equal(SingBoxState.Running, manager.State);

            // Reconnect server-switch teardown.
            manager.Stop();

            Assert.Equal(SingBoxState.Stopped, manager.State);

            // The fix: an intentional -1 exit during a stop is NOT a crash.
            Assert.Equal(0, crashedCount);

            var events = sink.Events;

            // No ERR "sing-box crashed (exit code ...)" line.
            Assert.DoesNotContain(events, e =>
                e.Level == LogEventLevel.Error &&
                e.MessageTemplate.Text.Contains("sing-box crashed (exit code"));

            // INF "Expected exit during intentional stop ..." line present, and
            // rendered with phase = "stop" (not "restart").
            var suppression = events.FirstOrDefault(e =>
                e.Level == LogEventLevel.Information &&
                e.MessageTemplate.Text.Contains("Expected exit during intentional") &&
                e.MessageTemplate.Text.Contains("suppressing Crashed event"));
            Assert.True(suppression != null,
                "Expected an INF 'Expected exit during intentional ... suppressing Crashed event' line.");
            // The structured {Phase} property must be "stop" (a plain reconnect
            // Stop, NOT a Restart) — assert on the property, not the rendered
            // text, to stay immune to Serilog's string-quoting in RenderMessage.
            Assert.True(suppression!.Properties.TryGetValue("Phase", out var phaseProp),
                "suppression log event must carry a {Phase} property");
            Assert.Equal("stop", (phaseProp as ScalarValue)?.Value);
            // With {Phase:l} the rendered line reads cleanly (no quotes).
            Assert.Contains("intentional stop (exit code: -1)", suppression.RenderMessage());
        }
        finally
        {
            try { File.Delete(exe); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void GenuineCrash_NoTeardownInFlight_FiresCrashed_StateFailed_LogsCrashAtError()
    {
        // The genuine-crash recovery path MUST be untouched: sing-box dies on its
        // own (FATAL exit code 1) with NO Stop/Restart in flight → Crashed fires
        // (HealthMonitor's recovery trigger) + State=Failed + ERR "crashed".
        if (!OperatingSystem.IsWindows()) return;

        var (logger, sink) = BuildCapturingLogger();
        var fake = new FakeProcessRunner();
        var handle = new FakeProcessHandle(pid: 42002); // SimulateExitedRaceLost = false
        fake.OnStart(_ => true, _ => handle);

        var exe = CreateStubExe();
        try
        {
            using var manager = BuildManager(fake, exe, logger);
            var crashedCount = 0;
            manager.Crashed += (_, _) => Interlocked.Increment(ref crashedCount);

            manager.StartWithJson("{}");

            // Spontaneous FATAL — no teardown in flight.
            handle.SignalExit(exitCode: 1);

            Assert.Equal(1, crashedCount);
            Assert.Equal(SingBoxState.Failed, manager.State);

            var events = sink.Events;
            Assert.Contains(events, e =>
                e.Level == LogEventLevel.Error &&
                e.MessageTemplate.Text.Contains("sing-box crashed (exit code"));
            // And NOT the suppression line — this was a real crash.
            Assert.DoesNotContain(events, e =>
                e.MessageTemplate.Text.Contains("Expected exit during intentional"));
        }
        finally
        {
            try { File.Delete(exe); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void ExitMinusOne_NoTeardownInFlight_StillFiresCrashed()
    {
        // Guards the dangerous over-suppression failure mode: the Kill-signal
        // exit code (-1) must NEVER be suppressed on its own. Only
        // teardown-in-flight + that code is suppressed. A spontaneous -1 (e.g.
        // user kills sing-box via Task Manager) is a real crash → Crashed must
        // fire so HealthMonitor recovers and the user isn't left offline.
        if (!OperatingSystem.IsWindows()) return;

        var (logger, sink) = BuildCapturingLogger();
        var fake = new FakeProcessRunner();
        var handle = new FakeProcessHandle(pid: 42003);
        fake.OnStart(_ => true, _ => handle);

        var exe = CreateStubExe();
        try
        {
            using var manager = BuildManager(fake, exe, logger);
            var crashedCount = 0;
            manager.Crashed += (_, _) => Interlocked.Increment(ref crashedCount);

            manager.StartWithJson("{}");

            // Spontaneous -1, NO Stop()/Restart() in flight.
            handle.SignalExit(exitCode: -1);

            Assert.Equal(1, crashedCount);
            Assert.Equal(SingBoxState.Failed, manager.State);
            Assert.Contains(sink.Events, e =>
                e.Level == LogEventLevel.Error &&
                e.MessageTemplate.Text.Contains("sing-box crashed (exit code"));
            Assert.DoesNotContain(sink.Events, e =>
                e.MessageTemplate.Text.Contains("Expected exit during intentional"));
        }
        finally
        {
            try { File.Delete(exe); } catch { /* best-effort */ }
        }
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static SingBoxManager BuildManager(IProcessRunner runner, string exePath, ILogger logger) =>
        new(new SingBoxSettings { ExecutablePath = exePath, ClashApi = "127.0.0.1:9090" },
            logger: logger,
            http: new FakeHttpClient(),
            runner: runner);

    private static string CreateStubExe()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"sbm-recon-stub-{Guid.NewGuid():N}.exe");
        File.WriteAllText(tmp, "stub");
        return tmp;
    }

    private static (ILogger logger, CapturingSink sink) BuildCapturingLogger()
    {
        var sink = new CapturingSink();
        var logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(sink)
            .CreateLogger();
        return (logger, sink);
    }

    /// <summary>Returns the StopInternal method body (bounded between its header
    /// and the following Restart() header) plus its start offset.</summary>
    private static (string body, int start) StopInternalBody()
    {
        var src = ReadSingBoxManagerSource();
        var start = src.IndexOf("private void StopInternal(bool releaseLock)", StringComparison.Ordinal);
        Assert.True(start >= 0, "StopInternal not found in SingBoxManager.cs");
        var end = src.IndexOf("public void Restart()", start, StringComparison.Ordinal);
        Assert.True(end > start, "Restart() (StopInternal end bound) not found after StopInternal");
        return (src.Substring(start, end - start), start);
    }

    private static string ReadSingBoxManagerSource() =>
        ReadSourceFile("VPNRouter.Core", "Services", "SingBoxManager.cs");

    private static string ReadSourceFile(params string[] segments)
    {
        var thisAssembly = typeof(SingBoxManager).Assembly;
        var binDir = Path.GetDirectoryName(thisAssembly.Location)!;
        var dir = new DirectoryInfo(binDir);
        while (dir != null)
        {
            var candidate = Path.Combine(new[] { dir.FullName }.Concat(segments).ToArray());
            if (File.Exists(candidate)) return ReadAllParts(candidate);
            dir = dir.Parent;
        }
        var fallback = Path.Combine(new[] { Environment.CurrentDirectory }.Concat(segments).ToArray());
        if (!File.Exists(fallback))
            throw new FileNotFoundException($"Source file not found: {string.Join("/", segments)}");
        return ReadAllParts(fallback);
    }

    // Reads the named source file PLUS any partial-class sibling files
    // (e.g. SingBoxManager.cs + SingBoxManager.CrashDetect.cs + ...) in the
    // same directory, concatenated. Keeps source-characterization assertions
    // stable across a partial-class split: the asserted method may live in any
    // partial, so we search the whole class source, not just the anchor file.
    private static string ReadAllParts(string primaryPath)
    {
        var dir = Path.GetDirectoryName(primaryPath)!;
        var stem = Path.GetFileNameWithoutExtension(primaryPath);
        var parts = Directory.GetFiles(dir, stem + "*.cs")
            .Where(p =>
            {
                var fn = Path.GetFileName(p);
                return fn == stem + ".cs" || fn.StartsWith(stem + ".", StringComparison.Ordinal);
            })
            .OrderBy(p => p, StringComparer.Ordinal);
        return string.Join("\n", parts.Select(File.ReadAllText));
    }

    /// <summary>Thread-safe in-memory Serilog sink so the behavioural tests can
    /// assert on emitted log events (level + message template).</summary>
    private sealed class CapturingSink : ILogEventSink
    {
        private readonly List<LogEvent> _events = new();
        private readonly object _gate = new();

        public void Emit(LogEvent logEvent)
        {
            lock (_gate) _events.Add(logEvent);
        }

        public IReadOnlyList<LogEvent> Events
        {
            get { lock (_gate) return _events.ToList(); }
        }
    }
}
