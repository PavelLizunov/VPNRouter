using System;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using VPNRouter.App.Localization;
using VPNRouter.Core.Services;

namespace VPNRouter.App.ViewModels;

/// <summary>
/// Runtime status indicators for the 3 background components (VPN, Zapret, TgProxy).
/// Polled every 2 seconds via <see cref="RuntimeStatusDetector"/>. Rendered as
/// coloured badges in the MainWindow header so the user can see system state
/// at a glance without opening Tools or checking the bottom status bar.
/// </summary>
public partial class MainWindowViewModel
{
    private DispatcherTimer? _runtimeStatusTimer;

    // ── Raw status (polled) ───────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VpnBadgeText))]
    [NotifyPropertyChangedFor(nameof(VpnBadgeBrush))]
    [NotifyPropertyChangedFor(nameof(VpnBadgeTooltip))]
    private ComponentRuntimeStatus _vpnRuntimeStatus = ComponentRuntimeStatus.Idle;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ZapretBadgeText))]
    [NotifyPropertyChangedFor(nameof(ZapretBadgeBrush))]
    [NotifyPropertyChangedFor(nameof(ZapretBadgeTooltip))]
    private ComponentRuntimeStatus _zapretRuntimeStatus = ComponentRuntimeStatus.Idle;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TgProxyBadgeText))]
    [NotifyPropertyChangedFor(nameof(TgProxyBadgeBrush))]
    [NotifyPropertyChangedFor(nameof(TgProxyBadgeTooltip))]
    private ComponentRuntimeStatus _tgProxyRuntimeStatus = ComponentRuntimeStatus.Idle;

    // ── Display properties (computed from status enum + IsRussian) ────────

    public string VpnBadgeText      => FormatBadgeText("VPN", VpnRuntimeStatus);
    public string ZapretBadgeText   => FormatBadgeText("Zapret", ZapretRuntimeStatus);
    public string TgProxyBadgeText  => FormatBadgeText("TgProxy", TgProxyRuntimeStatus);

    public IBrush VpnBadgeBrush     => BadgeBrush(VpnRuntimeStatus);
    public IBrush ZapretBadgeBrush  => BadgeBrush(ZapretRuntimeStatus);
    public IBrush TgProxyBadgeBrush => BadgeBrush(TgProxyRuntimeStatus);

    public string VpnBadgeTooltip     => FormatTooltip(IsRussian ? "VPN" : "VPN", VpnRuntimeStatus);
    public string ZapretBadgeTooltip  => FormatTooltip(IsRussian ? "Zapret DPI bypass" : "Zapret DPI bypass", ZapretRuntimeStatus);
    public string TgProxyBadgeTooltip => FormatTooltip(IsRussian ? "Telegram proxy" : "Telegram proxy", TgProxyRuntimeStatus);

    // ── Lifecycle ─────────────────────────────────────────────────────────

    /// <summary>
    /// Start polling component status every 2 seconds. Call once from the
    /// MainWindowViewModel constructor after all dependencies are set up.
    /// </summary>
    private void StartRuntimeStatusPolling()
    {
        if (_runtimeStatusTimer != null) return;

        // Populate immediately so the badges don't flash "Idle" for 2 seconds on launch
        UpdateRuntimeStatus();

        _runtimeStatusTimer = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) =>
        {
            UpdateRuntimeStatus();
        });
        _runtimeStatusTimer.Start();
    }

    private void UpdateRuntimeStatus()
    {
        try
        {
            var vpnRunning = RuntimeStatusDetector.IsVpnRunning();
            var zapretRunning = RuntimeStatusDetector.IsZapretRunning();

            var tgPort = _settings?.App?.TgProxyPort ?? 0;
            if (tgPort <= 0) tgPort = 1443;
            var tgProxyRunning = RuntimeStatusDetector.IsTgProxyRunning(tgPort);

            var nextVpn      = vpnRunning    ? ComponentRuntimeStatus.Running : ComponentRuntimeStatus.Idle;
            var nextZapret   = zapretRunning ? ComponentRuntimeStatus.Running : ComponentRuntimeStatus.Idle;
            var nextTgProxy  = tgProxyRunning? ComponentRuntimeStatus.Running : ComponentRuntimeStatus.Idle;

            // Preserve "Failed" state until next successful detection
            if (VpnRuntimeStatus != ComponentRuntimeStatus.Failed || nextVpn == ComponentRuntimeStatus.Running)
                VpnRuntimeStatus = nextVpn;

            if (ZapretRuntimeStatus != ComponentRuntimeStatus.Failed || nextZapret == ComponentRuntimeStatus.Running)
                ZapretRuntimeStatus = nextZapret;

            if (TgProxyRuntimeStatus != ComponentRuntimeStatus.Failed || nextTgProxy == ComponentRuntimeStatus.Running)
                TgProxyRuntimeStatus = nextTgProxy;

            // Sync IsConnected with real VPN state. This handles the case where
            // the Windows Service started sing-box before the desktop app opened
            // — the one-shot DetectServiceManagedVpn() in the ctor may have
            // missed it if the race was the other way. Also covers external
            // stop of sing-box (e.g. user killed it from Task Manager).
            SyncConnectedWithVpnRuntime(vpnRunning);
        }
        catch
        {
            // Poll failures are non-fatal
        }
    }

    /// <summary>
    /// Reconcile the UI's IsConnected flag (and its dependent labels) with the
    /// actual presence of a sing-box process. Called every poll tick after
    /// <see cref="UpdateRuntimeStatus"/> has refreshed the VPN badge.
    /// </summary>
    private void SyncConnectedWithVpnRuntime(bool vpnRunning)
    {
        // Don't disturb state during an explicit connect/disconnect transition
        if (IsConnecting) return;

        if (vpnRunning && !IsConnected)
        {
            IsConnected = true;
            ConnectButtonText = Strings.StopVPN;
            var configLabel = IsSubscribeMode ? "subscribe" : IsVlessMode ? "manual" : "custom";
            var tunnelLabel = IsSplitTunnel ? "split" : "full";
            var mode = $"{configLabel}/{tunnelLabel}";
            StatusText = IsRussian
                ? $"Подключено через службу [{mode}]"
                : $"Connected via service [{mode}]";
            try { StartSubRefreshTimer(); } catch { }
        }
        else if (!vpnRunning && IsConnected)
        {
            // sing-box disappeared without the app initiating a stop — reset UI
            IsConnected = false;
            ConnectButtonText = Strings.StartVPN;
            StatusText = Strings.NotConnected;
        }
    }

    // ── Formatters ────────────────────────────────────────────────────────

    private string FormatBadgeText(string name, ComponentRuntimeStatus status)
    {
        var icon = status switch
        {
            ComponentRuntimeStatus.Running => "🟢",
            ComponentRuntimeStatus.Failed  => "🔴",
            _                              => "⚪"
        };
        return $"{icon} {name}";
    }

    private static IBrush BadgeBrush(ComponentRuntimeStatus status)
    {
        // Look up from the token dictionary (Tokens.axaml) so the badge
        // automatically adapts to theme variant. Fallback to a hardcoded
        // brush if the resource isn't found (unit tests, design-time, etc).
        var key = status switch
        {
            ComponentRuntimeStatus.Running => "SuccessSolidBrush",
            ComponentRuntimeStatus.Failed  => "DangerSolidBrush",
            _                              => "TextMutedBrush"
        };

        if (Avalonia.Application.Current != null &&
            Avalonia.Application.Current.Resources.TryGetResource(
                key, Avalonia.Application.Current.ActualThemeVariant, out var res) &&
            res is IBrush brush)
        {
            return brush;
        }

        return status switch
        {
            ComponentRuntimeStatus.Running => new SolidColorBrush(Color.FromRgb(0x16, 0xA3, 0x4A)),
            ComponentRuntimeStatus.Failed  => new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26)),
            _                              => new SolidColorBrush(Color.FromRgb(0x94, 0xA0, 0xB2))
        };
    }

    private string FormatTooltip(string componentName, ComponentRuntimeStatus status)
    {
        var ru = IsRussian;
        var stateText = status switch
        {
            ComponentRuntimeStatus.Running => ru ? "работает" : "running",
            ComponentRuntimeStatus.Failed  => ru ? "ошибка запуска" : "failed to start",
            _                              => ru ? "остановлен"   : "stopped"
        };
        return $"{componentName}: {stateText}";
    }

    /// <summary>
    /// Click-handler target for the VPN badge: switch to the tab that shows the
    /// currently-active config source. If the user is running a subscription
    /// config, jump to the Subscribe tab (tab 1); otherwise land on Manual
    /// (tab 0) where both VLESS and Custom configs live.
    /// </summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void NavigateToVpn()
    {
        SelectedTabIndex = IsSubscribeMode ? 1 : 0;
    }

    /// <summary>Zapret badge click: switch to Tools tab AND select Zapret sub-section.</summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void NavigateToZapret()
    {
        SelectedTabIndex = 4;        // Tools tab
        SelectedToolIndex = 0;       // Zapret sub-section
    }

    /// <summary>TgProxy badge click: switch to Tools tab AND select TgProxy sub-section.</summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void NavigateToTgProxy()
    {
        SelectedTabIndex = 4;        // Tools tab
        SelectedToolIndex = 1;       // TgProxy sub-section
    }
}
