using System;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using VPNRouter.App.Localization;
using VPNRouter.Core.Services;

namespace VPNRouter.App.ViewModels;

public partial class MainWindowViewModel
{
    private DispatcherTimer? _runtimeStatusTimer;

    private DateTime _lastSkiaPurgeAt = DateTime.MinValue;

    private int _runtimeIdleStreak;

    private int _runtimeSkipRemaining;

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

    public string VpnBadgeText      => FormatBadgeText("VPN", VpnRuntimeStatus);
    public string ZapretBadgeText   => FormatBadgeText("Zapret", ZapretRuntimeStatus);
    public string TgProxyBadgeText  => FormatBadgeText("TgProxy", TgProxyRuntimeStatus);

    public IBrush VpnBadgeBrush     => BadgeBrush(VpnRuntimeStatus);
    public IBrush ZapretBadgeBrush  => BadgeBrush(ZapretRuntimeStatus);
    public IBrush TgProxyBadgeBrush => BadgeBrush(TgProxyRuntimeStatus);

    public string VpnBadgeTooltip     => FormatTooltip(Strings.BadgeTooltipVpn, VpnRuntimeStatus);
    public string ZapretBadgeTooltip  => FormatTooltip(Strings.BadgeTooltipZapret, ZapretRuntimeStatus);
    public string TgProxyBadgeTooltip => FormatTooltip(Strings.BadgeTooltipTgProxy, TgProxyRuntimeStatus);

    private void StartRuntimeStatusPolling()
    {
        if (_runtimeStatusTimer != null) return;

        UpdateRuntimeStatus();

        _runtimeStatusTimer = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) =>
        {
            UpdateRuntimeStatus();
            MaybePollConnStats();
        });
        _runtimeStatusTimer.Start();
    }

    private void UpdateRuntimeStatus()
    {
        try
        {
            if (_runtimeSkipRemaining > 0)
            {
                _runtimeSkipRemaining--;
                return;
            }

            var window = GetMainWindow();
            var isHidden = window != null && (!window.IsVisible || window.WindowState == WindowState.Minimized);

            var vpnRunning = RuntimeStatusDetector.IsVpnRunning();
            var zapretRunning = RuntimeStatusDetector.IsZapretRunning();

            var tgProxyRunning = false;
#if PLATFORM_WINDOWS
            var proxy = Volatile.Read(ref _tgProxy);
            tgProxyRunning = proxy?.IsRunning == true;
#endif

            if (isHidden)
            {
                _runtimeSkipRemaining = 4;
            }
            else if (vpnRunning || zapretRunning || tgProxyRunning)
            {
                _runtimeIdleStreak = 0;
                _runtimeSkipRemaining = 0;
            }
            else
            {
                _runtimeIdleStreak++;
                _runtimeSkipRemaining = _runtimeIdleStreak switch
                {
                    < 3   => 0,
                    < 6   => 1,
                    < 12  => 2,
                    _      => 3,
                };
            }

            var nextVpn      = vpnRunning    ? ComponentRuntimeStatus.Running : ComponentRuntimeStatus.Idle;
            var nextZapret   = zapretRunning ? ComponentRuntimeStatus.Running : ComponentRuntimeStatus.Idle;
            var nextTgProxy  = tgProxyRunning? ComponentRuntimeStatus.Running : ComponentRuntimeStatus.Idle;

            if (VpnRuntimeStatus != ComponentRuntimeStatus.Failed || nextVpn == ComponentRuntimeStatus.Running)
                VpnRuntimeStatus = nextVpn;

            if (ZapretRuntimeStatus != ComponentRuntimeStatus.Failed || nextZapret == ComponentRuntimeStatus.Running)
                ZapretRuntimeStatus = nextZapret;

            if (TgProxyRuntimeStatus != ComponentRuntimeStatus.Failed || nextTgProxy == ComponentRuntimeStatus.Running)
                TgProxyRuntimeStatus = nextTgProxy;

            SyncConnectedWithVpnRuntime(vpnRunning);

            if ((DateTime.UtcNow - _lastSkiaPurgeAt).TotalSeconds >= 60)
            {
                _lastSkiaPurgeAt = DateTime.UtcNow;
                try { SkiaSharp.SKGraphics.PurgeAllCaches(); }
                catch { }
            }
        }
        catch
        {
        }
    }

    public void ForceRefreshRuntimeStatus()
    {
        _runtimeIdleStreak = 0;
        _runtimeSkipRemaining = 0;
        UpdateRuntimeStatus();
    }

    private void SyncConnectedWithVpnRuntime(bool vpnRunning)
    {
        if (IsConnecting) return;

        if (vpnRunning &&
            (!IsConnected ||
             StatusText.StartsWith(Strings.FailedStartVpn, StringComparison.Ordinal)))
        {
            if (_engine.SingBoxPid != null || _engine.IsRunning)
            {
                if (!IsConnected) return;
                RestoreConnectedStatus();
                return;
            }

            IsConnected = true;
            ConnectButtonText = Strings.StopVPN;
            var configuredMode = _settings.App.ConfigMode ?? "generated";
            var configLabel = configuredMode.Equals("subscribe", StringComparison.OrdinalIgnoreCase)
                ? (IsRussian ? "подписка" : "subscribe")
                : configuredMode.Equals("generated", StringComparison.OrdinalIgnoreCase)
                    ? (IsRussian ? "ручной" : "manual")
                    : (IsRussian ? "свой" : "custom");
            var tunnelLabel = IsSplitTunnel ? (IsRussian ? "сплит" : "split") : (IsRussian ? "полный" : "full");
            var mode = $"{configLabel}/{tunnelLabel}";
            StatusText = IsRussian
                ? $"Подключено через службу [{mode}]"
                : $"Connected via service [{mode}]";
            MarkTrueSplitServiceManagedIfNeeded();
            try { StartSubRefreshTimer(); } catch { }
        }
        else if (!vpnRunning && IsConnected)
        {
            if (_lastSuccessfulConnectAt != DateTime.MinValue &&
                (DateTime.UtcNow - _lastSuccessfulConnectAt).TotalSeconds < 8)
                return;

            try
            {
                if (_engine?.IsRunning == true)
                    return;
            }
            catch { }

            IsConnected = false;
            ConnectButtonText = Strings.StartVPN;
            StatusText = Strings.NotConnected;
        }
    }

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

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void NavigateToVpn()
    {
        if (IsSimpleMode) return;
        SelectedTabIndex = (_settings.App.ConfigMode ?? "generated")
            .Equals("subscribe", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
    }

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void NavigateToZapret()
    {
        if (IsSimpleMode)
        {
            IsSimpleMode = false;
            _settings.App.UiMode = "advanced";
            SaveSettings();
        }
        SelectedTabIndex = 4;
        SelectedToolIndex = 0;
    }

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void NavigateToTgProxy()
    {
        if (IsSimpleMode)
        {
            IsSimpleMode = false;
            _settings.App.UiMode = "advanced";
            SaveSettings();
        }
        SelectedTabIndex = 4;
        SelectedToolIndex = 1;
    }
}
