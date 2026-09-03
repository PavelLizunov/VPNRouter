// v2.36.0-r4 (brat 2026-05-24 — intentional-stop regression fix) pins.
//
// Bug report: brat's vpnrouter20260524_001.log at 12:11:49 showed
//   12:11:49.050 [INF] [SingBoxManager] Stopping sing-box (PID 28116)
//   12:11:49.064 [ERR] [SingBoxManager] sing-box crashed (exit code: -1)
// 14ms between intentional Stop and a FALSE "sing-box crashed" event.
// Same pattern repeated several times across 2026-05-23 / 2026-05-24.
//
// Root cause: Phase 3+ IProcessRunner refactor (commit 2026-05-21)
// moved the EnableRaisingEvents=false-before-Kill pattern from
// SingBoxManager into ProcessHandle.Dispose. But SingBoxManager.StopInternal
// calls _handle.Kill() WITHOUT first calling Dispose — Dispose runs in
// the finally block AFTER WaitForExit, by which point the OS has
// already raised Exited and the C# event handler has fired
// "sing-box crashed". The Phase 3+ comment claiming
// "Kill→WaitForExit→Dispose preserves intent" was wrong (no Dispose
// before Exited fires).
//
// Fix: added IProcessHandle.SuppressExitedEvent() which production
// ProcessHandle implements by calling _process.EnableRaisingEvents
// = false. SingBoxManager.StopInternal calls it BEFORE Kill on both
// the Windows graceful path and the Linux capability-mode path.
//
// What this file pins:
//   1. SuppressExitedEvent on IProcessHandle exists + works
//   2. Source pin — SingBoxManager.StopInternal calls SuppressExitedEvent
//      before Kill in both platform paths
//   3. Behavioural — after SuppressExitedEvent + Kill via fake runner,
//      Exited event does NOT fire even though SignalExit was called.
//
// Brief: discovered while analysing brat's Z:\brat logs 2026-05-24.
// Pre-existing bug found via field log review, not via audit agent.

#nullable enable

using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// Pins the v2.36.0-r4 intentional-stop regression fix. See file-header.
/// </summary>
public sealed class SingBoxManagerSuppressExitedEventTests
{
    [Fact]
    public void Source_StopInternal_CallsSuppressExitedEventBeforeKill_WindowsPath()
    {
        // Source pin: the Windows graceful path inside StopInternal must
        // call SuppressExitedEvent BEFORE Kill. A refactor that reorders
        // (or drops the suppression) would trip this test and signal the
        // regression returning.

        var src = ReadSourceFile("VPNRouter.Core", "Services", "SingBoxManager.cs");

        // Find the substring around the Windows graceful Kill site (the
        // line that calls `_handle.Kill(entireProcessTree: true)` — first
        // null-forgiving is on SuppressExitedEvent; the chain uses `_handle.`
        // afterward since the compiler knows it's non-null). The suppression
        // call must appear textually before the Kill.
        var suppressIdx = src.IndexOf("_handle!.SuppressExitedEvent()", StringComparison.Ordinal);
        var killIdx = src.IndexOf("_handle.Kill(entireProcessTree: true)", suppressIdx + 1, StringComparison.Ordinal);

        Assert.True(suppressIdx >= 0, "Expected `_handle!.SuppressExitedEvent()` in SingBoxManager.cs (Windows graceful path)");
        Assert.True(killIdx >= 0, "Expected `_handle.Kill(entireProcessTree: true)` after SuppressExitedEvent in SingBoxManager.cs (Windows graceful path)");
        Assert.True(suppressIdx < killIdx,
            "SuppressExitedEvent must be called BEFORE Kill in the Windows graceful Stop path. " +
            $"suppressIdx={suppressIdx}, killIdx={killIdx} — wrong order would re-introduce brat's false-Crashed regression.");
    }

    [Fact]
    public void Source_StopInternal_CallsSuppressExitedEventBeforeKill_LinuxPath()
    {
        // Source pin: the Linux capability-mode path inside StopInternal
        // must also call SuppressExitedEvent BEFORE Kill. Mirror of the
        // Windows test above.

        var src = ReadSourceFile("VPNRouter.Core", "Services", "SingBoxManager.cs");

        // The Linux capability path captures the exact handle locally so a
        // concurrent lifecycle cannot replace the signal target. Pin both
        // calls on that captured target and their ordering.
        var linuxBranch = src.IndexOf("v2.28.0: Linux capability-mode path", StringComparison.Ordinal);
        Assert.True(linuxBranch >= 0, "Linux capability path landmark missing");

        // Take a window from the Linux branch up to the next "catch (Exception"
        // — should be ~80 lines.
        var nextCatch = src.IndexOf("catch (Exception ex)", linuxBranch, StringComparison.Ordinal);
        Assert.True(nextCatch > linuxBranch, "Linux branch catch-block not found");
        var linuxWindow = src.Substring(linuxBranch, nextCatch - linuxBranch);

        var suppressIdx = linuxWindow.IndexOf("targetHandle.SuppressExitedEvent()", StringComparison.Ordinal);
        var killIdx = linuxWindow.IndexOf("targetHandle.Kill(entireProcessTree: true)", StringComparison.Ordinal);

        Assert.True(suppressIdx >= 0, "Expected exact targetHandle.SuppressExitedEvent() in Linux capability-mode path");
        Assert.True(killIdx >= 0, "Expected exact targetHandle.Kill(entireProcessTree: true) in Linux capability-mode path");
        Assert.True(suppressIdx < killIdx,
            $"SuppressExitedEvent must be called BEFORE Kill in the Linux capability-mode Stop path. " +
            $"suppressIdx={suppressIdx}, killIdx={killIdx}.");
    }

    [Fact]
    public void IProcessHandle_HasSuppressExitedEventMethod()
    {
        // Reflection pin: the IProcessHandle interface must expose
        // SuppressExitedEvent. A refactor that drops it (or renames)
        // breaks the SingBoxManager source-pins above; this finds it
        // even if the production code uses dynamic dispatch.

        var method = typeof(IProcessHandle).GetMethod(nameof(IProcessHandle.SuppressExitedEvent));
        Assert.NotNull(method);
        Assert.Equal(typeof(void), method!.ReturnType);
        Assert.Empty(method.GetParameters());
    }

    [Fact]
    public async Task Behavioural_AfterSuppress_SignalExit_DoesNotFireExited()
    {
        // Behavioural pin: when SuppressExitedEvent is called BEFORE the
        // process exits, the subsequent Exited event is NOT raised to
        // C# subscribers. Mirrors the production OS behaviour (Process
        // .EnableRaisingEvents=false → no Exited callback).

        var handle = new FakeProcessHandle(pid: 99001);
        var exitedFired = 0;
        handle.Exited += (_, _) => Interlocked.Increment(ref exitedFired);

        handle.SuppressExitedEvent();
        Assert.Equal(1, handle.SuppressExitedEventCallCount);

        // Now signal exit — Exited handler must NOT fire because the
        // subscription was suppressed.
        handle.SignalExit(exitCode: 0);

        // Give the async machinery a tiny moment to settle (the event
        // handler runs synchronously in our fake, but be defensive).
        await Task.Delay(50);

        Assert.Equal(0, exitedFired);
        Assert.True(handle.HasExited);
    }

    [Fact]
    public async Task Behavioural_WithoutSuppress_SignalExit_DoesFireExited()
    {
        // Inverse: without SuppressExitedEvent, SignalExit DOES fire the
        // Exited event. Pins that the suppression IS the gate (vs. just
        // a broken event handler).

        var handle = new FakeProcessHandle(pid: 99002);
        var exitedFired = 0;
        handle.Exited += (_, _) => Interlocked.Increment(ref exitedFired);

        handle.SignalExit(exitCode: 0);

        await Task.Delay(50);

        Assert.Equal(1, exitedFired);
        Assert.True(handle.HasExited);
    }

    // v2.36.0-r5: sister-service audit found 2 more sites with the same
    // Phase 3+ regression. These pins ensure TgProxyManager + ZapretManager
    // also call SuppressExitedEvent before Kill.

    [Fact]
    public void Source_TgProxyManager_StopCallsSuppressExitedEventBeforeKill()
    {
        // v2.36.0-r5 (audit followup) — TgProxyManager.Stop wires
        // startedHandle.Exited to log "[TgProxy] Process exited" and
        // calls handle.Kill without suppression. Sibling of brat's
        // SingBoxManager regression.

        var src = ReadSourceFile("VPNRouter.Core", "Services", "TgProxyManager.cs");

        var suppressIdx = src.IndexOf("handle.SuppressExitedEvent()", StringComparison.Ordinal);
        var killIdx = src.IndexOf("handle.Kill(entireProcessTree: true)", suppressIdx + 1, StringComparison.Ordinal);

        Assert.True(suppressIdx >= 0, "Expected `handle.SuppressExitedEvent()` in TgProxyManager.cs");
        Assert.True(killIdx >= 0, "Expected `handle.Kill(entireProcessTree: true)` after Suppress in TgProxyManager.cs");
    }

    [Fact]
    public void Source_ZapretManager_StopCallsSuppressExitedEventBeforeKill()
    {
        // v2.36.0-r5 (audit followup) — ZapretManager has HIGHER user-
        // visible severity than TgProxy: its Exited handler calls
        // DetectImmediateExit which fires ImmediateExitDetected when
        // runtime<2s + exitCode!=0. False user toast "AV blocked Zapret"
        // when user themselves clicked Stop. Suppress prevents this.

        var src = ReadSourceFile("VPNRouter.Core", "Services", "ZapretManager.cs");

        var suppressIdx = src.IndexOf("handle.SuppressExitedEvent()", StringComparison.Ordinal);
        var killIdx = src.IndexOf("handle.Kill(entireProcessTree: true)", suppressIdx + 1, StringComparison.Ordinal);

        Assert.True(suppressIdx >= 0, "Expected `handle.SuppressExitedEvent()` in ZapretManager.cs");
        Assert.True(killIdx >= 0, "Expected `handle.Kill(entireProcessTree: true)` after Suppress in ZapretManager.cs");
    }

    private static string ReadSourceFile(params string[] segments)
    {
        var thisAssembly = typeof(SingBoxManager).Assembly;
        var binDir = Path.GetDirectoryName(thisAssembly.Location)!;
        var dir = new DirectoryInfo(binDir);
        while (dir != null)
        {
            var candidate = Path.Combine((new[] { dir.FullName }).Concat(segments).ToArray());
            if (File.Exists(candidate)) return ReadAllParts(candidate);
            dir = dir.Parent;
        }
        var fallback = Path.Combine((new[] { Environment.CurrentDirectory }).Concat(segments).ToArray());
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
}
