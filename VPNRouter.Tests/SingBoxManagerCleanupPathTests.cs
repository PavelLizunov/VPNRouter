#nullable enable

using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

public sealed class SingBoxManagerCleanupPathTests
{
    private static SingBoxSettings BuildIdleSettings()
    {
        return new SingBoxSettings
        {
            ExecutablePath = Path.Combine(Path.GetTempPath(), "nonexistent-sing-box-for-b1-test.exe"),
        };
    }

    [Fact]
    public void Source_Dispose_ContainsInterlockedCompareExchange()
    {
        var sourcePath = FindRepoFile("VPNRouter.Core", "Services", "SingBoxManager.cs");
        Assert.True(File.Exists(sourcePath),
            $"SingBoxManager.cs source not found. Tried: {sourcePath}");

        var source = SingBoxSourceText.ReadAll(sourcePath);

        Assert.Contains("Interlocked.CompareExchange(ref _disposed, 1, 0)", source);
    }

    [Fact]
    public void Source_ProcessExit_GatedByVolatileRead()
    {
        var sourcePath = FindRepoFile("VPNRouter.Core", "Services", "SingBoxManager.cs");
        var source = SingBoxSourceText.ReadAll(sourcePath);

        Assert.Contains("Volatile.Read(ref _disposed)", source);
        Assert.Contains("if (Volatile.Read(ref _disposed) == 0)", source);
    }

    [Fact]
    public void Source_Dispose_StopsLeaseWithoutDisposingProcessWideLock()
    {
        var sourcePath = FindRepoFile("VPNRouter.Core", "Services", "SingBoxManager.cs");
        var source = SingBoxSourceText.ReadAll(sourcePath);
        var disposeMethodStart = source.IndexOf("public void Dispose()", StringComparison.Ordinal);
        Assert.True(disposeMethodStart >= 0, "Dispose method not found");

        var disposeBodyApprox = source.Substring(disposeMethodStart, Math.Min(1800, source.Length - disposeMethodStart));
        Assert.Contains("Stop();", disposeBodyApprox);
        Assert.DoesNotContain("_tunLock.Dispose()", disposeBodyApprox);
    }

    [Fact]
    public async Task ConcurrentDispose_ManyThreads_NoExceptionThrown()
    {
        var mgr = new SingBoxManager(BuildIdleSettings());

        const int threadCount = 50;
        using var barrier = new Barrier(threadCount);
        var tasks = new Task[threadCount];

        for (int i = 0; i < threadCount; i++)
        {
            tasks[i] = Task.Run(() =>
            {
                barrier.SignalAndWait();
                mgr.Dispose();
            });
        }

        await Task.WhenAll(tasks);

        mgr.Dispose();
    }

    [Fact]
    public void Dispose_LeavesManagerInTerminalState()
    {
        var mgr = new SingBoxManager(BuildIdleSettings());
        mgr.Dispose();

        Assert.Equal(SingBoxState.Stopped, mgr.State);

        mgr.Dispose();
        mgr.Dispose();
        mgr.Dispose();
    }

    private static string FindRepoFile(params string[] segments)
    {
        var thisAssembly = typeof(SingBoxManager).Assembly;
        var coreDir = Path.GetDirectoryName(thisAssembly.Location)!;

        var dir = new DirectoryInfo(coreDir);
        while (dir != null)
        {
            var candidate = Path.Combine((new[] { dir.FullName }).Concat(segments).ToArray());
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return Path.Combine((new[] { Environment.CurrentDirectory }).Concat(segments).ToArray());
    }
}
