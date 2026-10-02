using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using VPNRouter.App.Services;
using VPNRouter.Core;

namespace VPNRouter.Tests;

public sealed class UiExceptionGuardTests
{
    [Fact]
    public void Limiter_AllowsAFewInAWindowThenStopsSwallowing()
    {
        var limiter = new UiExceptionRateLimiter(3, TimeSpan.FromSeconds(10));
        var t0 = new DateTime(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc);

        Assert.True(limiter.TrySwallow(t0));
        Assert.True(limiter.TrySwallow(t0.AddSeconds(1)));
        Assert.True(limiter.TrySwallow(t0.AddSeconds(2)));
        Assert.False(limiter.TrySwallow(t0.AddSeconds(3)));
        Assert.False(limiter.TrySwallow(t0.AddSeconds(9)));
    }

    [Fact]
    public void Limiter_ForgetsOldExceptionsWhenTheWindowPasses()
    {
        var limiter = new UiExceptionRateLimiter(2, TimeSpan.FromSeconds(10));
        var t0 = new DateTime(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc);

        Assert.True(limiter.TrySwallow(t0));
        Assert.True(limiter.TrySwallow(t0.AddSeconds(1)));
        Assert.False(limiter.TrySwallow(t0.AddSeconds(2)));
        Assert.True(limiter.TrySwallow(t0.AddSeconds(12)));
    }

    [AvaloniaFact]
    public void Guard_KeepsTheUiThreadAliveAfterAnExceptionInAJob()
    {
        var previous = AppPaths.DataDir;
        var dataDir = Path.Combine(Path.GetTempPath(), "vpnrouter-uiguard-test-" + Guid.NewGuid().ToString("N"));
        AppPaths.OverrideDataDir(dataDir);
        UiExceptionGuard.Install();
        UiExceptionGuard.Install();
        try
        {
            var after = false;
            Dispatcher.UIThread.Post(() => throw new InvalidOperationException("Visual was invalidated during the render pass (test)"));
            Dispatcher.UIThread.Post(() => after = true);
            Dispatcher.UIThread.RunJobs();

            Assert.True(after, "a job queued after the failing one must still run");
            Assert.NotEmpty(Directory.GetFiles(Path.Combine(dataDir, "crashes"), "crash-*.txt"));
        }
        finally
        {
            UiExceptionGuard.UninstallForTests();
            AppPaths.OverrideDataDir(previous);
            try { Directory.Delete(dataDir, recursive: true); } catch { }
        }
    }
}
