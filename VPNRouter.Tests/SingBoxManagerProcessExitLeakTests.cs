// v2.40.0 audit P1 (plans/bug-responsiveness-memory-audit-targets-2026-06-02.md
// "SingBoxManager регистрирует захватывающий ProcessExit lambda").
//
// Pre-fix the ctor subscribed an anonymous lambda to
// AppDomain.CurrentDomain.ProcessExit that captured `this`. Because the
// delegate was anonymous it could never be removed, so AppDomain's static
// ProcessExit invocation list kept a strong reference to every SingBoxManager
// ever constructed — disposed or not — until process exit. One manager per
// process is harmless, but a test harness or a future host-reload that
// recreates the manager would accumulate dead instances for the life of the
// process.
//
// Fix: a NAMED handler (OnAppDomainProcessExit) + an explicit
// `AppDomain.CurrentDomain.ProcessExit -= OnAppDomainProcessExit` in Dispose().
// A disposed manager is then no longer reachable from the static hook and is
// eligible for GC.
//
// This file pins both:
//   1. A behavioural WeakReference test — disposed managers are collected.
//   2. A source pin — Dispose() emits the unsubscribe (so a refactor that
//      drops it trips here).
//
// Cross-platform: ctor + Dispose are platform-neutral, so this runs everywhere.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

public sealed class SingBoxManagerProcessExitLeakTests
{
    private static SingBoxSettings BuildIdleSettings() => new()
    {
        ExecutablePath = Path.Combine(Path.GetTempPath(), "nonexistent-sing-box-for-leak-test.exe"),
    };

    // Construct + Dispose in a NoInlining helper so the JIT can't keep the
    // local alive past the method, and return only WeakReferences. After this
    // returns there is no managed strong reference to any manager except the
    // (now-removed) ProcessExit subscription — so the fix must let them die.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static List<WeakReference> CreateAndDispose(int count)
    {
        var refs = new List<WeakReference>(count);
        for (int i = 0; i < count; i++)
        {
            var mgr = new SingBoxManager(BuildIdleSettings());
            refs.Add(new WeakReference(mgr));
            mgr.Dispose();
            mgr = null;
        }
        return refs;
    }

    [Fact]
    public void DisposedManagers_AreNotRetainedByProcessExitHook()
    {
        var refs = CreateAndDispose(25);

        // Robust collection: LOOP forced gen-2 collects + finalizer drains.
        // A ProcessExit-rooted instance is a HARD root — it survives every
        // round, so a real leak still fails this. But a plain GC straggler
        // (promoted to gen2 under test-host load, awaiting a later sweep) can
        // outlive a single 2-collect pass; on a busy Windows runner this made
        // the test flake at "3-4/25 alive" even though Dispose correctly
        // unsubscribes. Looping clears the stragglers without masking a leak.
        int alive = refs.Count;
        for (int round = 0; round < 20 && alive > 0; round++)
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true);
            GC.WaitForPendingFinalizers();
            Thread.Sleep(10);
            GC.Collect(2, GCCollectionMode.Forced, blocking: true);
            alive = refs.Count(r => r.IsAlive);
        }

        Assert.True(alive == 0,
            $"{alive}/25 disposed SingBoxManager instances are still alive after a full GC. " +
            "The AppDomain.ProcessExit subscription is likely retaining them — Dispose() must " +
            "unsubscribe OnAppDomainProcessExit.");
    }

    [Fact]
    public void Source_Dispose_UnsubscribesProcessExitHandler()
    {
        // Deterministic backstop for the WeakReference test above: Dispose()
        // MUST detach the named ProcessExit handler. A refactor that drops the
        // unsubscribe (or reverts to an unremovable lambda) trips here.
        var sourcePath = FindRepoFile("VPNRouter.Core", "Services", "SingBoxManager.cs");
        Assert.True(File.Exists(sourcePath), $"SingBoxManager.cs not found at {sourcePath}");
        var source = SingBoxSourceText.ReadAll(sourcePath);

        Assert.Contains("AppDomain.CurrentDomain.ProcessExit += OnAppDomainProcessExit", source);
        Assert.Contains("AppDomain.CurrentDomain.ProcessExit -= OnAppDomainProcessExit", source);
    }

    private static string FindRepoFile(params string[] segments)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(new[] { dir.FullName }.Concat(segments).ToArray());
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        // Fall back to a path relative to the test assembly (won't exist → test asserts).
        return Path.Combine(AppContext.BaseDirectory, segments.Last());
    }
}
