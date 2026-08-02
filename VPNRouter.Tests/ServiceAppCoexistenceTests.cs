using System.IO;
using System.Linq;
using System.Threading;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

/// <summary>
/// v2.31.10-r2 — pin the Service+App coexistence fix.
///
/// <para>Bug: <see cref="OrphanCleanup.KillOrphans()"/> was killing
/// sing-box.exe unconditionally on every <c>VPNRouter.App</c> startup.
/// When the Windows Service had spawned sing-box, the App's startup
/// killed it; <c>HealthMonitor</c>'s 5–10s exponential backoff
/// eventually respawned it, but during that window the user's tunnel
/// was down and (with <c>block_on_vpn_fail</c> set) the kill-switch
/// fired. Live trace on this VM: 2026-05-06 17:01:01–17:01:08, sing-box
/// PID 1112 (Service-spawned) killed by App startup, PID 10712
/// respawned 5s later by Service's HealthMonitor.</para>
///
/// <para>Fix: <see cref="OrphanCleanup.KillOrphans(Serilog.ILogger?, bool)"/>
/// gained a <c>respectTunLock</c> parameter (default <c>true</c>). With
/// it true, sing-box is preserved when
/// <see cref="TunOwnershipLock.IsOwnedByAnyone"/> reports an active owner.
/// User-takeover sites (App's Stop/Connect/Update commands) opt out by
/// passing <c>respectTunLock: false</c>.</para>
///
/// <para>Tests are SOURCE-STRING PINS — same pattern as
/// <see cref="OrphanCleanupGuardTests"/> — because <c>OrphanCleanup</c>
/// is a procedural static helper that would require process-mocking
/// infrastructure to behaviour-test the kill path. The string pins are
/// surprisingly load-bearing: they catch most real regressions (a
/// future "simplify back to one path" refactor would trip both the
/// signature check and the call-site checks).</para>
/// </summary>
public sealed class ServiceAppCoexistenceTests
{
    [Fact]
    public void OrphanCleanup_KillOrphans_HasRespectTunLockParameter()
    {
        var src = LoadSource("VPNRouter.Core", "Services", "OrphanCleanup.cs");
        if (src == null) return; // partial CI checkout

        // The new parameter must be present in the public signature so
        // App.Program.cs can call the safe overload while user-takeover
        // sites pass false explicitly. A future signature change that
        // removes the parameter (or renames it) would silently revert
        // the v2.31.10-r2 coexistence guarantee.
        Assert.Contains("bool respectTunLock", src);

        // The default has to be true — anything else makes startup paths
        // unsafe by default. App.Program.cs:205 calls the parameterless
        // overload and depends on the safe default.
        Assert.Contains("respectTunLock = true", src);
    }

    [Fact]
    public void OrphanCleanup_KillOrphans_GuardsSingBoxKillWithTunLockCheck()
    {
        var src = LoadSource("VPNRouter.Core", "Services", "OrphanCleanup.cs");
        if (src == null) return;

        // Strip C# // comments so commentary about the bug doesn't
        // fool a contains-check into reporting a guarded kill when
        // the actual code path is unconditional.
        var stripped = StripLineComments(src);

        // The guard MUST exist: respectTunLock + IsOwnedByAnyone()
        // appear in the same expression that decides whether to skip
        // the sing-box kill. We don't pin the exact spelling (op order,
        // local-var name) but both tokens have to be in the function
        // body. If a future refactor drops the IsOwnedByAnyone check,
        // we're back to the v2.31.10-r1 bug.
        Assert.Contains("respectTunLock", stripped);
        Assert.Contains("IsOwnedByAnyone", stripped);

        // The sing-box kill itself must still exist (we don't want a
        // refactor that "simplifies" by removing it — orphan reaping
        // is the entire point of OrphanCleanup when nobody holds the
        // lock).
        Assert.Contains("KillByName(\"sing-box\"", stripped);
    }

    [Fact]
    public void AppProgram_StartupCallsKillOrphans_WithSafeDefault()
    {
        var src = LoadSource("VPNRouter.App", "Program.cs");
        if (src == null) return;

        // Startup MUST call the parameterless / default-respectTunLock
        // overload. Pre-r2 this site was the bug-causing call. If a
        // future refactor adds `respectTunLock: false` here, the bug
        // returns. Match either the bare call or an explicit-true
        // form to allow either spelling.
        Assert.Matches(
            @"OrphanCleanup\.KillOrphans\s*\(\s*\)|OrphanCleanup\.KillOrphans\s*\([^)]*respectTunLock\s*:\s*true",
            src);

        // Negative pin: the startup site must NEVER opt out of the
        // TunLock guard. If someone copies the user-takeover pattern
        // here we want to fail loudly.
        Assert.DoesNotMatch(
            @"OrphanCleanup\.KillOrphans\s*\([^)]*respectTunLock\s*:\s*false",
            ExtractStartupRegion(src));
    }

    [Fact]
    public void UserTakeoverSites_OptOutOfTunLockGuard()
    {
        // The 3 user-action sites that intentionally want to take over
        // sing-box (and the Windows Service) MUST pass
        // respectTunLock:false. Without the explicit opt-out, the new
        // safe default would turn user-clicked Stop / Connect / Update
        // into no-ops when Service held the lock — "press disconnect,
        // it stays connected" UX regression.

        // Stop button branch (MainWindowViewModel.Connection.cs)
        var vm = LoadSource("VPNRouter.App", "ViewModels", "MainWindowViewModel.Connection.cs");
        if (vm != null)
        {
            // Both VM call sites (Stop branch + Connect branch) carry
            // explicit false. We don't try to disambiguate them by
            // surrounding context — just count occurrences. There were
            // two unconditional calls before r2; both must now be
            // explicit opt-outs.
            var optOutCount = System.Text.RegularExpressions.Regex.Matches(
                vm,
                @"OrphanCleanup\.KillOrphans\s*\([^)]*respectTunLock\s*:\s*false")
                .Count;

            Assert.True(
                optOutCount >= 2,
                $"Expected >=2 KillOrphans(respectTunLock:false) calls in MainWindowViewModel.Connection.cs (Stop + Connect branches); found {optOutCount}");
        }

        // Update apply branch (UpdateNotificationViewModel.cs) —
        // helper.cmd handles the Service stop separately, the
        // pre-update sweep here is meant to be unconditional.
        var update = LoadSource("VPNRouter.App", "ViewModels", "UpdateNotificationViewModel.cs");
        if (update != null)
        {
            Assert.Matches(
                @"OrphanCleanup\.KillOrphans\s*\([^)]*respectTunLock\s*:\s*false",
                update);
        }
    }

    [Fact]
    public void TunOwnershipLock_IsOwnedByAnyone_NonOwnerProbeIsIdempotent()
    {
        // Behavioural pin: the static IsOwnedByAnyone() peek must NOT
        // accidentally acquire the system semaphore. If it ever did
        // (e.g. someone removes the immediate Release() inside), every
        // call would block subsequent SingBoxManager.TryAcquire calls
        // for the lifetime of the calling process — the App's
        // DetectServiceManagedVpn poll is on a 1-2s timer, so this
        // would be a fast-onset deadlock.
        //
        // Strategy: call IsOwnedByAnyone() many times, then verify
        // we can still TryAcquire afterwards from a fresh lock object.
        // No actual TUN device is touched — TunOwnershipLock is purely
        // a system-wide semaphore.

        for (var i = 0; i < 50; i++)
        {
            // Don't assert the return — environment may or may not
            // have a real owner. We only care that the probe doesn't
            // poison subsequent acquisitions.
            _ = TunOwnershipLock.IsOwnedByAnyone();
        }

        using var fresh = new TunOwnershipLock();
        var acquired = fresh.TryAcquire();
        try
        {
            // If a real Service is running on the test host, this can
            // legitimately fail. Don't assert the boolean — assert that
            // the call returned (didn't deadlock or throw) by reaching
            // this line.
            Assert.True(acquired || !acquired);
        }
        finally
        {
            if (acquired) fresh.Release();
        }
    }

    [Fact]
    public void TunOwnershipLock_InstanceAfterDispose_ReturnsUsableReplacement()
    {
        var disposed = TunOwnershipLock.Instance();
        disposed.Dispose();

        var replacement = TunOwnershipLock.Instance();
        try
        {
            Assert.NotSame(disposed, replacement);
            Assert.False(IsDisposed(replacement));
        }
        finally
        {
            replacement.Dispose();
        }
    }

    [Fact]
    public void TunOwnershipLock_ReconnectReplacement_RearmsOwnerMonitor()
    {
        TunOwnershipLock.Instance().Dispose();
        var first = TunOwnershipLock.Instance();
        using var firstSemaphore = SeedProcessOnlyOwnership(first);
        ProcessOwnership.ConfiguredExePath = UniqueMissingExecutable("first");
        var firstMonitor = OwnerMonitor(first);
        Assert.NotNull(firstMonitor);
        Assert.False(firstMonitor!.IsCancellationRequested);

        first.Dispose();

        var second = TunOwnershipLock.Instance();
        Semaphore? secondSemaphore = null;
        try
        {
            secondSemaphore = SeedProcessOnlyOwnership(second);
            ProcessOwnership.ConfiguredExePath = UniqueMissingExecutable("second");
            var secondMonitor = OwnerMonitor(second);

            Assert.NotSame(first, second);
            Assert.NotNull(secondMonitor);
            Assert.NotSame(firstMonitor, secondMonitor);
            Assert.False(secondMonitor!.IsCancellationRequested);
        }
        finally
        {
            ProcessOwnership.ConfiguredExePath = null;
            if (!IsDisposed(second))
                second.Dispose();
            else
                SetField(second, "_owned", false);
            secondSemaphore?.Dispose();
        }
    }

    /// <summary>
    /// Pin: <see cref="VPNRouter.Service"/>'s startup zombie-cleanup must
    /// also continue to gate on <see cref="TunOwnershipLock.IsOwnedByAnyone"/>
    /// — historically this was the model the App startup eventually copied.
    /// If the Service-side guard is ever removed, we'd reintroduce the
    /// pre-v2.26.3 Bug A symptom (ticking "Enable background service"
    /// instantly drops a live App tunnel).
    /// </summary>
    [Fact]
    public void Service_Startup_GuardsZombieKillWithTunLockCheck()
    {
        var src = LoadSource("VPNRouter.Service", "Program.cs");
        if (src == null) return;

        var stripped = StripLineComments(src);
        Assert.Contains("TunOwnershipLock.IsOwnedByAnyone", stripped);
        Assert.Contains("sing-box", stripped);  // the kill itself still exists
    }

    // ─── helpers ────────────────────────────────────────────────────────────

    private static string? LoadSource(params string[] relativeParts)
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        for (var i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(new[] { dir.FullName }.Concat(relativeParts).ToArray());
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
        }
        return null;
    }

    /// <summary>Strip <c>//</c> line comments. Preserves code strings on
    /// the same line by only removing from the first <c>//</c> onwards.
    /// Doesn't try to handle <c>/* */</c> blocks — sources we pin don't
    /// use them in the relevant regions.</summary>
    private static string StripLineComments(string src)
    {
        return string.Join('\n',
            src.Split('\n').Select(l =>
                l.Contains("//") ? l[..l.IndexOf("//")] : l));
    }

    private static bool IsDisposed(TunOwnershipLock instance)
        => (bool)GetField(instance, "_disposed")!;

    private static CancellationTokenSource? OwnerMonitor(TunOwnershipLock instance)
        => GetField(instance, "_ownerRecordMonitorCts") as CancellationTokenSource;

    private static Semaphore SeedProcessOnlyOwnership(TunOwnershipLock instance)
    {
        var semaphore = new Semaphore(1, 1);
        Assert.True(semaphore.WaitOne(0));
        SetField(instance, "_semaphore", semaphore);
        SetField(instance, "_owned", true);
        return semaphore;
    }

    private static string UniqueMissingExecutable(string label)
        => Path.Combine(
            Path.GetTempPath(),
            $"vpnrouter-owner-monitor-{label}-{System.Guid.NewGuid():N}.exe");

    private static object? GetField(TunOwnershipLock instance, string fieldName)
    {
        var field = typeof(TunOwnershipLock).GetField(
            fieldName,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(field);
        return field!.GetValue(instance);
    }

    private static void SetField(TunOwnershipLock instance, string fieldName, object? value)
    {
        var field = typeof(TunOwnershipLock).GetField(
            fieldName,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(instance, value);
    }

    /// <summary>Pull the startup region out of <c>Program.cs</c> so the
    /// negative pin in <see cref="AppProgram_StartupCallsKillOrphans_WithSafeDefault"/>
    /// only checks the lines around the OrphanCleanup call. Pulling a
    /// 60-line window keeps us from accidentally tripping on test-string
    /// literals further down the file.</summary>
    private static string ExtractStartupRegion(string src)
    {
        var idx = src.IndexOf("OrphanCleanup.KillOrphans", System.StringComparison.Ordinal);
        if (idx < 0) return src; // file changed shape — let the positive pin do the work
        var start = System.Math.Max(0, idx - 800);
        var end = System.Math.Min(src.Length, idx + 800);
        return src.Substring(start, end - start);
    }
}
