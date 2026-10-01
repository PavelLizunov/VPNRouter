using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using System;
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
            var previousSubscriptionUrl = AndroidStorage.GetSubscriptionUrl();

            if (!string.IsNullOrWhiteSpace(_serverInput?.Text))
            {
                OnSaveClicked(sender, e);
                if (_serverInputError is not null && _serverInputError.IsVisible)
                {
                    return;
                }
            }

            var subscriptionUrl = AndroidStorage.GetSubscriptionUrl();
            if (!string.IsNullOrEmpty(subscriptionUrl))
            {
                var cachedSubscription = AndroidStorage.GetSubscriptions().Find(s =>
                    s.Enabled &&
                    string.Equals(s.Url, subscriptionUrl, StringComparison.OrdinalIgnoreCase) &&
                    s.Servers is { Count: > 0 });

                if (cachedSubscription is null ||
                    !string.Equals(previousSubscriptionUrl, subscriptionUrl, StringComparison.OrdinalIgnoreCase))
                {
                    await ApplyScannedSubscriptionUrlAsync(subscriptionUrl);
                    return;
                }
            }

            SetVpnChipState(ChipState.Connecting);
            UpdateZapretChipFromState();
            activity.RequestConnect();
        }
    }

    private void OnIntentChanged(bool connected)
    {
        Dispatcher.UIThread.Post(() => UpdateConnectionState(connected));
    }

    private void AttachLifecycleEvents()
    {
        if (_lifecycleEventsAttached) return;
        _lifecycleEventsAttached = true;
        MainActivity.IntentChanged += OnIntentChanged;
        MainActivity.TunnelErrorReported += OnTunnelErrorReported;
        MainActivity.StatsReported += OnStatsReported;
    }

    private long _statsPrevDown, _statsPrevUp;
    private DateTime _statsPrevAt;
    private string? _lastStatsSubtitle;

    private void OnStatsReported(long down, long up, int conn)
    {
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                if (_connectionStartedAt is null || _statusCard is null) return;
                var now = DateTime.UtcNow;
                string? subtitle = null;
                if (_statsPrevAt != default)
                {
                    var dt = (now - _statsPrevAt).TotalSeconds;
                    if (dt > 0.1)
                    {
                        var dRate = Math.Max(0, down - _statsPrevDown) / dt;
                        var uRate = Math.Max(0, up - _statsPrevUp) / dt;
                        subtitle = $"↓ {HumanRate(dRate)}   ↑ {HumanRate(uRate)}   · {conn} conn";
                    }
                }
                _statsPrevDown = down; _statsPrevUp = up; _statsPrevAt = now;
                if (subtitle is not null && !string.Equals(subtitle, _lastStatsSubtitle, StringComparison.Ordinal))
                {
                    _lastStatsSubtitle = subtitle;
                    _statusCard.Subtitle = subtitle;
                }
            }
            catch { }
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
        if (_statusCard is null) return;

        if (connected)
        {
            _statusCard.IsOn = true;
            _statusCard.IsWarn = false;
            _statusCard.IsOff = false;
            _statusCard.Title = Localization.SimpleStatusTitleOn;
            _statusCard.Subtitle = Localization.SimpleStatusDescOn;
            if (_ctaConnect is not null) _ctaConnect.IsVisible = false;
            if (_ctaConnecting is not null) _ctaConnecting.IsVisible = false;
            if (_ctaDisconnect is not null) _ctaDisconnect.IsVisible = true;
            SetVpnChipState(ChipState.On);

            _connectionStartedAt = DateTime.UtcNow;
            _statsPrevAt = default; _lastStatsSubtitle = null;
            _lastHealthLogSize = -1;
            _lastHealthLogMTime = DateTime.MinValue;
            _firstProbePending = true;
            _lastHealthOk = false;
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
        var anim = new Avalonia.Animation.Animation
        {
            Duration = System.TimeSpan.FromMilliseconds(1200),
            IterationCount = Avalonia.Animation.IterationCount.Infinite,
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

    private void OnTunnelErrorReported(string message)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (string.IsNullOrWhiteSpace(message)) return;
            _lastError = message.Trim();
            _lastErrorAt = DateTime.UtcNow;
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
        try
        {
            var ctx = global::Android.App.Application.Context;
            var filesDir = ctx.FilesDir;
            if (filesDir is null)
            {
                _lastHealthOk = false;
                return;
            }
            var logPath = System.IO.Path.Combine(filesDir.AbsolutePath, "singbox.log");
            if (!System.IO.File.Exists(logPath))
            {
                _lastHealthOk = false;
                return;
            }

            var info = new System.IO.FileInfo(logPath);
            var size = info.Length;
            var mtime = info.LastWriteTimeUtc;
            var grew = _lastHealthLogSize >= 0 && size > _lastHealthLogSize;
            var recent = (DateTime.UtcNow - mtime) < HealthStaleThreshold;

            var vpnUp = MainActivity.IsVpnTransportActive(
                global::Android.App.Application.Context);

            _lastHealthOk = grew || recent || vpnUp;
            _lastHealthLogSize = size;
            _lastHealthLogMTime = mtime;
            _firstProbePending = false;
        }
        catch
        {
            _lastHealthOk = false;
            _firstProbePending = false;
        }
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
