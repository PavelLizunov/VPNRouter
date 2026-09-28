using Serilog;

namespace VPNRouter.Core.Services;

public sealed class PowerEventListener : IDisposable
{
    private readonly Action _onWakeOrUnlock;
    private readonly ILogger _logger;
    private bool _subscribed;
    private bool _disposed;

    public PowerEventListener(Action onWakeOrUnlock, ILogger? logger = null)
    {
        _onWakeOrUnlock = onWakeOrUnlock;
        _logger = logger ?? Log.Logger;
    }

    public void Start()
    {
        if (_subscribed || _disposed) return;
        if (!OperatingSystem.IsWindows())
        {
            _logger.Debug("[PowerEventListener] Non-Windows platform — listener inactive");
            return;
        }

#if PLATFORM_WINDOWS
        try
        {
            Microsoft.Win32.SystemEvents.SessionSwitch += OnSessionSwitch;
            Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;
            _subscribed = true;
            _logger.Information("[PowerEventListener] Listening for SessionSwitch + PowerModeChanged");
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[PowerEventListener] SystemEvents subscribe failed (non-fatal)");
        }
#endif
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (!_subscribed) return;
#if PLATFORM_WINDOWS
        try
        {
            Microsoft.Win32.SystemEvents.SessionSwitch -= OnSessionSwitch;
            Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[PowerEventListener] SystemEvents unsubscribe failed (non-fatal)");
        }
#endif
        _subscribed = false;
    }

#if PLATFORM_WINDOWS
    private void OnSessionSwitch(object? sender, Microsoft.Win32.SessionSwitchEventArgs e)
    {
        switch (e.Reason)
        {
            case Microsoft.Win32.SessionSwitchReason.SessionUnlock:
            case Microsoft.Win32.SessionSwitchReason.ConsoleConnect:
            case Microsoft.Win32.SessionSwitchReason.RemoteConnect:
                _logger.Information("[PowerEventListener] Session event {Reason} — probing HealthMonitor", e.Reason);
                SafeInvoke();
                break;
            default:
                _logger.Debug("[PowerEventListener] Session event {Reason} — no probe", e.Reason);
                break;
        }
    }

    private void OnPowerModeChanged(object? sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        if (e.Mode == Microsoft.Win32.PowerModes.Resume)
        {
            _logger.Information("[PowerEventListener] Power Resume — probing HealthMonitor");
            SafeInvoke();
        }
        else
        {
            _logger.Debug("[PowerEventListener] PowerMode {Mode} — no probe", e.Mode);
        }
    }
#endif

    private void SafeInvoke()
    {
        try { _onWakeOrUnlock(); }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[PowerEventListener] Wake callback raised (non-fatal)");
        }
    }
}
