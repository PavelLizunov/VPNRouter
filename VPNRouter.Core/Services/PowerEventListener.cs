using Serilog;

namespace VPNRouter.Core.Services;

/// <summary>
/// v2.31.6-r10 (Phase D, brat user-reported recovery gap fix).
///
/// <para>Listens for Windows session/power events and forwards them as
/// a single "wake-up" callback. Pairs with
/// <see cref="HealthMonitor.ProbeNow"/> to immediately re-evaluate
/// sing-box health after the OS resumes from a state that may have
/// killed the tunnel:</para>
///
/// <list type="bullet">
///   <item>Resume from suspend (S3/S4/modern-standby).</item>
///   <item>User unlocks the workstation after a screen-lock.</item>
///   <item>Console connect (RDP session attach, fast user switch).</item>
/// </list>
///
/// <para><b>Why this is needed</b>: brat's <c>vpnrouter20260503.log</c>
/// shows multiple sing-box crashes with exit code <c>1073807364</c>
/// (= <c>STATUS_CONTROL_C_EXIT</c> = 0x40010004) — Windows console
/// events fire when the user locks the screen / suspends / logs off.
/// HealthMonitor's periodic <c>System.Threading.Timer</c> is throttled
/// or stopped during modern-standby (S0 low-power idle), so when the
/// laptop wakes, the recovery check that v2.31.5-r2 added to
/// <c>OnHealthTick</c> doesn't fire on a cadence the user notices —
/// brat saw 35-min and 9-hour gaps before manually relaunching.</para>
///
/// <para><b>Why <c>Microsoft.Win32.SystemEvents</c></b>: it manages a
/// dedicated message-pump thread internally so we don't have to set
/// one up. The event delivery happens on a threadpool thread; our
/// callback should be quick (just delegate to HealthMonitor.ProbeNow,
/// which itself respects the v2.31.6-r9 re-entry guard).</para>
///
/// <para><b>Cross-platform</b>: Windows-only (the
/// <c>Microsoft.Win32.SystemEvents</c> package is only referenced from
/// Core when building <c>net8.0</c> on a Windows host — see Core.csproj
/// conditional ItemGroup). All entry points are guarded by
/// <see cref="OperatingSystem.IsWindows"/> so the type compiles on the
/// Android target too (where the calls become no-ops).</para>
/// </summary>
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

    /// <summary>
    /// Subscribe to <c>SystemEvents.SessionSwitch</c> +
    /// <c>SystemEvents.PowerModeChanged</c>. Idempotent.
    /// No-op on non-Windows platforms.
    /// </summary>
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
            // SystemEvents subscription can fail when running under a
            // session that doesn't have a message pump (e.g. Windows
            // Service running under LocalSystem before user logon).
            // In that scenario the daemon path of HealthMonitor's
            // periodic timer is the only recovery vector — log + carry
            // on so we don't tank service startup.
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
        // Fire the wake callback for any "user is back / desktop active
        // again" reason. SessionLock means the tunnel may have just been
        // killed by a console event — but the user can't observe yet
        // (screen is locked); we wait for SessionUnlock to actually
        // trigger recovery.
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
        // Only Resume is interesting — it means the system has just
        // come back from S3/S4/modern-standby. StatusChange fires for
        // battery-level / AC-power transitions which don't affect VPN.
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
            // The callback runs on a threadpool thread; if HealthMonitor
            // happens to be mid-Stop, ProbeNow may throw on disposed
            // state. Catching here keeps SystemEvents from flagging us
            // as a faulty subscriber + getting un-registered.
            _logger.Warning(ex, "[PowerEventListener] Wake callback raised (non-fatal)");
        }
    }
}
