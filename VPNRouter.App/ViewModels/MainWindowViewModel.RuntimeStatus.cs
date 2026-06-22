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

    /// <summary>
    /// Wall-clock of the last Skia cache purge. We purge at most every
    /// ~60 seconds (on top of the 2 s poll cadence) so the cost of
    /// regenerating font atlases doesn't show up as a visible render stall.
    /// </summary>
    private DateTime _lastSkiaPurgeAt = DateTime.MinValue;

    /// <summary>
    /// v2.28.5-r5: count of consecutive ticks where all three components
    /// (VPN, Zapret, TgProxy) were idle. Used to throttle the
    /// <see cref="Process.GetProcessesByName"/> calls when nothing's
    /// expected to change — those calls allocate Process[] arrays each
    /// tick and contributed to the user-reported "0–1% CPU cycling
    /// at idle".
    /// </summary>
    private int _runtimeIdleStreak;

    /// <summary>v2.28.5-r5: skip-count for the current cycle. After the
    /// idle streak passes the threshold we skip every other tick
    /// (effective 4 s poll), then every two-out-of-three ticks
    /// (effective 6 s), capped at every three-out-of-four ticks
    /// (effective 8 s). Reset to 0 the moment any component starts.</summary>
    private int _runtimeSkipRemaining;

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

    // v2.37.0-r18 — fixed silly inline ternaries that had identical RU/EN.
    // VPN is universal; Zapret + TgProxy now properly localized via Strings.
    public string VpnBadgeTooltip     => FormatTooltip(Strings.BadgeTooltipVpn, VpnRuntimeStatus);
    public string ZapretBadgeTooltip  => FormatTooltip(Strings.BadgeTooltipZapret, ZapretRuntimeStatus);
    public string TgProxyBadgeTooltip => FormatTooltip(Strings.BadgeTooltipTgProxy, TgProxyRuntimeStatus);

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
            MaybePollConnStats();   // Desktop STATS: live up/down + conn count (fire-and-forget)
        });
        _runtimeStatusTimer.Start();
    }

    private void UpdateRuntimeStatus()
    {
        try
        {
            // v2.28.5-r5: adaptive poll. If everything's been idle for a
            // while, skip some ticks to cut CPU "0-1% cycling at idle".
            // The skip plan: 0–2 idle ticks → poll every tick (no skip,
            // 2 s effective). 3–5 idle ticks → skip every other tick
            // (4 s effective). 6+ idle ticks → skip 2-of-3 (6 s effective),
            // capped at 3-of-4 (8 s effective). Any time something
            // actually starts running, the streak resets and full polling
            // resumes immediately.
            if (_runtimeSkipRemaining > 0)
            {
                _runtimeSkipRemaining--;
                return;
            }

            var vpnRunning = RuntimeStatusDetector.IsVpnRunning();
            var zapretRunning = RuntimeStatusDetector.IsZapretRunning();

            var tgPort = _settings?.App?.TgProxyPort ?? 0;
            if (tgPort <= 0) tgPort = 1443;
            var tgProxyRunning = RuntimeStatusDetector.IsTgProxyRunning(tgPort);

            if (vpnRunning || zapretRunning || tgProxyRunning)
            {
                _runtimeIdleStreak = 0;
                _runtimeSkipRemaining = 0;
            }
            else
            {
                _runtimeIdleStreak++;
                _runtimeSkipRemaining = _runtimeIdleStreak switch
                {
                    < 3   => 0, // 2 s effective
                    < 6   => 1, // 4 s effective
                    < 12  => 2, // 6 s effective
                    _      => 3, // 8 s effective (cap)
                };
            }

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

            // v2.20.5: periodic Skia cache purge. Piggy-backs on the existing
            // 2 s status poll; runs at most once per minute so the cost of
            // regenerating font atlases is amortised. An Avalonia user
            // reported ~40 MB working set drop from a single PurgeAllCaches
            // call in a long-lived app — documented in
            // plans/vpnrouter-memory-research.md.
            if ((DateTime.UtcNow - _lastSkiaPurgeAt).TotalSeconds >= 60)
            {
                _lastSkiaPurgeAt = DateTime.UtcNow;
                try { SkiaSharp.SKGraphics.PurgeAllCaches(); }
                catch { /* native-side failures are not fatal */ }
            }
        }
        catch
        {
            // Poll failures are non-fatal
        }
    }

    /// <summary>
    /// v2.32.1-r6 (Bug-r10-C) — synchronous status refresh for callers
    /// that just changed something they expect the badges to reflect
    /// (e.g. <c>KillConflictingVpnAsyncCommand</c>). Without this, the
    /// adaptive poll throttle (up to 8 s when idle) can leave green
    /// badges looking stuck on stale state for several seconds.
    ///
    /// <para>Resets the idle-streak counter so subsequent timer ticks
    /// run at full 2 s cadence again — defensive against the throttle
    /// staying high after a state transition.</para>
    /// </summary>
    public void ForceRefreshRuntimeStatus()
    {
        _runtimeIdleStreak = 0;
        _runtimeSkipRemaining = 0;
        UpdateRuntimeStatus();
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

        // v2.44.1-r2 (user report 2026-06-22): also reconcile when we already
        // think we're connected but the status drifted to "Failed to start VPN".
        // A late-phase start throw (post-start probe / AutoFailover) can clobber
        // StatusText AFTER an earlier OnEngineStatus("Connected") set IsConnected
        // true — leaving a stale failure on screen while the tunnel is actually
        // up. The original `!IsConnected`-only condition never refreshed that.
        if (vpnRunning &&
            (!IsConnected ||
             StatusText.StartsWith(Strings.FailedStartVpn, StringComparison.Ordinal)))
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
            // v2.20.0: protect a freshly-connected session from premature
            // demotion. On macOS, Process.GetProcessesByName("sing-box") can
            // return an empty list for 1–2 seconds after `sudo sing-box …`
            // spawns (sudo's privilege-drop + re-exec handoff makes the
            // parent PID transient). If a poll lands in that window we'd
            // flip IsConnected back to false even though the tunnel is up.
            // Skip the demote for 8 s after a confirmed successful connect.
            if (_lastSuccessfulConnectAt != DateTime.MinValue &&
                (DateTime.UtcNow - _lastSuccessfulConnectAt).TotalSeconds < 8)
                return;

            // v2.21.4: beyond the 8 s grace window, double-check the engine
            // directly before demoting. On macOS both the reported user
            // symptom ("status flips to not-connected while VPN is clearly
            // still running") and Linux (pkexec-owned root child) can make
            // Process.GetProcessesByName("sing-box") return 0 even when
            // sing-box is alive — process enumeration via sysctl/procfs
            // occasionally misses root-owned children depending on kernel
            // state. _engine.IsRunning is authoritative: it pings the
            // Clash API over HTTP, which only responds if sing-box is
            // actually serving traffic. If the API says alive, don't
            // demote — wait for a subsequent tick where both signals
            // agree the tunnel is gone.
            try
            {
                if (_engine?.IsRunning == true)
                    return;
            }
            catch { /* IsRunning failures fall through to demote */ }

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
        // In Simple mode the VPN controls are already on screen (whole page
        // IS the VPN controls), so the badge click is a no-op rather than
        // a confusing tab-switch to a hidden layer.
        if (IsSimpleMode) return;
        SelectedTabIndex = IsSubscribeMode ? 1 : 0;
    }

    /// <summary>Zapret badge click: switch to Tools tab AND select Zapret sub-section.
    /// If the user is currently in Simple UI mode, also flip to Advanced so the
    /// target tab is actually visible — Simple mode hides the whole tab strip.</summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void NavigateToZapret()
    {
        if (IsSimpleMode)
        {
            IsSimpleMode = false;
            _settings.App.UiMode = "advanced";
            SaveSettings();
        }
        SelectedTabIndex = 4;        // Tools tab
        SelectedToolIndex = 0;       // Zapret sub-section
    }

    /// <summary>TgProxy badge click: switch to Tools tab AND select TgProxy sub-section.
    /// Same Simple→Advanced fallback as NavigateToZapret.</summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void NavigateToTgProxy()
    {
        if (IsSimpleMode)
        {
            IsSimpleMode = false;
            _settings.App.UiMode = "advanced";
            SaveSettings();
        }
        SelectedTabIndex = 4;        // Tools tab
        SelectedToolIndex = 1;       // TgProxy sub-section
    }
}
