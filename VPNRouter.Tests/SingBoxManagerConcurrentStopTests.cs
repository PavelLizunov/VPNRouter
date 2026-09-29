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

public sealed class SingBoxManagerConcurrentStopTests
{
    private static SingBoxSettings BuildIdleSettings()
    {
        return new SingBoxSettings
        {
            ExecutablePath = Path.Combine(Path.GetTempPath(), "nonexistent-sing-box-for-b2-test.exe"),
        };
    }



    [Fact]
    public async Task ConcurrentStop_ManyThreads_NoExceptionThrown()
    {
        using var mgr = new SingBoxManager(BuildIdleSettings());

        const int threadCount = 100;
        using var barrier = new Barrier(threadCount);
        var tasks = new Task[threadCount];

        for (int i = 0; i < threadCount; i++)
        {
            tasks[i] = Task.Run(() =>
            {
                barrier.SignalAndWait();
                mgr.Stop();
            }, TestContext.Current.CancellationToken);
        }

        await Task.WhenAll(tasks);

        Assert.Equal(SingBoxState.Stopped, mgr.State);
    }

    [Fact]
    public void SequentialStop_AfterFirstStop_StillExecutesBody()
    {
        using var mgr = new SingBoxManager(BuildIdleSettings());

        mgr.Stop();
        Assert.Equal(SingBoxState.Stopped, mgr.State);

        mgr.Stop();
        Assert.Equal(SingBoxState.Stopped, mgr.State);

        mgr.Stop();
        Assert.Equal(SingBoxState.Stopped, mgr.State);
    }

    private static string FindRepoFile(string startDir, params string[] segments)
    {
        var dir = new DirectoryInfo(startDir);
        while (dir != null)
        {
            var candidate = Path.Combine((new[] { dir.FullName }).Concat(segments).ToArray());
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return Path.Combine((new[] { Environment.CurrentDirectory }).Concat(segments).ToArray());
    }
}
