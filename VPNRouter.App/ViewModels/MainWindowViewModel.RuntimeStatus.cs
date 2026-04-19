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
            var nextVpn = RuntimeStatusDetector.IsVpnRunning()
                ? ComponentRuntimeStatus.Running
                : ComponentRuntimeStatus.Idle;

            var nextZapret = RuntimeStatusDetector.IsZapretRunning()
                ? ComponentRuntimeStatus.Running
                : ComponentRuntimeStatus.Idle;

            var tgPort = _settings?.App?.TgProxyPort ?? 0;
            if (tgPort <= 0) tgPort = 1443;
            var nextTgProxy = RuntimeStatusDetector.IsTgProxyRunning(tgPort)
                ? ComponentRuntimeStatus.Running
                : ComponentRuntimeStatus.Idle;

            // Preserve "Failed" state until next successful detection
            if (VpnRuntimeStatus != ComponentRuntimeStatus.Failed || nextVpn == ComponentRuntimeStatus.Running)
                VpnRuntimeStatus = nextVpn;

            if (ZapretRuntimeStatus != ComponentRuntimeStatus.Failed || nextZapret == ComponentRuntimeStatus.Running)
                ZapretRuntimeStatus = nextZapret;

            if (TgProxyRuntimeStatus != ComponentRuntimeStatus.Failed || nextTgProxy == ComponentRuntimeStatus.Running)
                TgProxyRuntimeStatus = nextTgProxy;
        }
        catch
        {
            // Poll failures are non-fatal
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

    private static IBrush BadgeBrush(ComponentRuntimeStatus status) => status switch
    {
        ComponentRuntimeStatus.Running => new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81)), // emerald
        ComponentRuntimeStatus.Failed  => new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)), // red
        _                              => new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80))  // gray
    };

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
    /// Click-handler target for badges: switch to the tab that controls this component.
    /// Called from MainWindow.axaml via Command binding.
    /// </summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void NavigateToVpn() => SelectedTabIndex = 0;  // Manual/Subscribe

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void NavigateToTools() => SelectedTabIndex = 4;  // Tools tab (Zapret/TgProxy live here)
}
