#nullable enable

using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// Baseline-compatible characterization test for NIGHT-01 TgProxy process ownership.
/// Verifies that failed Stop retains exact handle and active secret when process exit
/// cannot be confirmed. Baseline Stop finally disposes and nulls the handle (expected RED).
/// TgProxyManager ctor takes only logger and runner (no AppPaths, no SafeModeStateCollection needed).
/// </summary>
public sealed class NightBaselineOwnershipCharacterizationTests
{
    [Fact]
    public void Night01_TgProxyManager_FailedStop_PreservesExactHandleAndActiveSecret()
    {
        var fakeRunner = new FakeProcessRunner();
        var logger = new LoggerConfiguration().CreateLogger();
        var sut = new TgProxyManager(logger, fakeRunner);

        const string secret = "night01-test-secret-42";
        var customHandle = new ControlledKillProcessHandle();

        SetHandle(sut, customHandle);
        SetActiveSecret(sut, secret);

        try
        {
            Assert.True(sut.IsRunning);
            Assert.Equal(123456, sut.Pid);

            sut.Stop();

            Assert.Equal(1, customHandle.KillCallCount);
            Assert.Same(customHandle, GetHandle(sut));
            Assert.Equal(secret, GetActiveSecret(sut));
            Assert.True(sut.IsRunning);
        }
        finally
        {
            SetHandle(sut, null);
            sut.Dispose();
            customHandle.Dispose();
        }
    }

    private static void SetHandle(TgProxyManager manager, IProcessHandle? handle)
    {
        typeof(TgProxyManager)
            .GetField("_handle", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(manager, handle);
    }

    private static IProcessHandle? GetHandle(TgProxyManager manager)
    {
        return (IProcessHandle?)typeof(TgProxyManager)
            .GetField("_handle", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(manager);
    }

    private static void SetActiveSecret(TgProxyManager manager, string secret)
    {
        typeof(TgProxyManager)
            .GetField("_activeSecret", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(manager, secret);
    }

    private static string? GetActiveSecret(TgProxyManager manager)
    {
        return (string?)typeof(TgProxyManager)
            .GetField("_activeSecret", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(manager);
    }

    private sealed class ControlledKillProcessHandle : IProcessHandle
    {
        public int Pid => 123456;
        public bool HasExited { get; private set; }
        public int KillCallCount { get; private set; }
        public int DisposeCallCount { get; private set; }
        public int SuppressCallCount { get; private set; }

        public event EventHandler<string>? OutputLine { add { } remove { } }
        public event EventHandler<string>? ErrorLine { add { } remove { } }
        public event EventHandler<int>? Exited { add { } remove { } }

        public Task<int> WaitForExitAsync(CancellationToken ct) => Task.FromResult(0);

        public void Kill(bool entireProcessTree = true)
        {
            KillCallCount++;
            throw new InvalidOperationException("Controlled test kill failure.");
        }

        public void SuppressExitedEvent()
        {
            SuppressCallCount++;
        }

        public ProcessSnapshot? TryGetSnapshot() => null;

        public void Dispose()
        {
            DisposeCallCount++;
        }

        public void ExplicitCleanup()
        {
            HasExited = true;
        }
    }
}
