using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using System;

namespace VPNRouter.Android;

/// <summary>
/// Phase 2C (Wave 9, 2026-05-18) — VPN-lifecycle view-state surface
/// extracted from <c>AndroidApp.axaml.cs</c>. The actual sing-box +
/// VpnService process lifecycle lives in <c>VpnRouterService.java</c>
/// and <c>MainActivity.cs</c> — this partial only carries the
/// AndroidApp-level state propagation: tunnel intent → chip states →
/// status card → diagnostics pump.
///
/// <para><strong>What's here</strong>:</para>
/// <list type="bullet">
///   <item>Connect/Disconnect tap dispatch (<see cref="OnConnectClicked"/>
///   → <c>MainActivity.Instance.RequestConnect/Disconnect</c>).</item>
///   <item>Idempotent lifecycle-event subscribe
///   (<see cref="AttachLifecycleEvents"/>).</item>
///   <item>Status card phase transitions
///   (<see cref="UpdateConnectionState"/>).</item>
///   <item>Chip state machine for VPN + Zapret pills
///   (<see cref="SetVpnChipState"/> / <see cref="SetZapretChipState"/> /
///   <see cref="UpdateZapretChipFromState"/> /
///   <see cref="StartChipPulse"/>).</item>
///   <item>Diagnostics pump (uptime + health probe + error one-liner)
///   driven by a 1 Hz DispatcherTimer:
///   <see cref="StartDiagnosticsTimer"/> / <see cref="OnDiagnosticsTick"/>
///   / <see cref="RunHealthProbe"/> / <see cref="ApplyHealthCheckDisplay"/>
///   / <see cref="ApplyErrorOneLinerDisplay"/> /
///   <see cref="FormatUptime"/>.</item>
///   <item>Tunnel-error receiver
///   (<see cref="OnTunnelErrorReported"/>) that surfaces the error
///   one-liner under the status card for 30 s.</item>
/// </list>
///
/// <para>Fields (<c>_statusCard</c>, <c>_statusHealthCheck</c>,
/// <c>_diagnosticsTimer</c>, <c>_connectionStartedAt</c>, <c>_lastError</c>,
/// chip-state enum + values) stay in the main partial — they're shared
/// reads from <c>BuildSimplePageView</c> and other UI builders.</para>
/// </summary>
public partial class AndroidApp
{
    // Idempotency guard for AttachLifecycleEvents. Avalonia 12 builds exactly
    // ONE AndroidApp + one MainView per process (the App is created once in
    // AvaloniaAndroidApplication.OnCreate; every Activity recreation re-parents
    // the SAME MainView — see Avalonia.Android.ApplicationLifetime.MainView →
    // MainViewFactory = () => _mainView), so the historical multi-instance
    // subscriber-swap (Bug-AND-011 / High-4, Avalonia-11-era `s_currentLifecycleSubscriber`)
    // is unreachable and was removed 2026-06-13. See
    // plans/android-status-card-stale-lifecycle-investigation-2026-06-13.md.
    private bool _lifecycleEventsAttached;

    private void OnConnectClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var activity = MainActivity.Instance;
        if (activity is null) return;
        if (MainActivity.IntendedConnected)
        {
            activity.RequestDisconnect();
        }
        else
        {
            // v2.32.0 desktop parity (2026-05-10): Connect implicitly persists
            // whatever is in the input field before requesting the tunnel —
            // mirrors SmpToggleConnectAsync. The Save button is gone from the
            // Simple page (subscriptions/servers managed in Advanced >
            // Subscriptions tab); typing a vless:// or subscription URL and
            // tapping Connect must still work. OnSaveClicked is a no-op when
            // the input matches what's already saved, so this is idempotent.
            // Skipped when the input is empty so an existing saved config
            // isn't wiped on a "just connect with what I had" tap.
            if (!string.IsNullOrWhiteSpace(_serverInput?.Text))
            {
                OnSaveClicked(sender, e);
                if (_serverInputError is not null && _serverInputError.IsVisible)
                {
                    return;
                }
            }
            // v3.0 Phase 7.1 — flip VPN chip to Connecting immediately so
            // the user gets feedback while the system VPN consent dialog
            // is on screen (most visible during first-launch consent
            // flow). IntentChanged(true) will follow and transition Off →
            // skipped → On in the normal happy path; on consent decline
            // or TUNNEL_ERROR it bounces back to Off.
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
        // Idempotent: OnFrameworkInitializationCompleted runs once per process
        // (single AndroidApp instance — see _lifecycleEventsAttached doc), so
        // this subscribes exactly once; the guard is belt-and-suspenders.
        if (_lifecycleEventsAttached) return;
        _lifecycleEventsAttached = true;
        MainActivity.IntentChanged += OnIntentChanged;
        MainActivity.TunnelErrorReported += OnTunnelErrorReported;
        MainActivity.StatsReported += OnStatsReported;   // P1: live tunnel stats
    }

    // P1 (2026-06-21) — live tunnel stats. VpnRouterService polls clash_api via a
    // PROTECTED socket (the app's own loopback is captured by the tun under a full
    // tunnel) and broadcasts cumulative down/up totals + conn count every 2s; we
    // derive the rate here and render it on the status card. Change-only write +
    // marshalled to the UI thread (the broadcast fires on a binder thread).
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
                // Change-only (Bug-AND-006): skip the setter when identical.
                if (subtitle is not null && !string.Equals(subtitle, _lastStatsSubtitle, StringComparison.Ordinal))
                {
                    _lastStatsSubtitle = subtitle;
                    _statusCard.Subtitle = subtitle;
                }
            }
            catch { /* never disturb the UI */ }
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
            // v3.0 Phase G step 1 (2026-05-09) — flip the shared StatusCard
            // into its On state. The internal Ellipse Fill resolves through
            // DynamicResource on SuccessSolidBrush, so a theme switch while
            // connected re-renders automatically (no manual rebind needed).
            _statusCard.IsOn = true;
            _statusCard.IsWarn = false;
            _statusCard.IsOff = false;
            _statusCard.Title = Localization.SimpleStatusTitleOn;
            _statusCard.Subtitle = Localization.SimpleStatusDescOn;
            if (_ctaConnect is not null) _ctaConnect.IsVisible = false;
            if (_ctaConnecting is not null) _ctaConnecting.IsVisible = false;
            if (_ctaDisconnect is not null) _ctaDisconnect.IsVisible = true;
            SetVpnChipState(ChipState.On);

            // v2.32.0 (AND-DIAG) — start uptime tracking + diagnostics
            // pump. Set _connectionStartedAt FIRST so the immediate first
            // tick (which renders uptime) sees a non-null value.
            _connectionStartedAt = DateTime.UtcNow;
            _statsPrevAt = default; _lastStatsSubtitle = null;   // P1: reset live-stats rate baseline
            _lastHealthLogSize = -1;
            _lastHealthLogMTime = DateTime.MinValue;
            _firstProbePending = true;
            _lastHealthOk = false;
            // Clear any stale error from a previous attempt — a successful
            // reconnect supersedes whatever went wrong before.
            _lastError = null;
            if (_statusErrorOneLiner is not null) _statusErrorOneLiner.IsVisible = false;
            StartDiagnosticsTimer();
            // Surface the pending message immediately so the user sees the
            // status card respond to their tap, instead of waiting up to
            // 30 s for the first probe.
            ApplyHealthCheckDisplay();

            // v2.40.0 AND-NODOZE (2026-06-02) — the first successful connect
            // is the highest-intent moment to ask for the battery-optimization
            // exemption that keeps this foreground service out of the Doze
            // bucket. Fires the native grant dialog exactly once; no-op if
            // already exempt or already asked. Self-guarded — can't throw into
            // this UI path.
            MaybePromptBatteryOptimizationExemption();
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

            // v2.32.0 (AND-DIAG) — reset uptime + hide health check.
            // Keep _lastError around if it was set in the same frame as
            // this disconnect (TUNNEL_ERROR fires before SetIntent(false))
            // so the error one-liner stays visible during the 30 s
            // window. The diagnostics timer keeps ticking briefly so the
            // 30 s auto-clear still runs even after disconnect.
            _connectionStartedAt = null;
            if (_statusHealthCheck is not null) _statusHealthCheck.IsVisible = false;
            if (_lastError is null)
            {
                StopDiagnosticsTimer();
            }
            else
            {
                // Make sure it stays running until the error window expires.
                StartDiagnosticsTimer();
            }
        }
        // v2.32.0 (AND-ZAPRET) — Zapret chip mirrors VPN phase when DPI
        // bypass is enabled, since the bypass is implemented inside the
        // sing-box outbound (no separate process). Recompute on every
        // VPN state transition.
        UpdateZapretChipFromState();
        UpdateConfigSummary();

        // AND-ADV-CHROME (2026-05-10) — flip the Advanced shell's
        // persistent footer (status dot + text + Start/Stop VPN button)
        // alongside the Simple page CTA, so the two surfaces stay in
        // lock-step even while the user is inside Advanced. Helper is
        // null-safe before BuildAdvancedShellOverlay has run.
        ApplyAdvancedFooterConnectionState(connected);
    }

    /// <summary>
    /// v3.0 Phase 7.1 (2026-05-04) — flip VPN chip background + foreground
    /// (and start/stop the Connecting pulse animation) to reflect the
    /// current tunnel lifecycle phase. Idempotent: calling with the same
    /// state is a no-op.
    ///
    /// <para>v3.0 Phase 8.2 (2026-05-07) — chip brushes go through
    /// <see cref="StyledElementResourceExtensions.BindToken"/> so they
    /// auto-repaint on theme variant change. The <paramref name="force"/>
    /// flag lets <see cref="ApplyTheme(string)"/> re-issue the bindings
    /// even when state hasn't changed (a theme flip needs to retain the
    /// active state but re-pick the new variant's color).</para>
    /// </summary>
    private void SetVpnChipState(ChipState state, bool force = false)
    {
        if (_vpnChip is null) return;
        if (_vpnChipState == state && !force) return;
        _vpnChipState = state;

        // Stop any in-flight pulse first — Connecting → On, Connecting → Off,
        // Off → On all need to clear the animation that was driving Opacity.
        // On a forced re-bind we still want to restart the pulse if state
        // is Connecting so the breathing animation stays in sync.
        // Bug-AND-011 / High-6 (2026-05-16 code review) — capture +
        // null + Cancel + Dispose. Pre-fix the CTS was Cancelled but
        // never Disposed, leaking the Timer + ManualResetEvent on
        // every state transition (and chips toggle on every Connect /
        // Disconnect / DPI bypass mode change).
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
            default: // Off
                bgKey = "SurfaceSunkenBrush";
                fgKey = "TextMutedBrush";
                break;
        }
        _vpnChip.BindToken(TextBlock.BackgroundProperty, bgKey);
        _vpnChip.BindToken(TextBlock.ForegroundProperty, fgKey);
        // Bug #3 fix (2026-05-11) — mirror brushes onto the Advanced
        // shell header chip so both surfaces share live state. The chip
        // is null until the Advanced overlay has been built once; first
        // open's force-rebind path covers that case.
        if (_advVpnChip is not null)
        {
            _advVpnChip.Opacity = 1.0;
            _advVpnChip.BindToken(TextBlock.BackgroundProperty, bgKey);
            _advVpnChip.BindToken(TextBlock.ForegroundProperty, fgKey);
        }
    }

    /// <summary>
    /// v2.32.0 (AND-ZAPRET, 2026-05-07) — same shape as
    /// <see cref="SetVpnChipState"/> but for the Zapret chip. Driven by
    /// <see cref="UpdateZapretChipFromState"/>, which composes the
    /// stored <c>dpi_bypass_mode</c> with the live VPN connection state
    /// into a chip color:
    /// <list type="bullet">
    ///   <item>Off: DPI bypass disabled OR VPN not connected (the
    ///   bypass mechanism is in-tunnel, so it can't be active when
    ///   the tunnel is down even if the user enabled it).</item>
    ///   <item>Connecting: DPI bypass enabled AND VPN currently in
    ///   the Connecting phase (pulse warning).</item>
    ///   <item>On: DPI bypass enabled AND VPN connected (success
    ///   green) — the tls_fragment block is now in libbox's outbound
    ///   dialer settings and packets are being fragmented.</item>
    /// </list>
    /// </summary>
    private void SetZapretChipState(ChipState state, bool force = false)
    {
        if (_zapretChip is null) return;
        if (_zapretChipState == state && !force) return;
        _zapretChipState = state;

        // Bug-AND-011 / High-6 (2026-05-16) — same CTS dispose pattern
        // as SetVpnChipState.
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
            default: // Off
                bgKey = "SurfaceSunkenBrush";
                fgKey = "TextMutedBrush";
                break;
        }
        _zapretChip.BindToken(TextBlock.BackgroundProperty, bgKey);
        _zapretChip.BindToken(TextBlock.ForegroundProperty, fgKey);
        // Bug #3 fix (2026-05-11) — mirror brushes onto the Advanced
        // shell header chip (same pattern as SetVpnChipState above).
        if (_advZapretChip is not null)
        {
            _advZapretChip.Opacity = 1.0;
            _advZapretChip.BindToken(TextBlock.BackgroundProperty, bgKey);
            _advZapretChip.BindToken(TextBlock.ForegroundProperty, fgKey);
        }
    }

    /// <summary>
    /// v2.32.0 (AND-ZAPRET) — recompute the Zapret chip color from
    /// (DPI bypass mode, VPN intent state, VPN chip phase). Called
    /// whenever any of the three inputs changes.
    /// </summary>
    private void UpdateZapretChipFromState()
    {
        // DPI bypass off → chip always off, regardless of VPN state.
        var mode = AndroidStorage.GetDpiBypassMode();
        if (string.IsNullOrEmpty(mode) || string.Equals(mode, "off",
            System.StringComparison.OrdinalIgnoreCase))
        {
            SetZapretChipState(ChipState.Off);
            return;
        }

        // DPI bypass enabled — chip mirrors the VPN chip's phase.
        // _vpnChipState is the most accurate signal because it goes
        // through Connecting on click before IntendedConnected flips.
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

    /// <summary>
    /// v3.0 Phase 7.1 — drive a soft "breathing" Opacity animation
    /// (1.0 ↔ 0.55 over 1.2 s, cycling indefinitely). Returns the CTS so
    /// callers can store + cancel it (one CTS per chip — VPN and Zapret
    /// chips each have their own field).
    ///
    /// <para>v2.32.0 (AND-ZAPRET) — refactored from a hard-coded
    /// <c>_vpnPulseCts</c> assignment so both chips can reuse the same
    /// animation. Old call site assigned the cts inside the method;
    /// new contract is "call site owns the CTS field, helper returns
    /// what to store".</para>
    /// </summary>
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
        // Fire-and-forget — the animation drives the visual and gets
        // cancelled when cts.Cancel() is called from SetVpnChipState
        // / SetZapretChipState. The cts itself is owned by the caller.
        _ = anim.RunAsync(target, cts.Token);
        return cts;
    }

    // ── v2.32.0 (AND-DIAG, 2026-05-07) — runtime diagnostics pump ──────

    /// <summary>
    /// Receives ACTION_TUNNEL_ERROR broadcasts via the static
    /// <see cref="MainActivity.TunnelErrorReported"/> event. The receiver
    /// fires from a binder dispatch thread, so we marshal to the UI
    /// thread before mutating Avalonia state. The actual rendering lives
    /// in <see cref="ApplyErrorOneLinerDisplay"/> + the diagnostics timer
    /// loop, which clears the message after
    /// <see cref="ErrorDisplayWindow"/> elapses.
    /// </summary>
    private void OnTunnelErrorReported(string message)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (string.IsNullOrWhiteSpace(message)) return;
            _lastError = message.Trim();
            _lastErrorAt = DateTime.UtcNow;
            ApplyErrorOneLinerDisplay();
            // Keep the timer alive so the 30 s auto-clear runs even when
            // we're disconnected (UpdateConnectionState(false) preserves
            // the timer when _lastError is set).
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
        // Render an immediate first frame so the title shows "0:00" /
        // "0:01" the second the user taps Connect, instead of waiting a
        // full second for the first DispatcherTimer tick.
        OnDiagnosticsTick();
    }

    private void StopDiagnosticsTimer()
    {
        if (_diagnosticsTimer is null) return;
        _diagnosticsTimer.Stop();
        if (_statusHealthCheck is not null) _statusHealthCheck.IsVisible = false;
        // Title resets to plain "Not connected" inside UpdateConnectionState,
        // so we don't touch _statusCard.Title here.
    }

    private void OnDiagnosticsTick()
    {
        try
        {
            // 1. Uptime — refresh title every tick while connected.
            //
            // Bug-AND-006 (2026-05-16) — only mutate Avalonia text
            // properties when the formatted string actually changed.
            // The 1 Hz tick fires twice within the same second under
            // dispatcher contention, and even Avalonia's equality
            // guards on TextBlock.Text/StyledProperty still walk the
            // setter path to compare strings. On budget Android
            // devices that path was a measurable contributor to the
            // user-reported overheating.
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
                    // AND-ADV-CHROME (2026-05-10) — mirror the uptime suffix
                    // into the Advanced shell's footer status text so the
                    // "Connected · M:SS" copy matches between Simple +
                    // Advanced surfaces. Bug-AND-006 — skip the write when
                    // the Advanced shell is collapsed (the TextBlock is
                    // off-screen + culled, but the property setter still
                    // raises an InvalidateMeasure walk through its parent
                    // tree which we can avoid entirely).
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

            // 2. Health probe — every 30 s, only while connected. The first
            // probe fires HealthProbeInterval after Connect; before that
            // we show "awaiting first check" so the surface is not blank.
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

            // 3. Error one-liner — auto-clear after 30 s.
            if (_lastError is not null)
            {
                if (DateTime.UtcNow - _lastErrorAt >= ErrorDisplayWindow)
                {
                    _lastError = null;
                    if (_statusErrorOneLiner is not null) _statusErrorOneLiner.IsVisible = false;
                    // If we're disconnected and the error has cleared,
                    // there's nothing left to drive — stop the timer.
                    if (_connectionStartedAt is null) StopDiagnosticsTimer();
                }
            }
        }
        catch
        {
            // Diagnostics rendering must never crash the app — swallow
            // and let the next tick try again.
        }
    }

    /// <summary>
    /// Read sing-box log file's size + last-write time and decide whether
    /// the tunnel is still actively writing. We pick log delta (rather
    /// than a TCP probe to the proxy) because:
    /// <list type="bullet">
    ///   <item>It's purely local file I/O — no network round-trip needed,
    ///   so the probe itself can't time out under poor connectivity.</item>
    ///   <item>sing-box writes regular DNS-resolution + TCP-connect lines
    ///   while routing real traffic, so a healthy tunnel = a growing log.</item>
    ///   <item>It works for every protocol (VLESS / Hysteria2 / TUIC / SS)
    ///   without needing per-protocol probe machinery.</item>
    /// </list>
    /// </summary>
    private void RunHealthProbe()
    {
        _lastHealthProbeAt = DateTime.UtcNow;
        try
        {
            // F1 fix (EOStārāTheia 2026-05-23 — v2.36 hotfix): read from
            // FilesDir (private sandbox), NOT GetExternalFilesDir.
            // Bug-AND-011 / Critical-1 (2026-05-16) moved sing-box's
            // log.output to the app's private sandbox (FilesDir) for
            // security — VLESS UUIDs / Reality handshake metadata
            // shouldn't leak to world-readable storage. But this health
            // probe wasn't updated to match, so it kept reading from
            // /sdcard/Android/data/.../singbox.log which never exists
            // post-AND-011. Result: every Android user saw the
            // "Проверка не отвечает" warning permanently regardless of
            // tunnel health. See plans/android-disconnect-investigation-v2.36.md.
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
            // mtime is also a healthy signal — covers the case where a
            // log rotation truncates the file (size shrinks) but writing
            // has resumed normally.
            var recent = (DateTime.UtcNow - mtime) < HealthStaleThreshold;

            // F3 (2026-06-15, device-confirmed A101BM): log growth is a
            // "traffic is flowing" signal, NOT a "tunnel is alive" signal. A
            // connected-but-IDLE tunnel writes no sing-box log lines, so after
            // HealthStaleThreshold (60s) the probe would flip to "Stale check"
            // on a perfectly healthy VPN — a common false alarm whenever the
            // user isn't actively generating traffic. Fold in the OS
            // VPN-transport ground truth (the same ConnectivityManager
            // TRANSPORT_VPN signal the resume re-sync trusts): if a VPN
            // transport is still up, the tunnel is alive regardless of log
            // idle. Genuine tunnel death (transport gone) still falls through
            // to the stale warning, so wedge detection is preserved.
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
            return;
        }

        if (_lastHealthOk)
        {
            var ago = (int)Math.Max(0, (DateTime.UtcNow - _lastHealthProbeAt).TotalSeconds);
            _statusHealthCheck.Text = string.Format(Localization.DiagHealthCheckOk, ago);
            _statusHealthCheck.BindToken(TextBlock.ForegroundProperty, "TextMutedBrush");
        }
        else
        {
            _statusHealthCheck.Text = Localization.DiagHealthCheckStale;
            _statusHealthCheck.BindToken(TextBlock.ForegroundProperty, "WarningFgBrush");
        }
    }

    private void ApplyErrorOneLinerDisplay()
    {
        if (_statusErrorOneLiner is null) return;
        if (string.IsNullOrEmpty(_lastError))
        {
            _statusErrorOneLiner.IsVisible = false;
            return;
        }
        _statusErrorOneLiner.Text = string.Format(Localization.DiagErrorOneLiner, _lastError);
        _statusErrorOneLiner.IsVisible = true;
    }

    /// <summary>
    /// Auto-switch uptime format. Under 1 hour: "M:SS" (e.g. "0:42",
    /// "12:05"). At/over 1 hour: "H:MM:SS" (e.g. "1:23:45"). Mirrors the
    /// pattern users see on stock Android in the lock-screen / system
    /// VPN-key tile (and Slack / WhatsApp call timers).
    /// </summary>
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
