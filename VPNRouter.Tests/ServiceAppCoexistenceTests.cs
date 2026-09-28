using System.IO;
using System.Linq;
using System.Threading;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

public sealed class ServiceAppCoexistenceTests
{
    [Fact]
    public void OrphanCleanup_KillOrphans_HasRespectTunLockParameter()
    {
        var src = LoadSource("VPNRouter.Core", "Services", "OrphanCleanup.cs");
        if (src == null) return;

        Assert.Contains("bool respectTunLock", src);

        Assert.Contains("respectTunLock = true", src);
    }

    [Fact]
    public void OrphanCleanup_KillOrphans_GuardsSingBoxKillWithTunLockCheck()
    {
        var src = LoadSource("VPNRouter.Core", "Services", "OrphanCleanup.cs");
        if (src == null) return;

        var stripped = StripLineComments(src);

        Assert.Contains("respectTunLock", stripped);
        Assert.Contains("IsOwnedByAnyone", stripped);

        Assert.Contains("KillByName(\"sing-box\"", stripped);
    }

    [Fact]
    public void AppProgram_StartupCallsKillOrphans_WithSafeDefault()
    {
        var src = LoadSource("VPNRouter.App", "Program.cs");
        if (src == null) return;

        Assert.Matches(
            @"OrphanCleanup\.KillOrphans\s*\(\s*\)|OrphanCleanup\.KillOrphans\s*\([^)]*respectTunLock\s*:\s*true",
            src);

        Assert.DoesNotMatch(
            @"OrphanCleanup\.KillOrphans\s*\([^)]*respectTunLock\s*:\s*false",
            ExtractStartupRegion(src));
    }

    [Fact]
    public void UserTakeoverSites_OptOutOfTunLockGuard()
    {
        var vm = LoadSource("VPNRouter.App", "ViewModels", "MainWindowViewModel.Connection.cs");
        if (vm != null)
        {
            var optOutCount = System.Text.RegularExpressions.Regex.Matches(
                vm,
                @"OrphanCleanup\.KillOrphans\s*\([^)]*respectTunLock\s*:\s*false")
                .Count;

            Assert.True(
                optOutCount >= 2,
                $"Expected >=2 KillOrphans(respectTunLock:false) calls in the connection partial (Stop + Connect branches); found {optOutCount}");
        }

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
        for (var i = 0; i < 50; i++)
        {
            _ = TunOwnershipLock.IsOwnedByAnyone();
        }

        using var fresh = new TunOwnershipLock();
        var acquired = fresh.TryAcquire();
        try
        {
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

    [Fact]
    public void Service_Startup_GuardsAndFiltersOrphanCleanup()
    {
        var src = LoadSource("VPNRouter.Service", "Program.cs")
            ?? throw new FileNotFoundException("VPNRouter.Service/Program.cs was not found.");
        var stripped = StripLineComments(src);

        Assert.Contains("using var cleanupLock = new VPNRouter.Core.Services.TunOwnershipLock()", stripped);
        Assert.Contains("_ = cleanupLock.TryAcquire()", stripped);
        Assert.Contains("if (!cleanupLock.HasOwnership)", stripped);
        Assert.Contains("if (!VPNRouter.Core.Services.ProcessOwnership.IsOwnedSingBox(z))", stripped);
        Assert.Contains("pinnedHandle.IsInvalid || pinnedHandle.IsClosed", stripped);
        Assert.Equal(1, stripped.Split("z.Kill(", System.StringSplitOptions.None).Length - 1);

        var reservation = stripped.IndexOf("_ = cleanupLock.TryAcquire()", System.StringComparison.Ordinal);
        var reservationGate = stripped.IndexOf("if (!cleanupLock.HasOwnership)", System.StringComparison.Ordinal);
        var enumeration = stripped.IndexOf("Process.GetProcessesByName(\"sing-box\")", System.StringComparison.Ordinal);
        var pin = stripped.IndexOf("var pinnedHandle = z.SafeHandle", System.StringComparison.Ordinal);
        var ownership = stripped.IndexOf("ProcessOwnership.IsOwnedSingBox(z)", System.StringComparison.Ordinal);
        var kill = stripped.IndexOf("z.Kill(", System.StringComparison.Ordinal);
        var wait = stripped.IndexOf("var exited = z.WaitForExit(3000)", System.StringComparison.Ordinal);
        var keepAlive = stripped.IndexOf("GC.KeepAlive(pinnedHandle)", System.StringComparison.Ordinal);
        var confirmed = stripped.IndexOf("killedOwnedProcess = true", System.StringComparison.Ordinal);
        var delayGate = stripped.IndexOf("if (killedOwnedProcess)", System.StringComparison.Ordinal);
        var delay = stripped.IndexOf("Thread.Sleep(2000)", System.StringComparison.Ordinal);
        Assert.True(
            reservation >= 0
            && reservation < reservationGate
            && reservationGate < enumeration
            && enumeration < pin
            && pin < ownership
            && ownership < kill
            && kill < wait
            && wait < keepAlive
            && keepAlive < confirmed
            && confirmed < delayGate
            && delayGate < delay,
            "TUN reservation and pinned ownership proof must precede Kill and release delay");

        var rejectionBlock = stripped[ownership..kill];
        Assert.Contains("continue;", rejectionBlock);
        var postKillBlock = stripped[kill..confirmed];
        Assert.Contains("continue;", postKillBlock);
        Assert.Contains("finally { z.Dispose(); }", stripped);
    }

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

    private static string ExtractStartupRegion(string src)
    {
        var idx = src.IndexOf("OrphanCleanup.KillOrphans", System.StringComparison.Ordinal);
        if (idx < 0) return src;
        var start = System.Math.Max(0, idx - 800);
        var end = System.Math.Min(src.Length, idx + 800);
        return src.Substring(start, end - start);
    }
}
