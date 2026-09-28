#nullable enable

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;
using Xunit;

namespace VPNRouter.Tests;

public sealed class SingBoxManagerLifecycleStressTests : IDisposable
{
    private readonly IProcessRunner? _savedTunDiagRunner;

    public SingBoxManagerLifecycleStressTests()
    {
        var runnerProp = typeof(TunAdapterDiagnostics).GetProperty(
            "Runner", BindingFlags.NonPublic | BindingFlags.Static);
        _savedTunDiagRunner = runnerProp?.GetValue(null) as IProcessRunner;

        var fakeDiagRunner = new FakeProcessRunner()
            .OnRun(_ => true, new ProcessResult(
                ExitCode: 0, Stdout: string.Empty, Stderr: string.Empty,
                Duration: TimeSpan.Zero, TimedOut: false));
        runnerProp?.SetValue(null, fakeDiagRunner);
    }

    public void Dispose()
    {
        var runnerProp = typeof(TunAdapterDiagnostics).GetProperty(
            "Runner", BindingFlags.NonPublic | BindingFlags.Static);
        if (_savedTunDiagRunner != null)
            runnerProp?.SetValue(null, _savedTunDiagRunner);

        try
        {
            var tunLock = TunOwnershipLock.Instance(null);
            tunLock.Release();
            tunLock.Dispose();
        }
        catch { }
    }

    [Fact]
    public void Stress_RepeatedConcurrentStopStorms_TunLockStaysBalanced()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(),
            "Windows-only — the graceful-Kill + TUN-orphan teardown paths are " +
            "the Windows branch; Linux/macOS go through pkexec/sudo not routed " +
            "through the IProcessRunner seam.");

        EnsureConfigDir();

        var runner = new FakeProcessRunner()
            .OnStart(_ => true, _ => new FakeProcessHandle(NewFakePid()));
        using var mgr = new SingBoxManager(
            DefaultSettings(), logger: null, http: new FakeHttpClient(), runner: runner);

        var lockInstance = TunOwnershipLock.Instance(null);
        if (IsLockOwned(lockInstance)) lockInstance.Release();

        const int storms = 120;
        const int threadsPerStorm = 8;

        for (int s = 0; s < storms; s++)
        {
            Assert.True(lockInstance.TryAcquire(),
                $"storm {s}: could not acquire the singleton TUN lock — the " +
                $"previous storm's concurrent Stop() leaked it (failed to " +
                $"release under contention). B2 guard or a releaseLock-gated " +
                $"release site regressed.");
            SetField(mgr, "_ownsTunLock", true);

            var handle = new FakeProcessHandle(NewFakePid());
            SetField(mgr, "_handle", handle);

            using var barrier = new Barrier(threadsPerStorm);
            var tasks = Enumerable.Range(0, threadsPerStorm)
                .Select(_ => Task.Run(() =>
                {
                    barrier.SignalAndWait();
                    mgr.Stop();
                }))
                .ToArray();

            Assert.True(Task.WaitAll(tasks, TimeSpan.FromSeconds(10)),
                $"storm {s}: concurrent Stop() storm did not complete within 10s — deadlock.");

            Assert.False(IsLockOwned(lockInstance),
                $"storm {s}: the TUN lock is still owned after a concurrent " +
                $"Stop() storm — no thread released it (or all releases were " +
                $"swallowed). Public Stop() must release (releaseLock=true).");
            Assert.Equal(SingBoxState.Stopped, mgr.State);

            SetField(mgr, "_handle", null);
        }
    }

    private static SingBoxSettings DefaultSettings() => new()
    {
        ExecutablePath = @"C:\nonexistent\sing-box.exe",
        ClashApi = "127.0.0.1:9090",
    };

    private static bool IsLockOwned(TunOwnershipLock lockInstance)
    {
        var f = typeof(TunOwnershipLock).GetField("_owned",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(f);
        return (bool)f!.GetValue(lockInstance)!;
    }

    private static int _fakePidCounter = 90000;
    private static int NewFakePid() => Interlocked.Increment(ref _fakePidCounter);

    private static void EnsureConfigDir()
    {
        try { Directory.CreateDirectory(VPNRouter.Core.AppPaths.ConfigDir); } catch { }
    }

    private static void SetField(SingBoxManager m, string fieldName, object? value)
    {
        var f = typeof(SingBoxManager).GetField(fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new InvalidOperationException($"SingBoxManager has no field '{fieldName}'");
        f.SetValue(m, value);
    }
}
