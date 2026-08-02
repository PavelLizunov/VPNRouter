using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
// Wave 12 Phase 3 (2026-05-18) — Avalonia 12 moved IClipboard.SetTextAsync /
// TryGetTextAsync into the ClipboardExtensions static class in
// Avalonia.Input.Platform. The legacy direct methods on IClipboard are gone;
// add this using so the extension-method dispatch resolves.
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using VPNRouter.Core;
using VPNRouter.Core.Models;
using VPNRouter.Core.Platform;
using VPNRouter.Core.Services;
using VPNRouter.Core.Services.FreeConfigs;
// v2.31.6-r8: removed duplicate `using VPNRouter.Core.Platform;` (was line 21
// and line 18 — compiler tolerates but flagged in the iter#4 audit).
using VPNRouter.App.Localization;
using VPNRouter.App.ViewModels.FreeConfigs;

namespace VPNRouter.App.ViewModels;

public partial class MainWindowViewModel : ViewModelBase, IDisposable
{
    // v2.37.0-r8 — extracted timeout magic numbers per Phase 1 quality pass.
    // Pre-r8 inline `Task.Delay(2000)` / `(5000)` at multiple sites obscured
    // why the values are what they are. Named constants make the policy
    // intent reviewable + tweakable in one place. Each comment explains
    // the lower-bound rationale (what we'd break by going shorter).
    //
    // Rules toast (`SetRulesToast`): 2s lets users notice the message
    // without it loitering through subsequent actions. Cancelled+reissued
    // when a new toast arrives within the window.
    private const int RulesToastDurationMs = 2000;
    // TgProxy settle window: matches the AutostartBootstrap path's 2s
    // (see `MainWindowViewModel.AutostartBootstrap.cs` line ~165). Proxy
    // needs ≥1.5s on warm-startup to bind the port and serve requests.
    private const int TgProxySettleDelayMs = 2000;
    // Reconnect retry sleep when TUN lock stolen by Service: enough time
    // for the Service's HealthMonitor to give up and release the lock on
    // its own (default ~1.5s release window in Windows Service mode).
    private const int ServiceReleaseRetryDelayMs = 2000;

    private readonly VpnEngine _engine;
    // v2.31.6-r12 (Phase H, iter#4 audit): Dispose-state guard.
    // Pre-r12 the VM had no IDisposable surface — _runtimeStatusTimer
    // (DispatcherTimer) and _subRefreshTimer (System.Threading.Timer)
    // only stopped on the explicit Quit() / OnEngineStatus("Stopped")
    // happy paths. On unhandled exit / X-button close / a future
    // ReloadMainWindowForLocalization-style window rebuild, both timers
    // would leak; FreeConfigsVm.Dispose() (which it implements) was
    // never called either. r12 adds Dispose() that unhooks engine
    // events, stops + disposes both timers, and disposes FreeConfigsVm.
    // MainWindow.Closed should call this; until then it's still wired
    // to Quit() so explicit-quit paths benefit immediately.
    private bool _disposed;
#if PLATFORM_WINDOWS
    private ZapretManager? _zapret;
    private TgProxyManager? _tgProxy;
#endif
    private readonly ILogger _logger;
    // Phase 4 Wave 19 (v3.0 refactor): ISettingsStore seam for Load / Save /
    // ConsumeRecoveryNotice / ConsumePlaceholderPruneNotice. Defaults to
    // <see cref="RealSettingsStore.Instance"/> in the parameterless ctor —
    // production paths see no behaviour change. Tests that want filesystem
    // isolation can pass an <c>InMemorySettingsStore</c> via the chained
    // overload below.
    private readonly ISettingsStore _settingsStore;
    private AppSettings _settings;
    private bool _isLoadingUI;
    private bool _appsLoaded;
    private System.Threading.Timer? _subRefreshTimer;
    private CancellationTokenSource? _subRefreshCts;
    private const int SubRefreshIntervalMs = 3600_000; // 1 hour

    /// <summary>
    /// Timestamp of the last UI-confirmed successful connect. Used by
    /// <see cref="SyncConnectedWithVpnRuntime"/> to suppress false demotes
    /// immediately after connect — on macOS the process enumeration used
    /// by <see cref="RuntimeStatusDetector.IsVpnRunning"/> occasionally
    /// returns false for the first 1–2 poll ticks after sing-box starts
    /// (sudo launch handoff), which was flipping IsConnected back to false.
    /// DateTime.MinValue = no recent connect.
    /// </summary>
    private DateTime _lastSuccessfulConnectAt = DateTime.MinValue;

    // ── Observable state ──

    [ObservableProperty] private string _statusText = Strings.NotConnected;

    // W1.3: "True split active" badge — fed by VpnEngine.TrueSplitEngagedChanged (the kernel driver
    // engaged, so excluded apps are bound past the TUN). Bound to a small status-zone badge + tooltip.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTrueSplitStatusVisible))]
    [NotifyPropertyChangedFor(nameof(IsTrueSplitRetryVisible))]
    private bool _isTrueSplitActive;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTrueSplitStatusVisible))]
    [NotifyPropertyChangedFor(nameof(IsTrueSplitRetryVisible))]
    private string _trueSplitStatusText = Strings.TrueSplitNotApplicable;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTrueSplitStatusVisible))]
    [NotifyPropertyChangedFor(nameof(IsTrueSplitRetryVisible))]
    private bool _isTrueSplitProblem;
    public bool IsTrueSplitStatusVisible =>
        IsConnected && IsSplitTunnel && IsRoutingAppsModeExclude && (!IsTrueSplitActive || IsTrueSplitProblem);
    public bool IsTrueSplitRetryVisible => IsTrueSplitStatusVisible && IsTrueSplitProblem && _engine.IsRunning;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SmpConnectButtonText))]
    [NotifyPropertyChangedFor(nameof(SmpConnectButtonBrush))]
    [NotifyPropertyChangedFor(nameof(SmpActiveServerLine))]
    [NotifyPropertyChangedFor(nameof(SmpHeroTitle))]
    // v2.18.0 compact-design additions — status card / CTA / mini-badge
    [NotifyPropertyChangedFor(nameof(SimpleStatusIsOn))]
    [NotifyPropertyChangedFor(nameof(SimpleStatusIsOff))]
    [NotifyPropertyChangedFor(nameof(SimpleStatusTitle))]
    [NotifyPropertyChangedFor(nameof(SimpleStatusDescription))]
    [NotifyPropertyChangedFor(nameof(SimpleCtaText))]
    [NotifyPropertyChangedFor(nameof(SimpleCtaIsConnected))]
    [NotifyPropertyChangedFor(nameof(SimpleCtaIsDisconnected))]
    // Bug-r9-F-DEFENSIVE — active outbound display refresh on connect.
    [NotifyPropertyChangedFor(nameof(SimpleActiveOutboundLine))]
    [NotifyPropertyChangedFor(nameof(SimpleActiveOutboundIsSuspect))]
    [NotifyPropertyChangedFor(nameof(SimpleActiveOutboundNormalVisible))]
    [NotifyPropertyChangedFor(nameof(SimpleActiveOutboundSuspectVisible))]
    [NotifyPropertyChangedFor(nameof(IsTrueSplitStatusVisible))]
    [NotifyPropertyChangedFor(nameof(IsTrueSplitRetryVisible))]
    private bool _isConnected;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SimpleStatusIsOn))]
    [NotifyPropertyChangedFor(nameof(SimpleStatusIsWarn))]
    [NotifyPropertyChangedFor(nameof(SimpleStatusIsOff))]
    [NotifyPropertyChangedFor(nameof(SimpleStatusTitle))]
    [NotifyPropertyChangedFor(nameof(SimpleStatusDescription))]
    [NotifyPropertyChangedFor(nameof(SimpleCtaText))]
    [NotifyPropertyChangedFor(nameof(SimpleCtaIsConnecting))]
    [NotifyPropertyChangedFor(nameof(SimpleCtaIsConnected))]
    [NotifyPropertyChangedFor(nameof(SimpleCtaIsDisconnected))]
    [NotifyPropertyChangedFor(nameof(SimpleActiveOutboundLine))]
    [NotifyPropertyChangedFor(nameof(SimpleActiveOutboundIsSuspect))]
    [NotifyPropertyChangedFor(nameof(SimpleActiveOutboundNormalVisible))]
    [NotifyPropertyChangedFor(nameof(SimpleActiveOutboundSuspectVisible))]
    private bool _isConnecting;
    [ObservableProperty] private string _connectButtonText = Strings.StartVPN;
    [ObservableProperty] private bool _isRussian;

    /// <summary>
    /// v2.29.0+ Layer 7 (UI surface for update receipt warning).
    /// Populated at app startup from
    /// <see cref="VPNRouter.Core.Services.UpdateChecker.CheckInstallReceipt"/>.
    /// Non-empty value surfaces a dismissible banner at the top of
    /// MainWindow. Empty when the previous update landed correctly OR
    /// no update was attempted recently.
    ///
    /// <para>Catches the failure mode where the auto-update flow
    /// completes (download + apply + restart) but the running binary
    /// is not actually newer than before. Pre-r7 was logged via Serilog
    /// only; users who don't tail the log never saw it.</para>
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdateWarning))]
    private string _updateWarningText = string.Empty;

    public bool HasUpdateWarning => !string.IsNullOrWhiteSpace(UpdateWarningText);

    [RelayCommand]
    private void DismissUpdateWarning() => UpdateWarningText = string.Empty;

    /// <summary>
    /// v2.32.0 — settings-validator recovery banner. Populated in the
    /// MainWindowViewModel constructor from
    /// <see cref="SettingsLoader.ConsumeRecoveryNotice"/> when the
    /// most recent <see cref="SettingsLoader.Load"/> rewrote defaults
    /// over a structurally-valid but semantically-broken config.yaml
    /// (typoed config_mode, port out of range, malformed subscription
    /// URL, etc.). Dismissible without persisting — the underlying
    /// notice was consumed once at startup so dismiss-on-close clears
    /// it for this session and the user won't see the same message
    /// again on next launch unless the corruption recurs.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSettingsRecoveryNotice))]
    private string _settingsRecoveryNoticeText = string.Empty;

    public bool HasSettingsRecoveryNotice =>
        !string.IsNullOrWhiteSpace(SettingsRecoveryNoticeText);

    [RelayCommand]
    private void DismissSettingsRecoveryNotice() =>
        SettingsRecoveryNoticeText = string.Empty;

    // ── v2.32.3 (2026-05-17, Z:\kanareik incident) — placeholder-prune banner ──
    // Populated by ConsumePlaceholderPruneNotice() reading SettingsMigrator's
    // count from AppConfig. Sibling to SettingsRecoveryNoticeText but distinct
    // because the message is specific (placeholder Reality keys, not a generic
    // "we reset something") and the user needs different guidance: add a real
    // vless:// URL or subscription instead of trying to recover the wiped
    // entries (those entries were never real to begin with).

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPlaceholderPruneNotice))]
    private string _placeholderPruneNoticeText = string.Empty;

    public bool HasPlaceholderPruneNotice =>
        !string.IsNullOrWhiteSpace(PlaceholderPruneNoticeText);

    [RelayCommand]
    private void DismissPlaceholderPruneNotice() =>
        PlaceholderPruneNoticeText = string.Empty;

    // ── Bug-r9-E (2026-05-11) — third-party VPN conflict banner ──
    // Set when ToggleConnectionAsync catches ConflictingVpnException
    // (thrown by VpnEngine.StartAsync via ConflictingVpnDetector). The
    // banner names the specific process(es) so the user knows what to
    // stop. Refresh re-runs detection so the user can dismiss after
    // closing the other VPN; Dismiss hides until the next Connect attempt.

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasConflictingVpnWarning))]
    private string _conflictingVpnWarningText = string.Empty;

    public bool HasConflictingVpnWarning =>
        !string.IsNullOrWhiteSpace(ConflictingVpnWarningText);

    [RelayCommand]
    private void DismissConflictingVpnWarning() =>
        ConflictingVpnWarningText = string.Empty;

    /// <summary>
    /// v2.32.1-r4 (Bug-r10-A) — captured conflict list for the
    /// <c>KillConflictingVpnCommand</c> to act on without re-running
    /// detection (which could race with the user simultaneously
    /// closing the other VPN themselves). Mirrors what
    /// <see cref="ConflictingVpnException.Conflicts"/> would carry.
    /// </summary>
    private System.Collections.Generic.IReadOnlyList<VPNRouter.Core.Services.ConflictingVpnDetector.ConflictingProcessInfo>
        _lastConflicts = System.Array.Empty<VPNRouter.Core.Services.ConflictingVpnDetector.ConflictingProcessInfo>();

    [RelayCommand]
    private void RefreshConflictingVpn()
    {
        var conflicts = VPNRouter.Core.Services.ConflictingVpnDetector
            .DetectConflictingVpnProcesses(_logger);
        _lastConflicts = conflicts;
        if (conflicts.Count == 0)
        {
            ConflictingVpnWarningText = string.Empty;
            return;
        }
        var first = conflicts[0];
        ConflictingVpnWarningText =
            Strings.ConflictOtherVpnDetectedMessage(first.ProcessName, first.Pid);
    }

    /// <summary>
    /// v2.32.1-r5 (Bug-r10-B) + reconnect fix (2026-06-15) — session-scoped
    /// opt-out from <see cref="ConflictingVpnDetector"/>. Set by
    /// <see cref="IgnoreVpnConflictAndConnectAsyncCommand"/> and KEPT for the rest
    /// of the app session so EVERY (re)start honours it: the primary Connect, the
    /// subscription/server-switch reconnect, the Free Configs connect, and (via
    /// <see cref="VpnEngine"/>) the internal AutoFailover re-entry.
    ///
    /// <para>Previously a one-shot reset right after the first Connect — so when a
    /// subscription server was removed, the auto-reconnect / failover re-ran the
    /// Phase 0 conflict pre-flight WITHOUT the user's ignore, threw
    /// <c>ConflictingVpnException</c>, and the VPN never came back while a tolerated
    /// VPN (AmneziaWG / WireGuard) was up. Persisting it for the session fixes that;
    /// a fresh re-detect happens on the next app launch.</para>
    /// </summary>
    private bool _skipVpnConflictThisSession;

    /// <summary>
    /// v2.32.1-r5 (Bug-r10-B) — «Игнорировать» button. Bypasses
    /// ConflictingVpnDetector on THIS Connect (session-scoped), clears
    /// banner, retries Connect. Use case: AmneziaVPN.exe sitting idle
    /// in tray (process running but wintun not held — false positive).
    /// Если юзер ошибся — sing-box упадёт с оригинальной wintun ошибкой
    /// в downstream catch'е, recoverable.
    /// </summary>
    [RelayCommand]
    private async Task IgnoreVpnConflictAndConnectAsync()
    {
        _skipVpnConflictThisSession = true;
        ConflictingVpnWarningText = string.Empty;
        _logger.Information("[VM] User opted to ignore VPN conflict — retrying Connect with bypass");
        if (!IsConnected && !IsConnecting)
        {
            await ToggleConnectionAsync();
        }
    }

    /// <summary>
    /// v2.32.1-r4 (Bug-r10-A) — user-reported pain (2026-05-11): на
    /// основной Win машине app требовал убить AmneziaVPN, но кнопки
    /// kill не было — пришлось через Task Manager. Этот command
    /// force-kills все processes из последней detection batch'и
    /// (<see cref="_lastConflicts"/>). Используется через UI-кнопку
    /// «Завершить» в conflict banner.
    /// </summary>
    [RelayCommand]
    private async Task KillConflictingVpnAsync()
    {
        if (_lastConflicts.Count == 0)
        {
            // Banner ещё видим но _lastConflicts пуст — refresh first.
            RefreshConflictingVpn();
            if (_lastConflicts.Count == 0) return;
        }

        var killed = 0;
        var failed = 0;
        foreach (var info in _lastConflicts)
        {
            try
            {
                using var proc = System.Diagnostics.Process.GetProcessById(info.Pid);
                // v2.32.1-r6 (Bug-r10-C): was Kill(entireProcessTree: true).
                // entireProcessTree walks Win32_Process WMI for descendants
                // and kills them too — on slow machines this can block the
                // dispatcher for seconds AND can clobber unrelated processes
                // that share a transient parent shell. User report: Kill
                // visually cleared Zapret + TgProxy green badges even though
                // the actual winws.exe / python.exe stayed alive — the
                // status poll was returning stale results during the long
                // WMI walk. Targeted Kill (single process, no tree) is the
                // right scope here: known-VPN-client processes don't have
                // meaningful descendants we need to clean up.
                proc.Kill();
                try { await proc.WaitForExitAsync(System.Threading.CancellationToken.None); } catch { }
                killed++;
                _logger.Information("[VM] Killed conflicting VPN: {Name} (PID {Pid})",
                    info.ProcessName, info.Pid);
            }
            catch (System.ArgumentException)
            {
                // Process already gone — count as success.
                killed++;
            }
            catch (Exception ex)
            {
                failed++;
                _logger.Warning(ex,
                    "[VM] Failed to kill conflicting VPN {Name} (PID {Pid}) — likely needs admin rights / protected process",
                    info.ProcessName, info.Pid);
            }
        }

        // Re-run detection: any new instance started during kill?
        // PIDs ушли, но user мог запустить второй процесс параллельно.
        RefreshConflictingVpn();

        // v2.32.1-r6 (Bug-r10-C): force-refresh Zapret/TgProxy runtime
        // status. The 2s polling timer adaptively throttles to 4–8s
        // when nothing is running — if the throttle was at 8s when
        // user clicked Kill, the green badges could appear stuck. A
        // single synchronous re-poll resets the streak + writes fresh
        // values immediately.
        try { ForceRefreshRuntimeStatus(); } catch { /* defensive */ }

        if (_lastConflicts.Count == 0)
        {
            ConflictingVpnWarningText = string.Empty;
            _logger.Information("[VM] Conflict cleared ({Killed} killed, {Failed} failed)",
                killed, failed);
        }
        else if (failed > 0)
        {
            // Some kills failed — surface a clearer message so user
            // knows to retry as admin or manually via Task Manager.
            ConflictingVpnWarningText =
                Strings.ConflictKillPartialFailure(killed, failed);
        }
    }

    /// <summary>
    /// One-shot adapter between <see cref="SettingsLoader.ConsumeRecoveryNotice"/>
    /// and the bound <see cref="SettingsRecoveryNoticeText"/> property.
    /// Lifted out of the constructor so the ctor stays compact (the
    /// AppAutostartTgProxy regression pin walks the first 5000 chars
    /// of the ctor body looking for the bootstrap fire-and-forget).
    /// </summary>
    private void ConsumeSettingsRecoveryNotice()
    {
        // Phase 4 Wave 19: route through the injected store so tests can
        // seed a notice via InMemorySettingsStore.SeedRecoveryNotice instead
        // of mutating SettingsLoader.LastRecoveryNotice statically.
        var recovery = _settingsStore.ConsumeRecoveryNotice();
        if (string.IsNullOrWhiteSpace(recovery)) return;

        // Loader-supplied recovery line already includes the backup
        // path; pass an empty path to Strings so we don't double up.
        SettingsRecoveryNoticeText =
            Strings.SettingsRecoveredFromBadConfig(string.Empty)
            + " (" + recovery + ")";
    }

    /// <summary>
    /// v2.32.3 (2026-05-17) — sibling of ConsumeSettingsRecoveryNotice for
    /// the placeholder-prune banner. SettingsMigrator stamps a count + UTC
    /// timestamp on AppConfig when it strips placeholder credentials; this
    /// adapter reads them once via
    /// <see cref="SettingsLoader.ConsumePlaceholderPruneNotice"/> and binds
    /// the resulting human message to <see cref="PlaceholderPruneNoticeText"/>.
    /// Two branches: at least one healthy server survives (normal banner)
    /// vs nothing left in vless.servers + no subscriptions (allGone banner
    /// that nudges the user to add a real server).
    /// </summary>
    private void ConsumePlaceholderPruneNotice()
    {
        // Phase 4 Wave 19: route through the injected store. Real semantics
        // are the same (mutates _settings.App.PlaceholderPruneCount in place);
        // the indirection matters only to test injection.
        var consumed = _settingsStore.ConsumePlaceholderPruneNotice(_settings);
        if (consumed.Count == 0) return;

        var hasAnyServerLeft =
            (_settings.Vless.GetEffectiveServers().Count > 0) ||
            (_settings.App.Subscriptions?.Any(s => s.Servers?.Count > 0) == true);

        PlaceholderPruneNoticeText = hasAnyServerLeft
            ? string.Format(Strings.PlaceholderPruneBanner, consumed.Count)
            : Strings.PlaceholderPruneBannerAllGone;
    }

    /// <summary>
    /// True when the window should render the one-page SimplePage instead of
    /// the full tabbed Advanced layout. Persisted via AppSettings.App.UiMode.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UiModeToggleText))]
    [NotifyPropertyChangedFor(nameof(UiModeToggleTooltip))]
    private bool _isSimpleMode;

    public string UiModeToggleText   => IsSimpleMode ? Strings.SmpToggleToAdvanced : Strings.SmpToggleToSimple;
    public string UiModeToggleTooltip => Strings.SmpToggleTooltip;

    // v2.21.0: Linux-specific flags for UI. Zapret (winws.exe) and TgProxy
    // (Python embeddable) are Windows-only; their sub-sections of the Tools
    // tab + related buttons are hidden on Linux. Expose both IsLinux and
    // IsWindows so XAML can bind IsVisible without a converter.
    public bool IsLinuxPlatform   => OperatingSystem.IsLinux();
    public bool IsWindowsPlatform => OperatingSystem.IsWindows();

    /// <summary>
    /// v2.40.x (Fix #9) / v2.41.0 (Fix #1): whether the DNS-leak-lockdown toggle
    /// does something on this OS. Windows: firewall DNS-port lockdown. macOS
    /// (r3): MacDnsHardening pins the system resolver to the TUN gateway so
    /// mDNSResponder stops leaking to the ISP. Linux: still a no-op (no DNS
    /// hardening / nftables kill-switch yet) → toggle greyed + honesty note.
    /// </summary>
    public bool IsDnsLeakLockdownAvailable => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();
    /// <summary>True when Zapret DPI bypass is available on the current OS (Windows only).</summary>
    public bool IsZapretAvailable => OperatingSystem.IsWindows();
    /// <summary>True when bundled Telegram proxy is available on the current OS (Windows only).</summary>
    public bool IsTgProxyAvailable => OperatingSystem.IsWindows();
    /// <summary>True when the wgturn Emergency Channel is available — Windows /
    /// macOS / Linux. The wgturn-cli binary is fetched on-demand per platform by
    /// WgturnUpdater (which publishes windows/darwin/linux x64+arm64 assets), so it
    /// needs no bundling.</summary>
    public bool IsEmergencyChannelAvailable =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() || OperatingSystem.IsLinux();
    /// <summary>Tools-tab visibility gate. Visible when ANY sub-tool is available,
    /// so the Emergency Channel shows on macOS/Linux even though Zapret + Telegram
    /// proxy are Windows-only. Was gated on <see cref="IsZapretAvailable"/>, which
    /// hid the whole Tools tab — and the cross-platform Emergency Channel — off
    /// Windows (the parity bug fixed 2026-06-15).</summary>
    public bool IsToolsAvailable => Internals.ToolTabAvailability.ToolsTabVisible(
        IsZapretAvailable, IsTgProxyAvailable, IsEmergencyChannelAvailable);
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsServerListMode))]
    [NotifyPropertyChangedFor(nameof(SimpleConfigModeSummary))]
    private bool _isVlessMode = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsServerListMode))]
    [NotifyPropertyChangedFor(nameof(SimpleConfigModeSummary))]
    private bool _isSubscribeMode = false;

    /// <summary>True when the server ListBox should be visible (Manual or Subscribe mode).</summary>
    public bool IsServerListMode => IsVlessMode || IsSubscribeMode;

    // v2.31.6-r9 — removed `_configModeIndex` ObservableProperty +
    // `ConfigModeItems` getter + `OnConfigModeIndexChanged` no-op partial.
    // The ComboBox they backed was dropped from the UI in v2.5.0 and the
    // empty handler had been parked as a no-op safety since. No XAML
    // bindings left, no callers — iter#4 audit confirmed unused.

    // Sync mode flags when tab changes. Saves on tab switch so Connect
    // always uses the mode matching the visible tab.
    partial void OnSelectedTabIndexChanged(int value)
    {
        if (_isLoadingUI || _isReconnecting) return;
        // v2.30.2-r1 diag: log every tab transition so the next repro of
        // "Servers tab opened in wrong sub-state" is unambiguous.
        _logger?.Information(
            "[VM] OnSelectedTabIndexChanged tab={Tab} (was IsVlessMode={V}, IsSubscribeMode={S}, ServerModeIndex={I})",
            value, IsVlessMode, IsSubscribeMode, SelectedServerModeIndex);
        if (value == 0) // Manual tab
        {
            IsVlessMode = true;
            IsSubscribeMode = false;
            // v2.30.2-r1 Bug 1 fix: when navigating into the Servers tab,
            // the sub-tab visual selection must match what the page is
            // actually showing. If the user previously had ConfigMode=
            // "subscribe" → "custom" → "subscribe" (peeking) → switch to
            // Servers tab, the SelectedServerModeIndex could be stuck on
            // 1 (Custom) from the peek, while the page now wants to
            // show VLESS rows. Re-sync to the data-driven default.
            var hasManual = Servers.Count > 0;
            var hasCustom = CustomConfigs.Count > 0;
            var desiredSubTab = (hasManual || !hasCustom) ? 0 : 1;
            if (SelectedServerModeIndex != desiredSubTab)
            {
                _logger?.Information(
                    "[VM] OnSelectedTabIndexChanged: aligning sub-tab {From}->{To} (manual={M}, custom={C})",
                    SelectedServerModeIndex, desiredSubTab, Servers.Count, CustomConfigs.Count);
                SelectedServerModeIndex = desiredSubTab;
            }
        }
        else if (value == 1) // Subscribe tab
        {
            IsSubscribeMode = true;
            IsVlessMode = false;
        }
        else if (value == 5) // FreeConfigs tab
        {
            // v2.20.1: lazy-load the FreeConfigs snapshot on first visit.
            // Users who never open this tab save ~6-7 MB of JSON
            // deserialization + retained list. Subsequent visits are no-ops.
            try { FreeConfigsVm?.EnsureCacheLoaded(); }
            catch (Exception ex)
            {
                _logger.Warning(ex, "[VM] FreeConfigs lazy-load failed");
            }
        }
        // Tab 2 (Network), Tab 3 (Applications), Tab 4 (Tools) — no action
    }
    [ObservableProperty] private string _subscriptionUrl = string.Empty;

    // Multiple subscriptions support (v2.12+)
    public ObservableCollection<SubscriptionViewModel> Subscriptions { get; } = new();
    [ObservableProperty] private string _newSubName = string.Empty;
    [ObservableProperty] private string _newSubUrl = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SimpleConfigModeSummary))]
    [NotifyPropertyChangedFor(nameof(IsFullTunnel))]
    [NotifyPropertyChangedFor(nameof(IsTrueSplitStatusVisible))]
    [NotifyPropertyChangedFor(nameof(IsTrueSplitRetryVisible))]
    private bool _isSplitTunnel = true;

    /// <summary>
    /// v2.25.10 fix: exposes the inverse of <see cref="IsSplitTunnel"/> as a
    /// two-way bindable bool so the Full Tunnel RadioButton in Settings →
    /// Routing can drive IsSplitTunnel directly (setting IsFullTunnel=true
    /// sets IsSplitTunnel=false). Needed because RadioButton with
    /// <c>{Binding !IsSplitTunnel}</c> is one-way only and cannot flip the
    /// bool back on user click. Previously the Full RadioButton relied on
    /// GroupName exclusivity to uncheck Split — that worked inside one
    /// window but broke after ReloadMainWindowForLocalization briefly kept
    /// both the old and new window's RadioButtons alive with the same
    /// GroupName, letting the group manager cross-wire them. User symptom:
    /// "VPN seemed to flip to Full by itself after language toggle and
    /// Split would no longer apply".
    /// </summary>
    public bool IsFullTunnel
    {
        get => !IsSplitTunnel;
        set
        {
            if (IsSplitTunnel != !value)
                IsSplitTunnel = !value;
        }
    }

    /// <summary>
    /// v2.32 (r10) — Apps Include/Exclude 2-mode toggle. User feedback
    /// "сделам 2 модм exclude и include". Backed by AM-1 chip's
    /// AppSettings.App.RoutingAppsMode (schema v3 field). Default
    /// "include" = legacy behaviour (selected apps -> VPN). "exclude"
    /// inverts: selected apps -> direct, everything else -> VPN.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRoutingAppsModeInclude))]
    [NotifyPropertyChangedFor(nameof(IsRoutingAppsModeExclude))]
    [NotifyPropertyChangedFor(nameof(L_CurrentAppsModeHint))]
    [NotifyPropertyChangedFor(nameof(IsTrueSplitStatusVisible))]
    [NotifyPropertyChangedFor(nameof(IsTrueSplitRetryVisible))]
    private string _routingAppsMode = "include";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAppsListEditorInclude))]
    [NotifyPropertyChangedFor(nameof(IsAppsListEditorExclude))]
    [NotifyPropertyChangedFor(nameof(ActiveAppGroups))]
    [NotifyPropertyChangedFor(nameof(SelectedActiveAppGroup))]
    private string _appsListEditorMode = "include";

    public bool IsAppsListEditorInclude
    {
        get => string.Equals(AppsListEditorMode, "include", StringComparison.OrdinalIgnoreCase);
        set { if (value) AppsListEditorMode = "include"; }
    }

    public bool IsAppsListEditorExclude
    {
        get => string.Equals(AppsListEditorMode, "exclude", StringComparison.OrdinalIgnoreCase);
        set { if (value) AppsListEditorMode = "exclude"; }
    }

    public ObservableCollection<AppGroupViewModel> ActiveAppGroups =>
        IsAppsListEditorExclude ? BypassAppGroups : AppGroups;

    public AppGroupViewModel? SelectedActiveAppGroup
    {
        get => IsAppsListEditorExclude ? SelectedBypassAppGroup : SelectedAppGroup;
        set
        {
            if (IsAppsListEditorExclude)
                SelectedBypassAppGroup = value;
            else
                SelectedAppGroup = value;
            OnPropertyChanged();
        }
    }

    partial void OnAppsListEditorModeChanged(string value)
    {
        if (value != "include" && value != "exclude")
        {
            AppsListEditorMode = "include";
            return;
        }
        OnPropertyChanged(nameof(ActiveAppGroups));
        OnPropertyChanged(nameof(SelectedActiveAppGroup));
    }

    /// <summary>True when <see cref="RoutingAppsMode"/> = "include". Two-way
    /// bool for radio/segmented-toggle binding.</summary>
    public bool IsRoutingAppsModeInclude
    {
        get => string.Equals(RoutingAppsMode, "include", StringComparison.OrdinalIgnoreCase);
        set { if (value) RoutingAppsMode = "include"; }
    }

    /// <summary>True when <see cref="RoutingAppsMode"/> = "exclude". Two-way
    /// bool for radio/segmented-toggle binding.</summary>
    public bool IsRoutingAppsModeExclude
    {
        get => string.Equals(RoutingAppsMode, "exclude", StringComparison.OrdinalIgnoreCase);
        set { if (value) RoutingAppsMode = "exclude"; }
    }

    partial void OnRoutingAppsModeChanged(string value)
    {
        if (_isLoadingUI) return;
        var canon = (value ?? "include").Trim().ToLowerInvariant();
        if (canon != "include" && canon != "exclude") canon = "include";
        _settings.App.RoutingAppsMode = canon;

        // AM-3 (2026-05-12): mode toggle keeps two independent selection
        // states (RoutingAppsInclude vs RoutingAppsExclude). When the
        // active mode flips, the checkbox UI must re-read every
        // AppItem.IsChecked from the now-active list — even apps that
        // haven't moved still need a notification so the binding refreshes.
        RefreshAppCheckboxes();

        try { SaveSettings(); }
        catch (Exception ex)
        {
            _logger?.Warning(ex, "[VM] SaveSettings on apps mode change failed");
        }
        if (IsConnected) HasPendingAppChanges = true;
    }

    /// <summary>
    /// AM-3 — read this app's checked state from the list that matches
    /// the currently-active <see cref="RoutingAppsMode"/>. Used by the
    /// AppItem bridge as its ReadMode callback. Case-insensitive lookup;
    /// missing list returns false.
    /// </summary>
    internal bool IsAppCheckedInCurrentMode(string processName)
    {
        if (string.IsNullOrEmpty(processName)) return false;
        var list = GetActiveAppList();
        if (list == null) return false;
        return list.Any(p =>
            string.Equals(p, processName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// AM-3 — write this app's checked state into the list that matches
    /// the currently-active <see cref="RoutingAppsMode"/>. Used by the
    /// AppItem bridge as its WriteMode callback. Idempotent: adding an
    /// already-present app is a no-op; removing a missing app is a
    /// no-op. Persists eagerly via SaveSettings (sub-millisecond YAML
    /// write) so toggles survive a Windows reboot even without an
    /// explicit Apply — matches the Bug-r9-I auto-save contract for
    /// AppGroup / AppItem changes.
    /// </summary>
    internal void SetAppCheckedInCurrentMode(string processName, bool isChecked)
    {
        if (string.IsNullOrEmpty(processName)) return;
        var list = GetActiveAppList();
        if (list == null) return;

        var existing = list.FirstOrDefault(p =>
            string.Equals(p, processName, StringComparison.OrdinalIgnoreCase));

        if (isChecked)
        {
            if (existing != null) return;
            list.Add(processName);
        }
        else
        {
            if (existing == null) return;
            list.Remove(existing);
        }

        if (_isLoadingUI) return;

        try { SaveSettings(); }
        catch (Exception ex)
        {
            _logger?.Warning(ex, "[VM] SaveSettings on per-app mode-aware toggle failed");
        }
        if (IsConnected) HasPendingAppChanges = true;
    }

    internal bool IsAppCheckedInIncludeList(string processName) =>
        IsAppCheckedInList(_settings.App.RoutingAppsInclude, processName);

    internal bool IsAppCheckedInExcludeList(string processName) =>
        IsAppCheckedInList(_settings.App.RoutingAppsExclude, processName);

    internal void SetAppCheckedInIncludeList(string processName, bool isChecked) =>
        SetAppCheckedInList(_settings.App.RoutingAppsInclude ??= new List<string>(), processName, isChecked);

    internal void SetAppCheckedInExcludeList(string processName, bool isChecked) =>
        SetAppCheckedInList(_settings.App.RoutingAppsExclude ??= new List<string>(), processName, isChecked);

    private static bool IsAppCheckedInList(List<string>? list, string processName)
    {
        if (string.IsNullOrEmpty(processName) || list == null) return false;
        return list.Any(p => string.Equals(p, processName, StringComparison.OrdinalIgnoreCase));
    }

    private void SetAppCheckedInList(List<string> list, string processName, bool isChecked)
    {
        if (string.IsNullOrEmpty(processName)) return;
        var existing = list.FirstOrDefault(p =>
            string.Equals(p, processName, StringComparison.OrdinalIgnoreCase));

        if (isChecked)
        {
            if (existing != null) return;
            list.Add(processName);
        }
        else
        {
            if (existing == null) return;
            list.Remove(existing);
        }

        if (_isLoadingUI) return;

        try { SaveSettings(); }
        catch (Exception ex)
        {
            _logger?.Warning(ex, "[VM] SaveSettings on per-app list toggle failed");
        }
        if (IsConnected) HasPendingAppChanges = true;
    }

    /// <summary>
    /// AM-3 — resolve the active list from the current mode. Defaults to
    /// RoutingAppsInclude when the value is anything other than
    /// "exclude" (safer default — the include path is the legacy one
    /// users expect).
    /// </summary>
    private List<string>? GetActiveAppList()
    {
        if (_settings?.App == null) return null;
        var isExclude = string.Equals(
            _settings.App.RoutingAppsMode, "exclude",
            StringComparison.OrdinalIgnoreCase);

        if (isExclude)
        {
            return _settings.App.RoutingAppsExclude
                ??= new List<string>();
        }
        return _settings.App.RoutingAppsInclude
            ??= new List<string>();
    }

    /// <summary>
    /// AM-3 — re-fire <see cref="AppItemViewModel.IsChecked"/> change
    /// notifications across every app so XAML CheckBoxes refresh from
    /// the now-active mode list. Called from
    /// <see cref="OnRoutingAppsModeChanged"/> after the mode flip.
    /// </summary>
    private void RefreshAppCheckboxes()
    {
        foreach (var group in AppGroups.Concat(BypassAppGroups))
        {
            foreach (var app in group.Apps)
                app.RaiseIsCheckedChanged();
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBadComboWarningVisible))]
    private bool _bypassRussianTraffic = true;


    partial void OnAutostartUiChanged(bool value)
    {
        if (_isLoadingUI) return;
        // v2.29.0: AutostartHelper became cross-platform (Mac LaunchAgent +
        // Linux XDG autostart in addition to the existing Win HKCU\Run).
        // Old `#if PLATFORM_WINDOWS` guard removed — helper handles platform
        // dispatch internally, no-ops on unsupported OS (none currently).
        try
        {
            if (value)
                AutostartHelper.Enable(Environment.ProcessPath!);
            else
                AutostartHelper.Disable();
        }
        catch (Exception ex) { _logger.Error(ex, "[VM] Autostart UI toggle failed"); }
        SaveSettings();
    }

    // v2.27 Bug B: re-fire PropertyChanged for SmpAutostartChecked whenever
    // AutostartVpn flips — the Simple-mode checkbox is now a computed read
    // of (ServiceVm.IsInstalled && ServiceVm.IsRunning && AutostartVpn),
    // so any one of those changing must notify the binding. The matching
    // ServiceVm.IsInstalled/IsRunning listener is wired in the constructor.
    partial void OnAutostartVpnChanged(bool value)
    {
        // 2026-05-11: SaveSettings can throw UnauthorizedAccessException
        // (test harness without admin, or AppData ACL drift). Match the
        // Bug-r9-I pattern from OnAppGroupPropertyChanged / OnAppItemPropertyChanged
        // — wrap in try/catch + log so the setter never propagates an IO
        // failure to the binding pipeline.
        if (!_isLoadingUI)
        {
            try { SaveSettings(); }
            catch (Exception ex) { _logger?.Warning(ex, "[VM] Auto-save on AutostartVpn change failed"); }
        }
        OnPropertyChanged(nameof(SmpAutostartChecked));
    }
    partial void OnAutostartZapretChanged(bool value)
    {
        if (_isLoadingUI) return;
        try { SaveSettings(); }
        catch (Exception ex) { _logger?.Warning(ex, "[VM] Auto-save on AutostartZapret change failed"); }
    }
    partial void OnAutostartTgProxyChanged(bool value)
    {
        if (_isLoadingUI) return;

        // v2.31.10-r5 — Generate secret on enable so the Service can
        // autostart tgproxy at boot. Without this, toggling the box
        // before ever clicking "Start" once left config.yaml's
        // tg_proxy_secret empty → Service logged "TgProxy secret not
        // configured, skipping" and silently returned → user saw
        // "Auto launch with Windows for tgproxy doesn't work" with no
        // UI feedback. Same RNG + format used by StartTgProxy below.
        if (value && string.IsNullOrWhiteSpace(TgProxySecret))
        {
            var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
            TgProxySecret = Convert.ToHexString(bytes).ToLowerInvariant();
            TgProxyLink = TgProxyManager.BuildProxyLink("127.0.0.1", TgProxyPort, TgProxySecret);
        }

        SaveSettings();
    }

    // Wave 39 (v2.35.0-r5): persist DNS leak lockdown toggle immediately
    // and surface the Apply pending state while VPN is connected so the
    // change re-applies the firewall lockdown on the next Apply. Hot-reload
    // is not enough — the lockdown lives in firewall rules, not sing-box
    // config, so FirewallManager.EnableDnsLockdownAsync / DisableDnsLockdownAsync
    // (Agent A) must run after the user toggles. The Apply path already
    // invokes those after a successful sing-box reload. Pattern mirrors
    // OnAutostartZapretChanged above (load-guard + try/catch on SaveSettings).
    partial void OnIsDnsLeakLockdownEnabledChanged(bool value)
    {
        if (_isLoadingUI) return;
        try { SaveSettings(); }
        catch (Exception ex) { _logger?.Warning(ex, "[VM] Auto-save on IsDnsLeakLockdownEnabled change failed"); }
        if (IsConnected) HasPendingAppChanges = true;
    }

    // Zapret section navigator (master-detail).
    // v2.31.6-r5 (ZAPRET-2): consolidated 7 sections → 5 per user
    // feedback 2026-05-03 night («упростить ZAPRET страницу — где
    // можно»). Audit findings:
    //   • Diagnostics had ONE button («Run diagnostics») + an output
    //     panel → merged into Status (lives below the warning banner;
    //     diagnosing is the natural follow-up to seeing the status).
    //   • Updates had TWO elements («Update IPSet list» + «Auto-check
    //     Zapret updates» checkbox) → merged into Strategy where the
    //     existing «Update Zapret» button already handles version
    //     management, keeping all update-related controls together.
    // Design handoff cell 7 specifies 7 sections, so this is a
    // documented deviation per v2.31.6-r1/r3 lesson (Rule B4): walking
    // each section with mcp__vpnrouter-test__mouse_click revealed
    // 6 of 7 had ≤3 elements — the 7-section spread was over-architected
    // for the actual content. Surface area per section now averages
    // ~5 controls — denser without crowding.
    // The IsZapret*Section flags below are kept as 5 contiguous indices;
    // pre-r5 dead branches («IsZapretUpdatesSection», «IsZapretDiagnosticsSection»)
    // are removed so the XAML can't accidentally bind to unreachable
    // sections.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsZapretStatusSection))]
    [NotifyPropertyChangedFor(nameof(IsZapretStrategySection))]
    [NotifyPropertyChangedFor(nameof(IsZapretHostsSection))]
    [NotifyPropertyChangedFor(nameof(IsZapretFiltersSection))]
    [NotifyPropertyChangedFor(nameof(IsZapretAdvancedSection))]
    private int _selectedZapretSectionIndex;

    public bool IsZapretStatusSection => SelectedZapretSectionIndex == 0;
    public bool IsZapretStrategySection => SelectedZapretSectionIndex == 1;
    public bool IsZapretHostsSection => SelectedZapretSectionIndex == 2;
    public bool IsZapretFiltersSection => SelectedZapretSectionIndex == 3;
    public bool IsZapretAdvancedSection => SelectedZapretSectionIndex == 4;

    // Zapret tool state
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LblFlowsealHosts))]
    private bool _flowsealHostsInstalled;
    [ObservableProperty] private int _gameFilterModeIndex;
    [ObservableProperty] private int _ipSetModeIndex;
    [ObservableProperty] private bool _zapretAutoUpdateCheck;

    public string LblFlowsealHosts => IsRussian
        ? (FlowsealHostsInstalled ? "Убрать Flowseal hosts" : "Добавить Flowseal hosts")
        : (FlowsealHostsInstalled ? "Remove Flowseal hosts" : "Add Flowseal hosts");

    // Settings section navigator (master-detail)
    // v2.30.0-r2: added Rules as section index 1 (between Routing and
    // Leak Protection — natural sibling to Routing, since both deal
    // with traffic routing). Existing indexes shifted +1.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSettingsRoutingSelected))]
    [NotifyPropertyChangedFor(nameof(IsSettingsRulesSelected))]
    [NotifyPropertyChangedFor(nameof(IsSettingsLeakSelected))]
    [NotifyPropertyChangedFor(nameof(IsSettingsContentSelected))]
    [NotifyPropertyChangedFor(nameof(IsSettingsUpdatesSelected))]
    [NotifyPropertyChangedFor(nameof(IsSettingsAutostartSelected))]
    private int _selectedSettingsIndex;

    public bool IsSettingsRoutingSelected   => SelectedSettingsIndex == 0;
    public bool IsSettingsRulesSelected     => SelectedSettingsIndex == 1;
    public bool IsSettingsLeakSelected      => SelectedSettingsIndex == 2;
    public bool IsSettingsContentSelected   => SelectedSettingsIndex == 3;
    public bool IsSettingsUpdatesSelected   => SelectedSettingsIndex == 4;
    public bool IsSettingsAutostartSelected => SelectedSettingsIndex == 5;

    // Tools sub-tabs
    // v2.32.2 (W-4) — added third tab «Emergency Channel» (wgturn).
    // Sub-tab order on the Tools page top strip:
    //   0 = Zapret
    //   1 = Telegram Proxy
    //   2 = Emergency Channel (new)
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsZapretToolSelected))]
    [NotifyPropertyChangedFor(nameof(IsTgProxyToolSelected))]
    [NotifyPropertyChangedFor(nameof(IsEmergencyChannelToolSelected))]
    // Default to the first AVAILABLE sub-tab so macOS/Linux (no Zapret/TgProxy)
    // opens on the Emergency Channel instead of the hidden Windows-only Zapret page.
    private int _selectedToolIndex = Internals.ToolTabAvailability.DefaultToolIndex(
        OperatingSystem.IsWindows(),                                             // Zapret
        OperatingSystem.IsWindows(),                                             // Telegram proxy
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() || OperatingSystem.IsLinux()); // Emergency Channel

    public bool IsZapretToolSelected => SelectedToolIndex == 0;
    public bool IsTgProxyToolSelected => SelectedToolIndex == 1;
    public bool IsEmergencyChannelToolSelected => SelectedToolIndex == 2;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedActiveAppGroup))]
    private AppGroupViewModel? _selectedAppGroup;

    // Detail editor state — independent of SelectedServer (left click sets active, right click opens detail)
    [ObservableProperty] private ServerViewModel? _detailServer;
    [ObservableProperty] private CustomConfigViewModel? _detailCustomConfig;

    [RelayCommand]
    private void CloseServerDetail() => DetailServer = null;

    [RelayCommand]
    private void CloseCustomConfigDetail() => DetailCustomConfig = null;

    [RelayCommand]
    private void OpenServerDetail(ServerViewModel? server) => DetailServer = server;

    [RelayCommand]
    private void OpenCustomConfigDetail(CustomConfigViewModel? cfg) => DetailCustomConfig = cfg;

    // Phase 2B (Wave 8, 2026-05-18) - Version block (VersionText,
    // AppVersionShortText, GetSingBoxVersion) moved to
    // MainWindowViewModel.Settings.cs.

        // Phase 2B (Wave 8, 2026-05-18) - Troubleshooting / About / Reset / Logs
    // moved to MainWindowViewModel.Settings.cs:
    //   - OpenLeakTest
    //   - RunHealthCheck
    //   - OpenAbout
    //   - ResetConfigArmed / ResetConfigMenuHeader / OnResetConfigArmedChanged
    //   - RestartInSafeMode / ResetConfig / _resetDisarmCts
    //   - OpenLogs
    // ── VLESS fields (for single-server quick edit) ──
    [ObservableProperty] private string _vlessUri = string.Empty;

    // ── Collections ──
    public ObservableCollection<ServerViewModel> Servers { get; } = new();
    public ObservableCollection<CustomConfigViewModel> CustomConfigs { get; } = new();
    public ObservableCollection<ServerViewModel> SubscriptionServers { get; } = new();
    [ObservableProperty] private ServerViewModel? _selectedSubscriptionServer;
    public ObservableCollection<AppGroupViewModel> AppGroups { get; } = new();
    public ObservableCollection<AppGroupViewModel> BypassAppGroups { get; } = new();

    // ── Selected items ──
    [ObservableProperty] private ServerViewModel? _selectedServer;
    [ObservableProperty] private CustomConfigViewModel? _selectedCustomConfig;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedActiveAppGroup))]
    private AppGroupViewModel? _selectedBypassAppGroup;

    // ── Sub-ViewModels ──
    public UpdateNotificationViewModel UpdateVm { get; }
    public ServiceViewModel ServiceVm { get; }
    public FreeConfigsPageViewModel FreeConfigsVm { get; private set; } = null!;

    public MainWindowViewModel() : this(null) { }

    /// <summary>
    /// Phase 4 Wave 19 (v3.0 refactor) — ctor overload with explicit
    /// <see cref="ISettingsStore"/> injection. Pass <c>null</c> (or use
    /// the parameterless ctor) to fall back to
    /// <see cref="RealSettingsStore.Instance"/>; tests pass
    /// <c>InMemorySettingsStore</c> to keep the VM isolated from the
    /// on-disk <c>config.yaml</c>.
    /// </summary>
    public MainWindowViewModel(ISettingsStore? settingsStore)
    {
        _settingsStore = settingsStore ?? RealSettingsStore.Instance;

        _logger = new LoggerConfiguration()
            .WriteTo.File(
                Path.Combine(AppPaths.LogsDir, "vpnrouter.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7)
            .WriteTo.Console()
            .CreateLogger();

        // v2.31.6-r16 (iter#7 / Phase 3): wire the static TcpTlsProbe logger
        // so quick-probe runs (Servers/Subscribe Test all + Free Configs bulk)
        // emit Debug-level entries to vpnrouter*.log. User feedback:
        // «есть ли у проверки логи?» — pre-r16 the answer was no (zero log
        // calls in TcpTlsProbe). r16 logs every probe target + outcome.
        TcpTlsProbe.Logger = _logger;

        // v2.29.0-r7+ Layer 7 — pick up receipt-derived "previous update
        // didn't land" warning that App.axaml.cs OnFrameworkInitialization
        // stored before this VM was constructed. The HasUpdateWarning
        // banner becomes visible immediately on first window paint.
        if (!string.IsNullOrWhiteSpace(Program.PendingUpdateWarning))
        {
            UpdateWarningText = Program.PendingUpdateWarning!;
            Program.PendingUpdateWarning = null; // consume — don't re-set on hot-reload
        }

        AppPaths.EnsureDirectories();
        DeployBundledProfiles();

        _engine = PlatformServices.CreateVpnEngine(_logger);
        _engine.StatusChanged += OnEngineStatus;
        // 2026-06-09 (rectuspc report): surface AutoFailover messages — the
        // post-start probe finding the active server dead / no failover
        // candidate. This event had NO subscriber in the GUI, so a
        // "connected but the server is unreachable" looked like a silent,
        // successful connect. Now it overwrites the connection status line
        // with the honest warning.
        _engine.AutoFailoverTriggered += OnAutoFailoverMessage;
        // W1.3: reflect the true-split driver's engaged state into the badge (marshalled to the UI
        // thread — the driver raises this from its own control-plane thread).
        _engine.TrueSplitEngagedChanged += OnTrueSplitEngagedChanged;
        _engine.TrueSplitStateChanged += OnTrueSplitStateChanged;

        _settings = _settingsStore.Load(AppPaths.ConfigYamlPath);

        // r10 r9 (Bug-r10-H): wire Servers.CollectionChanged → MarkOrphanServers
        // so the "Не из подписки" badge stays consistent across all entry-add
        // paths (Free Configs Use, paste, subscription rebuild). Must happen
        // after _settings loads.
        WireServersOrphanTracking();

        // v2.32.2 (W-4) — populate Emergency Channel card state from the
        // settings YAML before any UI binding fires. Polls
        // WgturnUpdater.IsInstalled() once; subsequent flips come from
        // Download / Remove commands. Implementation in
        // MainWindowViewModel.Wgturn.cs.
        InitializeWgturnState();

        // Sub-VMs
        UpdateVm = new UpdateNotificationViewModel(_settings.Update, _logger);
        ServiceVm = new ServiceViewModel(_logger);
        FreeConfigsVm = new FreeConfigsPageViewModel(_logger, ApplyFreeConfigAsync, () => _settings);

        // v2.27 Bug B: SmpAutostartChecked is a computed over ServiceVm state,
        // so we need to re-fire PropertyChanged on Simple's checkbox binding
        // every time the service transitions. Without this, an Advanced-mode
        // "Enable background service" toggle that flips IsInstalled/IsRunning
        // silently leaves Simple's UI stale until the user navigates away and
        // back. Scoped to the two properties that actually feed the computed
        // — ignores IsBusy / StatusMessage churn during install.
        //
        // v2.31.10 (autostart UX clarity): IsInstalled also feeds the
        // per-component status badges (LblAutostart{Vpn,Zapret,TgProxy}Status
        // + the IsAutostart*StatusGood/Warn/Bad triplet). When the user toggles
        // the master service, all 12 of those bindings need a fresh read.
        // IsRunning is intentionally not included for the new badges — the
        // boot semantics depend on IsInstalled, not on whether SCM has a live
        // process right now.
        ServiceVm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ServiceViewModel.IsInstalled)
                               or nameof(ServiceViewModel.IsRunning))
            {
                OnPropertyChanged(nameof(SmpAutostartChecked));
            }
            if (e.PropertyName == nameof(ServiceViewModel.IsInstalled))
            {
                OnPropertyChanged(nameof(LblAutostartVpnStatus));
                OnPropertyChanged(nameof(LblAutostartZapretStatus));
                OnPropertyChanged(nameof(LblAutostartTgProxyStatus));
                OnPropertyChanged(nameof(IsAutostartVpnStatusGood));
                OnPropertyChanged(nameof(IsAutostartVpnStatusWarn));
                OnPropertyChanged(nameof(IsAutostartVpnStatusBad));
                OnPropertyChanged(nameof(IsAutostartZapretStatusGood));
                OnPropertyChanged(nameof(IsAutostartZapretStatusWarn));
                OnPropertyChanged(nameof(IsAutostartZapretStatusBad));
                OnPropertyChanged(nameof(IsAutostartTgProxyStatusGood));
                OnPropertyChanged(nameof(IsAutostartTgProxyStatusWarn));
                OnPropertyChanged(nameof(IsAutostartTgProxyStatusBad));
            }
        };

        LoadSettingsIntoUI();

        // v2.40.x (Fix #7): follow live OS appearance flips while the theme
        // preference is "system". Wired once here (PlatformSettings is ready
        // after LoadSettingsIntoUI's first ApplyTheme); torn down in Dispose.
        WireOsThemeFollow();

        // Detect VPN already running (e.g. started by Windows Service on boot)
        DetectServiceManagedVpn();

        // Background update check (fire-and-forget, silent fail)
        _ = UpdateVm.CheckOnStartupAsync();

        // Status dashboard (v2.15.0): poll VPN/Zapret/TgProxy every 2s
        StartRuntimeStatusPolling();

        // v2.31.10 — App-side autostart bootstrap. Closes the gap where
        // autostart_tgproxy / autostart_zapret in config.yaml were read
        // into UI state but never spawned the daemons unless the Windows
        // Service was installed. Defers to the Service when it's running
        // (Service handles boot-spawn). See
        // MainWindowViewModel.AutostartBootstrap.cs for the gating logic.
        _ = BootstrapAutostartAsync();

        // v2.32.0 — surface a SettingsValidator recovery banner if the
        // most recent SettingsLoader.Load rewrote defaults over a
        // structurally-valid but semantically-broken config.yaml.
        // Called after the bootstrap fire-and-forget so the regression
        // pin in AppAutostartTgProxyTests.Bootstrap_IsInvokedFromConstructor
        // (5000-char ctor window) still locates the bootstrap call.
        ConsumeSettingsRecoveryNotice();
        // v2.32.3 (Z:\kanareik incident) — placeholder-prune banner.
        ConsumePlaceholderPruneNotice();
    }

    /// <summary>
    /// Detect if VPN is already running via Windows Service (sing-box process alive).
    /// Sets IsConnected so the UI reflects reality instead of showing "Not connected".
    /// </summary>
    /// <summary>Raised when the active server (green-dot) changes — views scroll to it.</summary>
    public event Action<ServerViewModel?>? ActiveServerChanged;

    /// <summary>
    /// Update IsActive flag on all ServerViewModels so the UI shows a green dot
    /// next to the currently-active server (both VLESS and Subscription lists).
    /// </summary>
    private void RefreshActiveIndicator()
    {
        var activeIp = _engine?.ActiveServerAddress;

        // v2.30.1-r3 fix: gate the active dot by ConfigMode so a manual
        // VLESS entry that happens to share an IP with a subscription
        // server doesn't light up alongside the subscription one.
        // v2.30.1-r6 fix: also disambiguate WITHIN each list — when two
        // entries share an IP (e.g. port 443 + port 8443 on the same
        // host, or VLESS + Hysteria2 on the same host), the previous
        // IP-only match lit BOTH up. User report 2026-05-01: "у меня
        // 2 конфига на 1 ip и при включения одного из них подсвечиваются
        // оба, будто я включил не 1 а 2".
        //
        // Match priority:
        //   1. Name == settings.Vless.ActiveServer (manual mode) /
        //      App.ActiveSubscriptionServer (subscribe mode) — the
        //      authoritative "which entry was picked" signal.
        //   2. Fallback to IP match when no name is set (legacy entries).
        //
        // The name path picks exactly one row even if many share an IP.
        var configMode = _settings?.App?.ConfigMode ?? "generated";
        var isManualMode = configMode.Equals("generated", StringComparison.OrdinalIgnoreCase);
        var isSubscribeMode = configMode.Equals("subscribe", StringComparison.OrdinalIgnoreCase);

        var manualActiveName = _settings?.Vless?.ActiveServer;
        var subscriptionActiveName = _settings?.App?.ActiveSubscriptionServer;

        ServerViewModel? active = null;

        foreach (var s in Servers)
        {
            var isActive = isManualMode
                && IsConnected
                && !string.IsNullOrEmpty(activeIp)
                && IsRowActive(s, activeIp, manualActiveName);
            s.IsActive = isActive;
            if (isActive) active = s;
        }

        // v2.44.1-r6: under AutoSelectBestServer the "proxy" outbound is a
        // urltest — there is no single stored active server, so the old
        // ActiveSubscriptionServer/IP match lit NO row while traffic ran (user
        // report 2026-06-23). Highlight the REAL member resolved from clash_api
        // (_autoSelectedServer, refreshed by the ConnStats poll) instead.
        var autoSelect = isSubscribeMode && AutoSelectBestServer;
        foreach (var s in SubscriptionServers)
        {
            bool isActive;
            if (autoSelect)
                isActive = IsConnected
                    && _autoSelectedServer is not null
                    && ReferenceEquals(s, _autoSelectedServer);
            else
                isActive = isSubscribeMode
                    && IsConnected
                    && !string.IsNullOrEmpty(activeIp)
                    && IsRowActive(s, activeIp, subscriptionActiveName);
            s.IsActive = isActive;
            if (isActive) active = s;
        }

        ActiveServerChanged?.Invoke(active);
    }

    /// <summary>
    /// v2.30.1-r6: disambiguate "active" rows when two entries share
    /// the same IP. If we have a known active-name (from
    /// <c>Vless.ActiveServer</c> or <c>App.ActiveSubscriptionServer</c>),
    /// only the row whose <c>Name</c> matches lights up. Otherwise we
    /// fall back to the legacy IP-only match so settings.yaml files
    /// from before this change still work.
    /// </summary>
    private static bool IsRowActive(ServerViewModel row, string activeIp, string? activeName)
    {
        if (row.Server != activeIp)
            return false;

        // No active-name available → legacy IP-only behaviour.
        if (string.IsNullOrWhiteSpace(activeName))
            return true;

        // Active-name available → require an exact name match. This
        // prevents two entries with the same IP from both lighting up.
        return string.Equals(row.Name, activeName, StringComparison.OrdinalIgnoreCase);
    }

    private void DetectServiceManagedVpn()
    {
        try
        {
            // v2.26.1 — two-signal detection:
            //   1. VPNRouter-OWNED sing-box process alive + TUN owned
            //   2. TUN ownership semaphore held by SOMEONE
            // Both must be true. Signal #1 alone had a false-positive
            // window on startup: a sing-box that just exited but whose
            // process record Windows hadn't reaped yet would still show
            // up and we'd flip IsConnected=true only to demote it on the
            // next poll. TUN-lock check gates that: once the owner releases
            // (on Stop or death), the kernel releases the semaphore
            // atomically so there's no stale window.
            // P1.4 (audit 2026-07-09): signal #1 is the OWNERSHIP-FILTERED
            // detector, not a bare process-name probe. RuntimeStatusDetector
            // .IsVpnRunning delegates to ProcessOwnership.AnySingBoxOwned
            // (image path under our bin dir or the registered custom exe;
            // unverifiable path => not-owned, fail-closed), so a third-party/dev
            // sing-box can never let the UI claim "Connected via service".
            // Supersedes the v2.40.0-r3 bare ProcessQuery.AnyAlive("sing-box")
            // probe; the detector is likewise handle-safe (disposes its Process[]).
            var singboxRunning = VPNRouter.Core.Services.RuntimeStatusDetector.IsVpnRunning();
            if (!singboxRunning) return;

            var tunOwned = TunOwnershipLock.IsOwnedByAnyone();
            if (!tunOwned)
            {
                // sing-box.exe present but nobody holds the TUN semaphore
                // — orphan / zombie from a previous run, not a live
                // service-managed tunnel. Let OrphanCleanup reap it on
                // the next cycle; don't adopt.
                return;
            }

            IsConnected = true;
            ConnectButtonText = Strings.StopVPN;
            var configLabel = IsSubscribeMode ? "subscribe" : IsVlessMode ? "manual" : "custom";
            var tunnelLabel = IsSplitTunnel ? "split" : "full";
            var mode = $"{configLabel}/{tunnelLabel}";
            StatusText = IsRussian
                ? $"Подключено через службу [{mode}]"
                : $"Connected via service [{mode}]";
            MarkTrueSplitServiceManagedIfNeeded();
            StartSubRefreshTimer();
            _logger.Information("[VM] Detected VPN running via service (sing-box alive + TUN owned)");
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "[VM] DetectServiceManagedVpn failed");
        }
    }


    // Phase 2B (Wave 8, 2026-05-18) - Apps / Profiles surface moved to
    // MainWindowViewModel.Profiles.cs:
    //   - LoadApps / CreateBridgedAppItem / ComputeLegacyEffectiveIncludeNames
    //   - WireAppChangeTracking / UnwireAllAppGroups + 3 PropertyChanged handlers
    //   - StripExe (static helper)
    //   - AddCategory / RemoveCategory
    //   - AddCustomApp / RemoveCustomApps / RemoveCustomApp
    //   - DeployBundledProfiles


    // Phase 2B (Wave 8, 2026-05-18) - StripExe moved to MainWindowViewModel.Profiles.cs.




    // Phase 2B (Wave 8, 2026-05-18) — Subscription tab commands +
    // auto-refresh timer moved to MainWindowViewModel.Subscriptions.cs:
    //   - RebuildSubscriptionPool
    //   - AddSubscriptionAsync / RemoveSubscription
    //   - RefreshSubscriptionAsync / RefreshAllSubscriptionsAsync
    //   - SyncSubscriptionAsync / ClearSubscription
    //   - StartSubRefreshTimer / StopSubRefreshTimer
    //   - RefreshSubscriptionSilentAsync




    // Phase 2B (Wave 8, 2026-05-18) - Apps/Profiles commands moved to
    // MainWindowViewModel.Profiles.cs:
    //   - _newCategoryName + AddCategory / RemoveCategory
    //   - AddCustomApp / RemoveCustomApps / RemoveCustomApp

    // Phase 2B (Wave 8, 2026-05-18) - Theme / Language / UI-mode / Settings
    // commands moved to MainWindowViewModel.Settings.cs:
    //   - ToggleTheme / ToggleLanguage
    //   - SetThemeLight / SetThemeDark
    //   - SetLanguageRussian / SetLanguageEnglish
    //   - ToggleUiMode / OpenAutostartSettings / InstallServiceForAutostart
    //   - ApplySettings / ShowWindow

    [RelayCommand]
    private void Quit()
    {
        if (_engine.IsRunning)
            _engine.Stop();

        StopSubRefreshTimer();

        // Kill zapret on app exit
        KillAllZapret();

        // Kill tg-ws-proxy on app exit
#if PLATFORM_WINDOWS
        // v2.31.6-r12: Debug-log instead of swallowing silently.
        try { _tgProxy?.Stop(); TgProxyManager.KillAll(TgProxyPort); }
        catch (Exception ex) { _logger.Debug(ex, "[VM] Quit: _tgProxy.Stop / KillAll failed"); }
#endif

        SaveSettings();

        // v2.31.6-r12 (Phase H): release IDisposable resources so a
        // subsequent app reopen / future ReloadMainWindowForLocalization
        // doesn't leak this VM's timers / event subscriptions.
        Dispose();

        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
    }

    /// <summary>
    /// v2.31.6-r12 (Phase H, iter#4 audit): IDisposable surface for the
    /// VM. Ensures both timers (`_runtimeStatusTimer`, `_subRefreshTimer`),
    /// the engine event handlers, and the FreeConfigs sub-VM all get
    /// torn down cleanly when the VM is no longer needed. Pre-r12 these
    /// only stopped on the explicit Quit() / OnEngineStatus("Stopped")
    /// paths; an unhandled exit or a future window-rebuild path would
    /// leak them.
    ///
    /// <para>Idempotent — safe to call multiple times. Exceptions during
    /// individual cleanup steps are swallowed-with-debug-log so the
    /// rest of the chain still runs (we'd rather leak ONE thing than
    /// leak everything because the first cleanup threw).</para>
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // 0. Drop the OS-appearance follow subscription (Fix #7) so the VM
        // isn't held alive by PlatformSettings after disposal.
        try { UnwireOsThemeFollow(); }
        catch (Exception ex) { _logger.Debug(ex, "[VM] Dispose: UnwireOsThemeFollow failed"); }

        // 1. Stop polling timers.
        try
        {
            _runtimeStatusTimer?.Stop();
            _runtimeStatusTimer = null;
        }
        catch (Exception ex) { _logger.Debug(ex, "[VM] Dispose: _runtimeStatusTimer stop failed"); }

        try { StopSubRefreshTimer(); }
        catch (Exception ex) { _logger.Debug(ex, "[VM] Dispose: StopSubRefreshTimer failed"); }

        // 2. Unhook engine events. _engine itself is owned by the host
        // (App / Service) so we don't dispose it here — just unhook
        // our handlers so a stale VM doesn't continue to receive
        // status updates after disposal.
        try
        {
            _engine.StatusChanged -= OnEngineStatus;
            _engine.AutoFailoverTriggered -= OnAutoFailoverMessage;
            _engine.TrueSplitEngagedChanged -= OnTrueSplitEngagedChanged;   // W1.3 (bug-hunt): don't leak a recreated VM
            _engine.TrueSplitStateChanged -= OnTrueSplitStateChanged;
        }
        catch (Exception ex) { _logger.Debug(ex, "[VM] Dispose: engine StatusChanged unhook failed"); }

        // 3. Dispose the FreeConfigs sub-VM (it owns its own timers +
        // HttpClient + cache write FileStream).
        try
        {
            FreeConfigsVm?.Dispose();
        }
        catch (Exception ex) { _logger.Debug(ex, "[VM] Dispose: FreeConfigsVm.Dispose failed"); }

        // 4. Cancel + dispose the subscription-refresh CTS if active.
        try
        {
            var cts = _subRefreshCts;
            _subRefreshCts = null;
            if (cts != null)
            {
                cts.Cancel();
                cts.Dispose();
            }
        }
        catch (Exception ex) { _logger.Debug(ex, "[VM] Dispose: _subRefreshCts cleanup failed"); }

        // 5. Dispose the clash_api live-stats client (owns an HttpClient). It is
        // normally disposed on disconnect (OnIsConnectedChanged false), but a quit
        // while still connected never flips IsConnected, so close it here too.
        try
        {
            _statsApi?.Dispose();
            _statsApi = null;
        }
        catch (Exception ex) { _logger.Debug(ex, "[VM] Dispose: _statsApi cleanup failed"); }

        // The X-close path calls Dispose() directly (not Quit()), so the manager
        // events / probe timer / toast continuations below would otherwise keep
        // this disposed VM rooted and post UI mutations after disposal (M1–M4).

#if PLATFORM_WINDOWS
        // 6. M1: detach the TgProxy stats handler + dispose the manager.
        // (_tgProxy / _zapret are Windows-only fields — guard the whole block.)
        try
        {
            if (_tgProxy != null)
            {
                _tgProxy.StatsUpdated -= OnTgProxyStats;
                _tgProxy.Dispose();
                _tgProxy = null;
            }
        }
        catch (Exception ex) { _logger.Debug(ex, "[VM] Dispose: _tgProxy cleanup failed"); }

        // 7. M2: detach the Zapret immediate-exit handler + dispose the manager.
        try
        {
            if (_zapret != null)
            {
                _zapret.ImmediateExitDetected -= OnZapretImmediateExit;
                _zapret.Dispose();
                _zapret = null;
            }
        }
        catch (Exception ex) { _logger.Debug(ex, "[VM] Dispose: _zapret cleanup failed"); }
#endif

        // 8. M3: stop the Zapret probe-elapsed timer (its callback captures the VM).
        try { StopZapretProbeElapsedTimer(); }
        catch (Exception ex) { _logger.Debug(ex, "[VM] Dispose: StopZapretProbeElapsedTimer failed"); }

        // 9. M4: cancel + dispose active toast CTS and invalidate the TgProxy toast
        // token so a pending delayed continuation no-ops instead of posting to a dead VM.
        try
        {
            _zapretAvBlockToastCts?.Cancel();
            _zapretAvBlockToastCts?.Dispose();
            _zapretAvBlockToastCts = null;
            _rulesToastCts?.Cancel();
            _rulesToastCts?.Dispose();
            _rulesToastCts = null;
            _tgProxyToastToken++;
        }
        catch (Exception ex) { _logger.Debug(ex, "[VM] Dispose: toast CTS cleanup failed"); }
    }

    // ── Theme ──

    private void ApplyTheme()
    {
        if (Application.Current != null)
        {
            // v2.40.x (Fix #7): resolve the effective variant from the
            // preference. "system" follows the OS appearance: we set
            // RequestedThemeVariant=Default so Avalonia tracks the platform,
            // but we ALSO read the OS variant explicitly because our custom
            // ThemeDictionaries (Light/Dark) don't reliably repaint on Default
            // alone — IsDarkTheme + the C#-resolved brush getters need a
            // concrete Light/Dark to read against.
            ThemeVariant effective;
            if (IsSystemThemePref)
            {
                Application.Current.RequestedThemeVariant = ThemeVariant.Default;
                effective = ReadOsThemeVariant();
            }
            else
            {
                effective = IsDarkThemePref ? ThemeVariant.Dark : ThemeVariant.Light;
                Application.Current.RequestedThemeVariant = effective;
            }
            IsDarkTheme = effective == ThemeVariant.Dark;
        }

        // DynamicResource bindings in XAML auto-update when the theme variant
        // changes — no manual refresh needed for those. But any brush
        // property that resolves from Application.Resources in C# (our
        // runtime-status badges + ServerViewModel.StatusDotBrush) is cached
        // in a read-only getter; we must re-fire PropertyChanged so the
        // binding re-reads the resolved value.
        OnPropertyChanged(nameof(VpnBadgeBrush));
        OnPropertyChanged(nameof(ZapretBadgeBrush));
        OnPropertyChanged(nameof(TgProxyBadgeBrush));

        foreach (var s in Servers)             s.NotifyThemeChanged();
        foreach (var s in SubscriptionServers) s.NotifyThemeChanged();
    }

    /// <summary>
    /// v2.40.x (Fix #7): read the OS appearance (Light/Dark) via Avalonia's
    /// PlatformSettings. Maps the platform-native signal — Windows registry
    /// AppsUseLightTheme, macOS NSAppearance, Linux freedesktop color-scheme —
    /// to a concrete <see cref="ThemeVariant"/>. Falls back to Light if the
    /// platform can't be queried (very early startup / headless).
    /// </summary>
    private static ThemeVariant ReadOsThemeVariant()
    {
        try
        {
            var os = Application.Current?.PlatformSettings?.GetColorValues().ThemeVariant;
            return os == PlatformThemeVariant.Dark ? ThemeVariant.Dark : ThemeVariant.Light;
        }
        catch
        {
            return ThemeVariant.Light;
        }
    }

    /// <summary>
    /// v2.40.x (Fix #7): coerce a persisted/raw theme string to one of the
    /// three canonical preferences. Unknown / null / legacy values fall back to
    /// "system" so fresh installs (and corrupted values) follow the OS.
    /// </summary>
    internal static string NormalizeThemePref(string? raw)
    {
        if (string.Equals(raw, "dark", StringComparison.OrdinalIgnoreCase)) return "dark";
        if (string.Equals(raw, "light", StringComparison.OrdinalIgnoreCase)) return "light";
        return "system";
    }

    /// <summary>
    /// v2.40.x (Fix #7): live OS appearance flip handler. Only re-applies while
    /// the preference is "system" — an explicit Light/Dark choice ignores OS
    /// changes. Marshalled to the UI thread because ApplyTheme touches
    /// Application.Current + raises PropertyChanged for the brush getters.
    /// </summary>
    private void OnPlatformColorValuesChanged(object? sender, PlatformColorValues e)
    {
        if (!IsSystemThemePref) return;
        Dispatcher.UIThread.Post(ApplyTheme);
    }

    // Held so Dispose can unsubscribe cleanly (avoids the leaked-handler /
    // double-fire class of bug the project already hit with DataContextChanged).
    private IPlatformSettings? _wiredPlatformSettings;

    private void WireOsThemeFollow()
    {
        if (_wiredPlatformSettings != null) return;   // wire exactly once
        try
        {
            var ps = Application.Current?.PlatformSettings;
            if (ps == null) return;
            ps.ColorValuesChanged += OnPlatformColorValuesChanged;
            _wiredPlatformSettings = ps;
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "[VM] WireOsThemeFollow: could not subscribe to ColorValuesChanged");
        }
    }

    private void UnwireOsThemeFollow()
    {
        try
        {
            if (_wiredPlatformSettings != null)
            {
                _wiredPlatformSettings.ColorValuesChanged -= OnPlatformColorValuesChanged;
                _wiredPlatformSettings = null;
            }
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "[VM] UnwireOsThemeFollow: unsubscribe failed");
        }
    }

    // ── Localization refresh ──

    private void RefreshLocalization()
    {
        ThemeToggleText = IsDarkTheme ? Strings.ThemeLight : Strings.ThemeDark;
        ConnectButtonText = IsConnected ? Strings.StopVPN : Strings.StartVPN;
        if (!IsConnected && !IsConnecting)
            StatusText = Strings.NotConnected;

        // Notify all properties — refreshes every Lbl* and other localized binding
        OnPropertyChanged(string.Empty);

        // Propagate to child view models — they have their own property notifiers
        foreach (var group in AppGroups.Concat(BypassAppGroups))
            group.NotifyDisplayNameChanged();
        foreach (var server in Servers)
            server.NotifyLocalizationChanged();
        foreach (var server in SubscriptionServers)
            server.NotifyLocalizationChanged();

        // v2.30.7-r3 — UpdateVm.CheckLinkText is computed from Strings;
        // it doesn't auto-refresh on lang change because OnPropertyChanged("")
        // only fires on the parent VM, not on child VMs.
        UpdateVm?.NotifyLangChanged();
    }

    // ── Helpers ──
    // Phase 2B (Wave 8, 2026-05-18) - DeployBundledProfiles moved to
    // MainWindowViewModel.Profiles.cs.

    private static Window? GetMainWindow()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            return desktop.MainWindow;
        return null;
    }

    // Phase 2B (Wave 8, 2026-05-18) — Free Configs apply path moved to
    // MainWindowViewModel.FreeConfigs.cs:
    //   - ApplyFreeConfigAsync
    //   - ShowFreeConfigSecurityWarningAsync
}
