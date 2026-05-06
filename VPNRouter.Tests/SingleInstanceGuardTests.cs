using System.IO;
using System.Linq;

namespace VPNRouter.Tests;

/// <summary>
/// v2.31.10-r1 (F-4) — SingleInstance.TryAcquireOrSignal regression pin.
///
/// <para>v2.31.7-r2 introduced the SingleInstance Mutex+pipe pattern but
/// shipped a subtle bug: <c>new Mutex(initiallyOwned: false, ...)</c>
/// creates the mutex without owning it, AND the original code only
/// called <c>WaitOne(0)</c> in the <c>!createdNew</c> branch. So the
/// FIRST instance created the mutex, never acquired ownership, and any
/// subsequent launch saw an unowned mutex, succeeded at <c>WaitOne(0)</c>,
/// and incorrectly fell through to the "first instance" code path. The
/// original got killed by <c>OrphanCleanup</c> (also fixed in this
/// release).</para>
///
/// <para>Live evidence (2026-05-06 01:34): pid 9060 was running, second
/// launch via <c>Start-Process VPNRouter.App.exe</c> resulted in pid
/// 7996 surviving and 9060 killed. Root cause traced to the conditional
/// WaitOne shape.</para>
///
/// <para>Fix: ALWAYS call <c>WaitOne(0)</c> on the freshly-created
/// Mutex. The createdNew flag is downgraded to diagnostic-only.</para>
///
/// <para>This test is a SOURCE-STRING PIN — a real behaviour test
/// would require either a sub-process spawner (heavyweight CI infra)
/// or refactoring SingleInstance to take name parameters (out of
/// scope for the F-4 fix). Source pin catches accidental regression
/// to the conditional-WaitOne shape.</para>
/// </summary>
public sealed class SingleInstanceGuardTests
{
    [Fact]
    public void TryAcquireOrSignal_AlwaysCallsWaitOne_OnFreshMutex()
    {
        var src = LoadSingleInstanceSource();
        if (src == null) return; // Source not available — partial CI checkout

        // Strip C# // comments so we don't match the explanatory text
        // describing the OLD bug shape.
        var stripped = string.Join("\n",
            src.Split('\n').Select(l =>
                l.Contains("//") ? l[..l.IndexOf("//")] : l));

        // The CALL to WaitOne must exist in actual code.
        Assert.Contains("_mutex.WaitOne(0)", stripped);

        // The DANGEROUS short-circuit pattern must NOT exist:
        //   if (!createdNew && !_mutex.WaitOne(0))
        // This pattern means WaitOne is skipped for the first instance.
        // Any equivalent (e.g. `&& createdNew == false &&`) would be a
        // separate concern but the canonical bug shape is the literal
        // form we shipped in v2.31.7-r2.
        Assert.DoesNotContain(
            "!createdNew && !_mutex.WaitOne",
            stripped);
        Assert.DoesNotContain(
            "!createdNew && !_mutex?.WaitOne",
            stripped);

        // Defensive: ensure AbandonedMutexException is handled — without
        // a catch, an abandoned-mutex from a crashed previous instance
        // would propagate as an unhandled exception during startup.
        Assert.Contains("AbandonedMutexException", stripped);
    }

    [Fact]
    public void TryAcquireOrSignal_StillSignalsExistingInstanceOnContention()
    {
        var src = LoadSingleInstanceSource();
        if (src == null) return;

        var stripped = string.Join("\n",
            src.Split('\n').Select(l =>
                l.Contains("//") ? l[..l.IndexOf("//")] : l));

        // Pin: the false-return path still calls TrySignalShow before
        // disposing — losing this would mean second-instance launches
        // can't bring-foreground the original.
        Assert.Contains("TrySignalShow(logger);", stripped);

        // Pin: false return path still disposes the mutex handle so
        // we don't leak HANDLEs across launch attempts.
        Assert.Contains("_mutex.Dispose();", stripped);
    }

    private static string? LoadSingleInstanceSource()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(
                dir.FullName, "VPNRouter.App", "Services", "SingleInstance.cs");
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
        }
        return null;
    }
}
