#if PLATFORM_WINDOWS
using Microsoft.Extensions.Logging.Abstractions;
using VPNRouter.Core.Services;
using VPNRouter.Service;
using VPNRouter.Tests.Fakes;

namespace VPNRouter.Tests;

public sealed class VPNRouterServiceShutdownTests
{
    [Theory]
    [InlineData("host", false)]
    [InlineData("application-stopping", false)]
    [InlineData("dispose", true)]
    public async Task ShutdownWaitsForOwnedExitEvenAfterHostDeadline(string entry, bool unknown)
    {
        var originalRoot = ZapretRuntimeDirectory.TestRoot;
        var root = Directory.CreateTempSubdirectory();
        ZapretRuntimeDirectory.TestRoot = () => root.FullName;
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var child = new FakeProcessHandle { RefuseKill = true, OnWaitForExit = () => waiting.TrySetResult() };
        var runner = new FakeProcessRunner().OnStart(_ => true, _ => child);
        var manager = new ZapretManager(runner: runner)
        {
            HasDriverConflict = () => false,
            ServiceStopRetryDelay = TimeSpan.Zero
        };
        using var lifetime = new CancellationTokenSource();
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new VPNRouterService(NullLogger<VPNRouterService>.Instance, new InMemorySettingsStore(),
            lifetime.Token, manager, (message, _) =>
            {
                if (message == "VPN Router service stopped") stopped.TrySetResult();
            });
        try
        {
            Directory.CreateDirectory(ZapretUpdater.BinDir);
            File.WriteAllText(ZapretUpdater.WinwsExePath, "fixture");
            manager.Start("--wf-tcp=443");
            child.ExitQueryUnavailable = unknown;
            var deadline = new CancellationToken(canceled: true);
            var shutdown = entry switch
            {
                "host" => service.StopAsync(deadline),
                "application-stopping" => lifetime.CancelAsync(),
                _ => Task.Run(service.Dispose, TestContext.Current.CancellationToken)
            };
            await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.False(shutdown.IsCompleted);
            Assert.False(stopped.Task.IsCompleted);
            Assert.True(manager.HasOwnedProcess);
            Assert.Equal(unknown ? 0 : 3, child.KillCallCount);
            Assert.Equal(0, child.DisposeCallCount);
            Assert.Throws<ObjectDisposedException>(() => manager.Start("--wf-tcp=80"));
            if (unknown)
            {
                child.SignalExit(0); // The process wait confirms exit even if status is unavailable.
            }
            else
            {
                Assert.False(child.LastKillIncludedChildren);
                child.RefuseKill = false;
                await service.StopAsync(deadline).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
                Assert.Equal(4, child.KillCallCount);
            }
            await shutdown.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.False(manager.HasOwnedProcess);
            Assert.Single(runner.StartCalls);
            Assert.Empty(runner.RunCalls);
            Assert.Equal(1, child.DisposeCallCount);
            if (entry == "host") Assert.True(stopped.Task.IsCompleted);
        }
        finally
        {
            child.SignalExit(0);
            service.Dispose();
            ZapretRuntimeDirectory.TestRoot = originalRoot;
            root.Delete(recursive: true);
        }
    }
}
#endif
