using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using System;
using VPNRouter.Core.Services;
using UiIcons = VPNRouter.Core.Services.UiIcons;

namespace VPNRouter.Android;

public partial class AndroidApp
{
    private bool _lifecycleEventsAttached;

    private async void OnConnectClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var activity = MainActivity.Instance;
        if (activity is null) return;
        if (MainActivity.IntendedConnected)
        {
            activity.RequestDisconnect();
        }
        else
        {
            var input = (_serverInput?.Text ?? string.Empty).Trim();
            var savedUrl = AndroidStorage.GetSubscriptionUrl();
            var typedSubscription = input.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                || input.StartsWith("http://", StringComparison.OrdinalIgnoreCase);
            var subscriptionUrl = typedSubscription ? input : input.Length == 0 && AndroidStorage.GetConfigMode() == "subscribe" ? savedUrl : null;
            if (!string.IsNullOrEmpty(subscriptionUrl))
            {
                var hasCurrentCache = savedUrl == subscriptionUrl && AndroidStorage.GetSubscriptions().Exists(s =>
                    s.Enabled && s.Url == subscriptionUrl && s.Servers is { Count: > 0 });
                if (!hasCurrentCache)
                {
                    if (_serverInputError != null) _serverInputError.IsVisible = false;
                    await ApplyScannedSubscriptionUrlAsync(subscriptionUrl);
                    return;
                }
            }

            if (input.Length > 0 && !string.Equals(input, AndroidStorage.SavedServerInput(), StringComparison.Ordinal))
            {
                OnSaveClicked(sender, e);
                if (_serverInputError is { IsVisible: true }) return;
            }

            if (_statusCard is not null)
            {
                _statusCard.IsError = false;
                _statusCard.IsWarn = true;
            }
            SetVpnChipState(ChipState.Connecting);
            UpdateZapretChipFromState();
            activity.RequestConnect();
        }
    }

    private void OnIntentChanged(AndroidTunnelEvent owner, bool connected) =>
        PaintCurrentTunnelState(owner, () => UpdateConnectionState(connected, owner));

    private static void PaintCurrentTunnelState(AndroidTunnelEvent owner, Action paint)
    {
        void Paint() => owner.PaintIfCurrent(MainActivity.ReadTunnelEvent, paint);
        if (Dispatcher.UIThread.CheckAccess()) Paint();
        else Dispatcher.UIThread.Post(Paint);
    }

    private void AttachLifecycleEvents()
    {
        if (_lifecycleEventsAttached) return;
        _lifecycleEventsAttached = true;
        MainActivity.IntentChanged += OnIntentChanged;
        MainActivity.TunnelErrorReported += OnTunnelErrorReported;
        MainActivity.StatsReported += OnStatsReported;
    }

    private readonly AndroidTunnelTraffic _traffic = new();
    private string? _lastStatsSubtitle;

    private void OnStatsReported(AndroidTunnelEvent owner, long sampleMs, long down, long up, int connections)
    {
        PaintCurrentTunnelState(owner, () =>
        {
            if (MainActivity.IsActivityPaused || _connectionStartedAt == null || _statusCard == null) return;
            if (_traffic.Measure(owner.Session, sampleMs, down, up) is not { } rate) return;
            var subtitle = $"↓ {HumanRate(rate.Down)}   ↑ {HumanRate(rate.Up)}   · {connections} conn";
            if (_statusCard.Subtitle != subtitle) _statusCard.Subtitle = subtitle;
            _lastStatsSubtitle = subtitle;
        });
    }

    private static string HumanRate(double bytesPerSec)
    {
        string[] u = { "B", "KB", "MB", "GB" };
        double v = bytesPerSec; int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return (i >= 2 ? v.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
                       : v.ToString("0", System.Globalization.CultureInfo.InvariantCulture)) + " " + u[i] + "/s";
    }

    private void UpdateConnectionState(bool connected)
    {
        if (MainActivity.ReadTunnelEvent() is { } owner)
            PaintCurrentTunnelState(owner, () => UpdateConnectionState(
                owner.StateEvent == null ? connected : owner.Session != null &&
                    VPNRouter.Core.Platform.Android.AndroidSingBoxRuntime.HasLiveTunnel(), owner));
    }

    private void UpdateConnectionState(bool connected, AndroidTunnelEvent owner)
    {
        if (_statusCard is null) return;
        bool sessionChanged = _traffic.SetSession(owner.Session);
        if (sessionChanged) _lastStatsSubtitle = null;

        if (connected)
        {
            _statusCard.IsOn = true;
            _statusCard.IsWarn = false;
            _statusCard.IsOff = false;
            _statusCard.Title = sessionChanged ? Localization.SimpleStatusTitleOn :
                _lastFormattedUptimeTitle ?? Localization.SimpleStatusTitleOn;
            _statusCard.Subtitle = _lastStatsSubtitle ?? Localization.SimpleStatusDescOn;
            if (_ctaConnect is not null) _ctaConnect.IsVisible = false;
            if (_ctaConnecting is not null) _ctaConnecting.IsVisible = false;
            if (_ctaDisconnect is not null) _ctaDisconnect.IsVisible = true;
            SetVpnChipState(ChipState.On);

            if (sessionChanged || _connectionStartedAt == null)
            {
                _connectionStartedAt = DateTime.UtcNow;
                _firstProbePending = true;
                _lastHealthProbeAt = DateTime.MinValue;
                _lastHealthOk = false;
            }
            _lastError = null;
            if (_statusErrorOneLiner is not null) _statusErrorOneLiner.IsVisible = false;
            _statusCard.IsError = false;
            StartDiagnosticsTimer();
            ApplyHealthCheckDisplay();

            var batteryPreviouslyPrompted = AndroidStorage.GetBatteryOptPromptShown();
            MaybePromptBatteryOptimizationExemption();
            if (batteryPreviouslyPrompted) MaybePromptAlwaysOnLockdown();
        }
        else
        {
            _statusCard.IsOn = false;
            _statusCard.IsWarn = false;
            _statusCard.IsOff = true;
            if (AndroidStorage.ResolveVpnState().State == VPNRouter.Core.Services.VpnConnectionState.Disconnected)
            {
                _lastError = null;
                _statusCard.IsError = false;
                if (_statusErrorOneLiner is not null) _statusErrorOneLiner.IsVisible = false;
            }
            _statusCard.Title = Localization.SimpleStatusTitleOff;
            _statusCard.Subtitle = Localization.SimpleStatusDescOff;
            if (_ctaConnect is not null) _ctaConnect.IsVisible = true;
            if (_ctaConnecting is not null) _ctaConnecting.IsVisible = false;
            if (_ctaDisconnect is not null) _ctaDisconnect.IsVisible = false;
            SetVpnChipState(ChipState.Off);

            _connectionStartedAt = null;
            if (_statusHealthCheck is not null) _statusHealthCheck.IsVisible = false;
            if (_lastError is null)
            {
                StopDiagnosticsTimer();
            }
            else
            {
                StartDiagnosticsTimer();
            }
        }
        UpdateZapretChipFromState();
        UpdateConfigSummary();

        ApplyAdvancedFooterConnectionState(connected);
    }

    private void SetVpnChipState(ChipState state, bool force = false)
    {
        if (_vpnChip is null) return;
        if (_vpnChipState == state && !force) return;
        _vpnChipState = state;
        if (_statusCard is not null) _statusCard.IsWarn = state == ChipState.Connecting;

        var prevVpnCts = _vpnPulseCts;
        _vpnPulseCts = null;
        try { prevVpnCts?.Cancel(); } catch { }
        prevVpnCts?.Dispose();
        _vpnChip.Opacity = 1.0;

        string bgKey, fgKey;
        switch (state)
        {
            case ChipState.On:
                bgKey = "SuccessBgBrush";
                fgKey = "SuccessFgBrush";
                break;
            case ChipState.Connecting:
                bgKey = "WarningBgBrush";
                fgKey = "WarningFgBrush";
                _vpnPulseCts = StartChipPulse(_vpnChip);
                break;
            default:
                bgKey = "SurfaceSunkenBrush";
                fgKey = "TextMutedBrush";
                break;
        }
        _vpnChip.BindToken(TextBlock.BackgroundProperty, bgKey);
        _vpnChip.BindToken(TextBlock.ForegroundProperty, fgKey);
        if (_advVpnChip is not null)
        {
            _advVpnChip.Opacity = 1.0;
            _advVpnChip.BindToken(TextBlock.BackgroundProperty, bgKey);
            _advVpnChip.BindToken(TextBlock.ForegroundProperty, fgKey);
        }
    }

    private void SetZapretChipState(ChipState state, bool force = false)
    {
        if (_zapretChip is null) return;
        if (_zapretChipState == state && !force) return;
        _zapretChipState = state;

        var prevZapretCts = _zapretPulseCts;
        _zapretPulseCts = null;
        try { prevZapretCts?.Cancel(); } catch { }
        prevZapretCts?.Dispose();
        _zapretChip.Opacity = 1.0;

        string bgKey, fgKey;
        switch (state)
        {
            case ChipState.On:
                bgKey = "SuccessBgBrush";
                fgKey = "SuccessFgBrush";
                break;
            case ChipState.Connecting:
                bgKey = "WarningBgBrush";
                fgKey = "WarningFgBrush";
                _zapretPulseCts = StartChipPulse(_zapretChip);
                break;
            default:
                bgKey = "SurfaceSunkenBrush";
                fgKey = "TextMutedBrush";
                break;
        }
        _zapretChip.BindToken(TextBlock.BackgroundProperty, bgKey);
        _zapretChip.BindToken(TextBlock.ForegroundProperty, fgKey);
        if (_advZapretChip is not null)
        {
            _advZapretChip.Opacity = 1.0;
            _advZapretChip.BindToken(TextBlock.BackgroundProperty, bgKey);
            _advZapretChip.BindToken(TextBlock.ForegroundProperty, fgKey);
        }
    }

    private void UpdateZapretChipFromState()
    {
        var mode = AndroidStorage.GetDpiBypassMode();
        if (string.IsNullOrEmpty(mode) || string.Equals(mode, "off",
            System.StringComparison.OrdinalIgnoreCase))
        {
            SetZapretChipState(ChipState.Off);
            return;
        }

        switch (_vpnChipState)
        {
            case ChipState.Connecting:
                SetZapretChipState(ChipState.Connecting);
                break;
            case ChipState.On:
                SetZapretChipState(ChipState.On);
                break;
            default:
                SetZapretChipState(ChipState.Off);
                break;
        }
    }

    private System.Threading.CancellationTokenSource StartChipPulse(Visual target)
    {
        var cts = new System.Threading.CancellationTokenSource();
        if (!UiMotion.Enabled) return cts;
        var anim = new Avalonia.Animation.Animation
        {
            Duration = System.TimeSpan.FromMilliseconds(1200),
            // about 43 s, then the chip stands still even if the state never leaves "connecting"
            IterationCount = new Avalonia.Animation.IterationCount(36),
            PlaybackDirection = Avalonia.Animation.PlaybackDirection.Alternate,
            Easing = new Avalonia.Animation.Easings.QuadraticEaseInOut(),
            Children =
            {
                new Avalonia.Animation.KeyFrame
                {
                    Cue = new Avalonia.Animation.Cue(0d),
                    Setters = { new Avalonia.Styling.Setter(Visual.OpacityProperty, 1.0) },
                },
                new Avalonia.Animation.KeyFrame
                {
                    Cue = new Avalonia.Animation.Cue(1d),
                    Setters = { new Avalonia.Styling.Setter(Visual.OpacityProperty, 0.55) },
                },
            },
        };
        _ = anim.RunAsync(target, cts.Token);
        return cts;
    }

    private void OnTunnelErrorReported(AndroidTunnelEvent owner, string message)
    {
        PaintCurrentTunnelState(owner, () =>
        {
            if (string.IsNullOrWhiteSpace(message)) return;
            _lastError = message.Trim();
            _lastErrorAt = DateTime.UtcNow;
            // a reported error ends the attempt: the ring shows the error, not "connecting", even if the chip still pulses
            if (_statusCard is not null) _statusCard.IsWarn = false;
            ApplyErrorOneLinerDisplay();
            StartDiagnosticsTimer();
        });
    }

    private void StartDiagnosticsTimer()
    {
        if (_diagnosticsTimer is not null && _diagnosticsTimer.IsEnabled) return;
        _diagnosticsTimer ??= new DispatcherTimer(
            TimeSpan.FromSeconds(1),
            DispatcherPriority.Background,
            (_, _) => OnDiagnosticsTick());
        _diagnosticsTimer.Start();
        OnDiagnosticsTick();
    }

    private void StopDiagnosticsTimer()
    {
        if (_diagnosticsTimer is null) return;
        _diagnosticsTimer.Stop();
        if (_statusHealthCheck is not null) _statusHealthCheck.IsVisible = false;
    }

    private void OnDiagnosticsTick()
    {
        if (MainActivity.IsActivityPaused) return;

        try
        {
            if (_connectionStartedAt is DateTime startUtc)
            {
                var elapsed = DateTime.UtcNow - startUtc;
                var uptimeTitle = string.Format(
                    Localization.SimpleStatusTitleOnWithUptime,
                    FormatUptime(elapsed));
                if (!string.Equals(uptimeTitle, _lastFormattedUptimeTitle, System.StringComparison.Ordinal))
                {
                    _lastFormattedUptimeTitle = uptimeTitle;
                    if (_statusCard is not null)
                        _statusCard.Title = uptimeTitle;
                    if (_advFooterStatusText is not null
                        && _advShellOverlay is not null
                        && _advShellOverlay.IsVisible)
                    {
                        _advFooterStatusText.Text = uptimeTitle;
                    }
                }
            }
            else
            {
                _lastFormattedUptimeTitle = null;
            }

            if (_connectionStartedAt is not null)
            {
                var sinceLastProbe = DateTime.UtcNow - _lastHealthProbeAt;
                var connectedFor = DateTime.UtcNow - _connectionStartedAt.Value;
                var dueForProbe = _lastHealthProbeAt == DateTime.MinValue
                    ? connectedFor >= HealthProbeInterval
                    : sinceLastProbe >= HealthProbeInterval;
                if (dueForProbe) RunHealthProbe();
                ApplyHealthCheckDisplay();
            }

            if (_lastError is not null)
            {
                if (DateTime.UtcNow - _lastErrorAt >= ErrorDisplayWindow)
                {
                    _lastError = null;
                    if (_statusErrorOneLiner is not null) _statusErrorOneLiner.IsVisible = false;
                    if (_statusCard is not null) _statusCard.IsError = false;
                    if (_connectionStartedAt is null) StopDiagnosticsTimer();
                }
            }
        }
        catch
        {
        }
    }

    private void RunHealthProbe()
    {
        _lastHealthProbeAt = DateTime.UtcNow;
        _lastHealthOk = VPNRouter.Core.Platform.Android.AndroidSingBoxRuntime.HasRecentTunnelStatus();
        _firstProbePending = false;
    }

    private void ApplyHealthCheckDisplay()
    {
        if (_statusHealthCheck is null) return;
        if (_connectionStartedAt is null)
        {
            _statusHealthCheck.IsVisible = false;
            return;
        }
        _statusHealthCheck.IsVisible = true;

        if (_firstProbePending && _lastHealthProbeAt == DateTime.MinValue)
        {
            _statusHealthCheck.Text = Localization.DiagHealthCheckPending;
            _statusHealthCheck.BindToken(TextBlock.ForegroundProperty, "TextMutedBrush");
            SetStatusIcon(_statusHealthIcon, null);
            return;
        }

        if (_lastHealthOk)
        {
            var ago = (int)Math.Max(0, (DateTime.UtcNow - _lastHealthProbeAt).TotalSeconds);
            _statusHealthCheck.Text = string.Format(UiIcons.StripSymbols(Localization.DiagHealthCheckOk), ago);
            _statusHealthCheck.BindToken(TextBlock.ForegroundProperty, "TextMutedBrush");
            SetStatusIcon(_statusHealthIcon, UiIcons.CircleCheck);
        }
        else
        {
            _statusHealthCheck.Text = Localization.DiagHealthCheckStale;
            _statusHealthCheck.BindToken(TextBlock.ForegroundProperty, "WarningFgBrush");
            SetStatusIcon(_statusHealthIcon, UiIcons.CircleAlert);
        }
    }

    private void ApplyErrorOneLinerDisplay()
    {
        if (_statusErrorOneLiner is null) return;
        if (string.IsNullOrEmpty(_lastError))
        {
            _statusErrorOneLiner.IsVisible = false;
            if (_statusCard is not null) _statusCard.IsError = false;
            return;
        }
        _statusErrorOneLiner.Text = string.Format(Localization.DiagErrorOneLiner, _lastError);
        _statusErrorOneLiner.IsVisible = true;
        if (_statusCard is not null) _statusCard.IsError = true;
    }

    private static string FormatUptime(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        if (elapsed.TotalHours >= 1)
        {
            return string.Format("{0}:{1:D2}:{2:D2}",
                (int)elapsed.TotalHours, elapsed.Minutes, elapsed.Seconds);
        }
        return string.Format("{0}:{1:D2}", elapsed.Minutes, elapsed.Seconds);
    }
}
