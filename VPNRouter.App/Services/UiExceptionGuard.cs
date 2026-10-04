using Avalonia.Threading;
using Serilog;
using VPNRouter.Core.Services;

namespace VPNRouter.App.Services;

// An exception on the UI thread (a layout or render pass, a binding, a handler) used to end the whole process: the app vanished, left the
// tunnel and its firewall rules behind and wrote nothing to the normal log. A failed frame is not worth a VPN session, so the guard logs it,
// writes a crash report (<data>\crashes\crash-*.txt, which the diagnostics bundle includes) and lets the app carry on. If exceptions keep
// coming (a broken page that fails on every frame) it stops swallowing them, so the process still ends instead of spinning.
public static class UiExceptionGuard
{
    private static readonly UiExceptionRateLimiter Limiter = new(MaxSwallowedInWindow, Window);
    private static int _installed;

    public const int MaxSwallowedInWindow = 8;
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(10);

    public static void Install()
    {
        if (Interlocked.Exchange(ref _installed, 1) == 1) return;
        Dispatcher.UIThread.UnhandledException += OnUnhandled;
    }

    // The tests that rely on UI exceptions propagating must not run with the guard installed.
    internal static void UninstallForTests()
    {
        if (Interlocked.Exchange(ref _installed, 0) == 0) return;
        Dispatcher.UIThread.UnhandledException -= OnUnhandled;
    }

    private static void OnUnhandled(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        var swallow = Limiter.TrySwallow(DateTime.UtcNow);
        try
        {
            Log.Error(e.Exception, "[UiGuard] Unhandled UI exception - {Action}", swallow ? "swallowed, the app keeps running" : "too many in a row, letting the process end");
        }
        catch
        {
            // logging must never be the reason for a second failure
        }

        CrashReporter.WriteReport(e.Exception, fatal: !swallow);
        if (swallow) e.Handled = true;
    }
}

// Allows at most `max` swallowed exceptions inside a sliding window.
public sealed class UiExceptionRateLimiter
{
    private readonly int _max;
    private readonly TimeSpan _window;
    private readonly Queue<DateTime> _recent = new();
    private readonly object _gate = new();

    public UiExceptionRateLimiter(int max, TimeSpan window)
    {
        _max = max;
        _window = window;
    }

    public bool TrySwallow(DateTime nowUtc)
    {
        lock (_gate)
        {
            while (_recent.Count > 0 && nowUtc - _recent.Peek() > _window)
                _recent.Dequeue();
            if (_recent.Count >= _max) return false;
            _recent.Enqueue(nowUtc);
            return true;
        }
    }
}
