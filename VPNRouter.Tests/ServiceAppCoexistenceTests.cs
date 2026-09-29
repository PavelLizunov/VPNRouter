using System.IO;
using System.Linq;
using System.Threading;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

public sealed class ServiceAppCoexistenceTests
{
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
