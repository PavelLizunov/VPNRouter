using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VPNRouter.App.Localization;
using VPNRouter.Core;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.App.ViewModels;

/// <summary>
/// Simple-mode page bindings (v2.17+). Lives in a partial so the full
/// Advanced MainWindowViewModel stays readable and nothing about today's
/// behaviour changes when the user is on Advanced.
///
/// In v2.17.1 this is a SKELETON — properties are bound, the Start
/// command is a stub that only logs and surfaces a 'coming in v2.17.2'
/// message in the status line. v2.17.2 replaces the stub with real
/// input parsing + Connect wiring. v2.17.3 wires the autostart checkbox
/// through ServiceInstaller.
/// </summary>
public partial class MainWindowViewModel
{
    // ── Input ─────────────────────────────────────────────────────────────
    /// <summary>
    /// Free-text field on SimplePage. Accepts either a single <c>vless://</c>
    /// URI (becomes a one-server VLESS config) or an <c>http(s)://</c>
    /// subscription URL. Classification in v2.17.2 via SimpleInputDetector.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SmpInputDetectedKind))]
    [NotifyPropertyChangedFor(nameof(SmpInputDetectedHint))]
    [NotifyPropertyChangedFor(nameof(SmpInputDetectedHintVisible))]
    [NotifyPropertyChangedFor(nameof(SmpSaveEnabled))]
    [NotifyPropertyChangedFor(nameof(SmpRefreshEnabled))]
    [NotifyPropertyChangedFor(nameof(SmpInputDirty))]
    [NotifyPropertyChangedFor(nameof(SmpConnectEnabled))]
    private string _smpInput = string.Empty;

    /// <summary>Inline error message shown below the input (empty = no error).</summary>
    [ObservableProperty] private string _smpErrorText = string.Empty;

    /// <summary>
    /// v2.32.0 parity audit F-11 (2026-05-09): Transient toast shown above the
    /// SimplePage CTA (e.g. "Saved as Subscription"). Empty = no toast. Mirrors
    /// the existing <see cref="RulesToastText"/> pattern; cleared automatically
    /// after ~2.5 s via <see cref="ShowSmpToast"/>.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSmpToast))]
    private string _smpToastText = string.Empty;

    /// <summary>True when <see cref="SmpToastText"/> is non-empty — drives toast border IsVisible.</summary>
    public bool HasSmpToast => !string.IsNullOrEmpty(SmpToastText);

    /// <summary>
    /// v2.32.0 parity audit F-11 (2026-05-09): snapshot of the input that was
    /// last persisted via <see cref="SmpSave"/> (or auto-saved by
    /// <see cref="SmpToggleConnectAsync"/>). Used to compute
    /// <see cref="SmpInputDirty"/> so the Connect button stays disabled while
    /// the user has typed something but not yet saved it. Pre-fix Connect would
    /// silently flip ConfigMode and leave the user with an empty subscription
    /// (F-12 in <c>parity-audit/findings.md</c>).
    /// </summary>
    private string _smpInputSavedSnapshot = string.Empty;

    /// <summary>
    /// True when the input field contains text that hasn't been Saved yet —
    /// either a freshly-pasted URL the user hasn't committed, or an edited
    /// version of an already-saved one. Empty input is NOT dirty (lets
    /// upgraders Connect with an existing config without typing).
    /// </summary>
    public bool SmpInputDirty
    {
        get
        {
            var current = (_smpInput ?? string.Empty).Trim();
            if (current.Length == 0) return false;
            return !string.Equals(current, _smpInputSavedSnapshot, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Auto-detected kind of the current <see cref="SmpInput"/> — drives the
    /// "Detected: …" status hint below the field and gates Save/Refresh
    /// buttons. Pure computed property (no caching) — recomputed on every
    /// PropertyChanged via the NotifyPropertyChangedFor on SmpInput.
    /// </summary>
    public SmpInputKind SmpInputDetectedKind => SimpleInputDetector.Classify(_smpInput);

    /// <summary>
    /// Localized "Detected: VLESS server" / "Detected: Subscription URL" /
    /// "Detected: unknown — paste vless:// or https:// link" line displayed
    /// below the input. Mirrors Android's inline auto-detect feedback on the
    /// Subscribe sub-tab.
    /// </summary>
    public string SmpInputDetectedHint => SmpInputDetectedKind switch
    {
        SmpInputKind.ServerUri        => Strings.SmpInputDetectedServer,
        SmpInputKind.SubscriptionUrl  => Strings.SmpInputDetectedSubscription,
        _                              => string.Empty,
    };

    /// <summary>True when the input contains a recognised scheme — hides the hint when empty.</summary>
    public bool SmpInputDetectedHintVisible =>
        SmpInputDetectedKind != SmpInputKind.Invalid;

    /// <summary>
    /// IsEnabled binding for the Save button. Save needs a valid (vless / sub)
    /// input — wiring through this lets XAML disable the button visually
    /// rather than relying on Save() to silently no-op.
    /// </summary>
    public bool SmpSaveEnabled => SmpInputDetectedKind != SmpInputKind.Invalid && SmpInputDirty;

    /// <summary>
    /// IsEnabled binding for the Refresh button. Refresh only makes sense for
    /// subscribe mode AND once the user has a saved subscription (otherwise
    /// there's nothing to fetch).
    /// </summary>
    public bool SmpRefreshEnabled => IsSubscribeMode
        && (_settings?.App?.Subscriptions?.Any(s => s.Enabled && !string.IsNullOrWhiteSpace(s.Url)) == true);

    /// <summary>
    /// IsEnabled binding for the Connect CTA. F-12 (parity audit): Connect must
    /// stay disabled while the user has typed an URL but not Saved it, so
    /// SmpToggleConnectAsync's silent ConfigMode flip path is unreachable from
    /// the UI. Disconnect (already-connected) and Cancel (connecting) remain
    /// enabled — only the disconnected → Connect transition is gated.
    /// </summary>
    public bool SmpConnectEnabled
    {
        get
        {
            // Already in a tunnel transition — let the user Disconnect/Cancel.
            if (IsConnected || IsConnecting) return true;
            // Disconnected: only allow Connect if there is no unsaved input
            // sitting in the field. Empty input + existing saved config is
            // fine (upgrader/auto-Connect path).
            return !SmpInputDirty;
        }
    }

    /// <summary>
    /// Controls the "Change config or mode" Expander on SimplePage.
    /// v2.17.9 fix: previously the Expander bound `IsExpanded="{Binding !IsConnected}"`
    /// which Avalonia compiled as TwoWay — toggling the Expander wrote back
    /// through the `!` inverter and flipped <see cref="IsConnected"/>, making
    /// the hero card claim "VPN is off" while the engine was still running.
    /// A dedicated observable with explicit TwoWay binding avoids the trap and
    /// lets the user open/close the form without any side effect on engine state.
    /// Defaults to true so a freshly-opened Simple page (typically disconnected)
    /// shows the form without a click.
    /// </summary>
    [ObservableProperty] private bool _smpFormExpanded = true;

    // ── Autostart toggle ─────────────────────────────────────────────────
    /// <summary>
    /// Simple-mode 'Start with Windows' checkbox. Encapsulates two things
    /// Advanced shows separately:
    ///   1. Windows Service install + start (via ServiceVm.AutostartChecked).
    ///   2. AppSettings.App.AutostartVpn = true (so the service actually
    ///      auto-starts the VPN at boot, not just sits there idle).
    /// Unchecking removes the service and disables AutostartVpn.
    /// </summary>
    /// <summary>
    /// v2.27 Bug B fix — was `[ObservableProperty] _smpAutostartChecked`
    /// initialised once from <c>AutostartVpn</c>. That broke Advanced → Simple
    /// sync: if a user ticked the Advanced "Enable background service" master
    /// toggle, only <c>ServiceVm.AutostartChecked</c> flipped, <c>AutostartVpn</c>
    /// stayed false, and Simple's checkbox silently disagreed with reality.
    ///
    /// <para>Now a computed property over the three signals the user actually
    /// cares about: service installed, service running, and the "auto-start
    /// VPN at boot" flag that the Service reads at boot. All three must be
    /// true for "VPN starts with Windows" to be true. PropertyChanged re-fires
    /// from <c>OnAutostartVpnChanged</c> and the <c>ServiceVm.PropertyChanged</c>
    /// subscription wired in the constructor, so either side ticking a box
    /// makes the other surface the right value.</para>
    ///
    /// <para>Setter encapsulates the full enable/disable chain: install+start
    /// service, flip <c>AutostartVpn</c>, and only uninstall the service when
    /// no other component (Zapret / TgProxy) still needs it running.</para>
    /// </summary>
    public bool SmpAutostartChecked
    {
        get => ServiceVm.IsInstalled
               && ServiceVm.IsRunning
               && AutostartVpn;
        set
        {
            if (_isLoadingUI) return;
            if (SmpAutostartChecked == value) return;

            if (value)
            {
                // install + start service via ServiceVm setter (it handles
                // sc.exe calls + UI busy state). Flip the boot-autostart flag
                // so the service actually starts the VPN rather than idling.
                if (!ServiceVm.AutostartChecked)
                    ServiceVm.AutostartChecked = true;
                // v2.31.6-r8: write through the [ObservableProperty] backing
                // field instead of the YAML model directly. Pre-r8 this set
                // `_settings.App.AutostartVpn = true;` which persisted
                // correctly via the SaveSettings call below but did NOT raise
                // PropertyChanged on the bound `AutostartVpn` getter that the
                // Advanced-mode checkbox uses. Result: toggling Simple-mode
                // autostart didn't visibly update the Settings → Autostart
                // pane until the next LoadSettingsIntoUI. Going through the
                // property keeps both surfaces in sync and reuses the
                // partial-method `OnAutostartVpnChanged` (which already calls
                // SaveSettings) — so the explicit SaveSettings below is a
                // belt-and-braces no-op when the property setter saved.
                AutostartVpn = true;
            }
            else
            {
                AutostartVpn = false;
                // Only tear down the service when nothing else depends on it.
                // Keeping it installed for Zapret/TgProxy autostart is fine —
                // the service just won't bring up VPN at boot.
                var stillNeeded = _settings.App.AutostartZapret
                                  || _settings.App.AutostartTgProxy;
                if (!stillNeeded && ServiceVm.AutostartChecked)
                    ServiceVm.AutostartChecked = false;
            }
            SaveSettings();
            OnPropertyChanged(nameof(SmpAutostartChecked));
        }
    }

    /// <summary>
    /// Simple-mode split profile — comma-separated list of built-in profile
    /// names. ProfileManager's merge path unions their process rules. Covers
    /// the 'Discord + Browsers + Work apps' default approved with the user
    /// on 2026-04-20.
    /// </summary>
    // v2.22.0-r1: include all 8 standard groups so default Split routes
    // Discord + Messengers + AI tools + Gaming etc. through VPN out of the
    // box. Tolerant resolver skips anything the platform's catalogue
    // doesn't have, so a missing group on one OS doesn't break anything.
    public const string SimpleSplitProfile =
        "Discord_Privacy,Messengers,AI_Tools,Browsers,Work_Suite,Streaming,Gaming,Privacy_Shell";

    // ── Derived button state ─────────────────────────────────────────────
    /// <summary>Big Start/Stop button caption — flips with <see cref="IsConnected"/>.</summary>
    public string SmpConnectButtonText => IsConnected ? Strings.SmpStopVpn : Strings.SmpStartVpn;

    /// <summary>Big Start/Stop button background — emerald when idle, red when connected.</summary>
    public IBrush SmpConnectButtonBrush
    {
        get
        {
            var key = IsConnected ? "DangerSolidBrush" : "AccentSolidBrush";
            var app = Avalonia.Application.Current;
            if (app != null &&
                app.Resources.TryGetResource(key, app.ActualThemeVariant, out var res) &&
                res is IBrush brush)
                return brush;
            // Fallback — arctic / red straight values
            return IsConnected
                ? new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26))
                : new SolidColorBrush(Color.FromRgb(0x0E, 0xA5, 0xE9));
        }
    }

    /// <summary>Big hero-card title: "VPN is running" / "VPN is off".</summary>
    public string SmpHeroTitle => IsConnected ? Strings.SmpConnectedTitle : Strings.SmpDisconnectedTitle;

    /// <summary>"Through: de-01 · 104.194.156.93" info line shown in Connected state.</summary>
    public string SmpActiveServerLine
    {
        get
        {
            if (!IsConnected) return string.Empty;
            string? name = IsSubscribeMode
                ? (SelectedSubscriptionServer ?? SubscriptionServers.FirstOrDefault())?.DisplayName
                : (SelectedServer ?? Servers.FirstOrDefault())?.DisplayName;
            var ip = _engine?.ActiveServerAddress;
            if (string.IsNullOrEmpty(name) && string.IsNullOrEmpty(ip)) return string.Empty;
            if (string.IsNullOrEmpty(name)) return $"{Strings.SmpActiveThrough} {ip}";
            if (string.IsNullOrEmpty(ip))   return $"{Strings.SmpActiveThrough} {name}";
            return $"{Strings.SmpActiveThrough} {name} · {ip}";
        }
    }

    // ── v2.18.0 compact-design bindings ──────────────────────────────────
    // Modelled after "VPNRouter Design System 2/handoff/SimpleMode.html".
    // Three states — on / warn / off — each with its own status dot colour,
    // title, description, and CTA visual. Bindings expose 3 mutually-exclusive
    // bools per state so XAML can show/hide a .state-variant block cleanly.

    /// <summary>True when VPN is up and no connect/disconnect is in-flight.</summary>
    public bool SimpleStatusIsOn   => IsConnected && !IsConnecting;
    /// <summary>True only during an active connect/disconnect transition.</summary>
    public bool SimpleStatusIsWarn => IsConnecting;
    /// <summary>True when idle and not currently transitioning.</summary>
    public bool SimpleStatusIsOff  => !IsConnected && !IsConnecting;

    /// <summary>
    /// Status-card title — one word when possible. Mirrors the variant-A
    /// "Protected / Connecting… / Not connected" wording.
    /// </summary>
    public string SimpleStatusTitle => IsConnecting
        ? Strings.SmpStatusConnecting
        : IsConnected
            ? Strings.SmpStatusProtected
            : Strings.SmpStatusNotConnected;

    /// <summary>
    /// Status-card description — single line, varies with state:
    ///   on   → "Connected via de-01 · 104.194.156.93"
    ///   warn → "Handshaking with the server…"
    ///   off  → "Traffic goes straight — pick a config and start the tunnel."
    /// </summary>
    public string SimpleStatusDescription
    {
        get
        {
            if (IsConnecting) return Strings.SmpStatusConnectingHint;
            if (IsConnected)
            {
                // Reuse the SmpActiveServerLine logic but strip the "Through:"
                // prefix since the status card uses its own verb.
                string? name = IsSubscribeMode
                    ? (SelectedSubscriptionServer ?? SubscriptionServers.FirstOrDefault())?.DisplayName
                    : (SelectedServer ?? Servers.FirstOrDefault())?.DisplayName;
                var ip = _engine?.ActiveServerAddress;
                var via = Strings.SmpStatusConnectedVia;
                if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(ip)) return $"{via} {name} · {ip}";
                if (!string.IsNullOrEmpty(name)) return $"{via} {name}";
                if (!string.IsNullOrEmpty(ip))   return $"{via} {ip}";
                return Strings.SmpStatusConnectedNoDetails;
            }
            return Strings.SmpStatusDisconnectedHint;
        }
    }

    /// <summary>
    /// Config row value — "subscribe · split" / "manual · full" / etc. Shown
    /// in the compact config row as the currently-active routing picture.
    /// </summary>
    public string SimpleConfigModeSummary
    {
        get
        {
            var configLabel = IsSubscribeMode ? Strings.SmpCfgSubscribe
                             : IsVlessMode    ? Strings.SmpCfgManual
                             :                  Strings.SmpCfgCustom;
            var tunnelLabel = IsSplitTunnel ? Strings.SmpCfgSplit : Strings.SmpCfgFull;
            return $"{configLabel} · {tunnelLabel}";
        }
    }

    /// <summary>CTA button caption — "Connect" / "Disconnect" / "Cancel".</summary>
    public string SimpleCtaText => IsConnecting
        ? Strings.SmpCtaCancel
        : IsConnected
            ? Strings.SmpCtaDisconnect
            : Strings.SmpCtaConnect;

    /// <summary>
    /// CTA visual-state flags (mutually exclusive). XAML shows one of three
    /// Button variants based on which of these is true — avoids cramming
    /// state switches into a style trigger. Matches .cta.on-state /
    /// .cta.connecting-state / .cta.off-state in the HTML reference.
    /// </summary>
    public bool SimpleCtaIsConnected    => IsConnected && !IsConnecting;
    public bool SimpleCtaIsConnecting   => IsConnecting;
    public bool SimpleCtaIsDisconnected => !IsConnected && !IsConnecting;

    /// <summary>
    /// Config row click handler. In v2.18.0 this simply toggles the hidden
    /// detail form (same SmpFormExpanded bool the v2.17.x Expander used)
    /// so the user can still tweak input/mode inline without navigating to
    /// Advanced. Future: open a bottom-sheet picker.
    /// </summary>
    [RelayCommand]
    private void OpenConfigPicker()
    {
        SmpFormExpanded = !SmpFormExpanded;
    }

    // ── Big Start/Stop command (real wiring in v2.17.2) ──────────────────

    /// <summary>
    /// Simple-mode connect flow. If already connected → just Stop (reuses
    /// the existing ToggleConnection path). Otherwise parses the pasted
    /// input, writes the minimum settings needed (single VLESS entry OR
    /// a single-entry subscriptions list), and hands off to ToggleConnection
    /// which runs the same Connect logic Advanced uses — including
    /// service-managed detection and WarnServiceManagedReconnect.
    /// </summary>
    [RelayCommand]
    private async Task SmpToggleConnectAsync()
    {
        SmpErrorText = string.Empty;

        if (IsConnected)
        {
            await ToggleConnectionAsync();
            return;
        }

        if (IsConnecting) return;

        var kind = SimpleInputDetector.Classify(_smpInput);

        // Empty / garbage input is OK if the user already has a working
        // config in settings — we just connect with what's there. This is
        // the common case when Simple is opened on an install that was
        // already configured (upgrader, Advanced → Simple toggle, or
        // service-autostarted VPN that's currently running).
        var hasExistingConfig =
            (_settings.Vless.Servers?.Count > 0) ||
            (_settings.App.Subscriptions?.Any(s => s.Enabled && s.Servers.Count > 0) == true);

        if (kind == SmpInputKind.Invalid)
        {
            if (hasExistingConfig)
            {
                // No-op: skip parsing, fall through to RoutingMode + connect.
            }
            else
            {
                SmpErrorText = IsRussian
                    ? "Вставь ссылку (vless:// / hysteria2:// / tuic:// / ss://) или URL подписки (http:// / https://)."
                    : "Paste a server link (vless:// / hysteria2:// / tuic:// / ss://) or a subscription URL (http:// / https://).";
                return;
            }
        }
        else if (kind == SmpInputKind.ServerUri)
        {
            if (!TryApplyVless(_smpInput.Trim())) return;
            // F-11: keep the dirty/snapshot state in sync with what we
            // just persisted so a return trip to the form doesn't
            // re-disable Connect.
            _smpInputSavedSnapshot = _smpInput.Trim();
            OnPropertyChanged(nameof(SmpInputDirty));
        }
        else if (kind == SmpInputKind.SubscriptionUrl)
        {
            if (!TryApplySubscriptionUrl(_smpInput.Trim())) return;
            _smpInputSavedSnapshot = _smpInput.Trim();
            OnPropertyChanged(nameof(SmpInputDirty));
        }

        // Tunnel mode (Split vs Full) — already bound to IsSplitTunnel via radio.
        _settings.App.RoutingMode = IsSplitTunnel ? "split" : "full";

        // Simple-Split uses the hardcoded default profile. Full tunnel
        // ignores the profile field entirely.
        if (IsSplitTunnel)
            _settings.ActiveProfile = SimpleSplitProfile;

        SaveSettings();
        _settings = SettingsLoader.Load(AppPaths.ConfigYamlPath);

        // Subscription mode needs a fresh fetch BEFORE connect so we have
        // servers to hand to the engine.
        if (kind == SmpInputKind.SubscriptionUrl)
        {
            try
            {
                await RefreshAllSubscriptionsAsync();
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "[Simple] Subscription refresh failed");
                SmpErrorText = IsRussian
                    ? $"Не удалось получить подписку: {ex.Message}"
                    : $"Couldn't fetch the subscription: {ex.Message}";
                return;
            }
        }

        // Hand off to the shared Connect path — it already handles mode
        // dispatch, TUN conflicts, service-managed warnings, etc.
        await ToggleConnectionAsync();
    }

    /// <summary>
    /// v2.32.0 parity audit F-02 row 6 (2026-05-09): Save command for the
    /// SimplePage action row. Mirrors the parse + persist portion of
    /// <see cref="SmpToggleConnectAsync"/> but stops short of Connect, so
    /// users can persist a freshly-pasted URI / URL without committing
    /// to a tunnel start. Pre-fix the Save action lived only on the
    /// Subscribe Advanced page; F-02 row 6 wanted parity with Android,
    /// which has Save / Refresh / QR buttons inline on the main scroller.
    /// QR is omitted on desktop (no camera surface).
    ///
    /// <para>
    /// v2.32.0 parity audit F-11 (2026-05-09): on success, kicks off a
    /// background subscription refresh (subscribe mode) and surfaces a toast
    /// so the user has explicit feedback. Records the saved value in
    /// <see cref="_smpInputSavedSnapshot"/> so the Connect button (gated by
    /// <see cref="SmpConnectEnabled"/> via <see cref="SmpInputDirty"/>)
    /// re-enables.
    /// </para>
    /// </summary>
    [RelayCommand]
    private async Task SmpSaveAsync()
    {
        SmpErrorText = string.Empty;
        var raw = (_smpInput ?? string.Empty).Trim();
        if (raw.Length == 0)
        {
            SmpErrorText = IsRussian
                ? "Поле пусто — вставь vless:// / hysteria2:// / tuic:// / ss:// или https://-URL подписки."
                : "Empty — paste a vless:// / hysteria2:// / tuic:// / ss:// link or an https:// subscription URL.";
            return;
        }

        var kind = SimpleInputDetector.Classify(raw);
        if (kind == SmpInputKind.Invalid)
        {
            SmpErrorText = IsRussian
                ? "Не похоже на валидную ссылку или подписочный URL."
                : "Doesn't look like a valid share-link or subscription URL.";
            return;
        }

        if (kind == SmpInputKind.ServerUri && !TryApplyVless(raw)) return;
        if (kind == SmpInputKind.SubscriptionUrl && !TryApplySubscriptionUrl(raw)) return;

        _settings.App.RoutingMode = IsSplitTunnel ? "split" : "full";
        if (IsSplitTunnel)
            _settings.ActiveProfile = SimpleSplitProfile;

        SaveSettings();
        _settings = SettingsLoader.Load(AppPaths.ConfigYamlPath);

        // F-11: mark this value as the persisted snapshot so SmpInputDirty
        // flips false, which re-enables the Connect CTA + disables Save.
        _smpInputSavedSnapshot = raw;
        OnPropertyChanged(nameof(SmpInputDirty));
        OnPropertyChanged(nameof(SmpSaveEnabled));
        OnPropertyChanged(nameof(SmpRefreshEnabled));
        OnPropertyChanged(nameof(SmpConnectEnabled));

        // F-11: explicit user feedback. "Saved as Subscription" / "Saved as
        // VLESS server" — Android shows the same after a Save tap.
        var toast = kind == SmpInputKind.SubscriptionUrl
            ? Strings.SmpSavedAsSubscriptionToast
            : Strings.SmpSavedAsServerToast;
        ShowSmpToast(toast);

        // F-11: subscription mode needs a fetch before the user clicks
        // Connect — otherwise the Servers tab is empty and Connect would
        // bail (or worse, in F-12 silently flip ConfigMode without telling
        // the user). Fire-and-forget; refresh failure is surfaced via the
        // existing _logger + the Refresh button stays clickable for retry.
        if (kind == SmpInputKind.SubscriptionUrl)
        {
            try
            {
                await RefreshAllSubscriptionsAsync();
                ShowSmpToast(Strings.SmpRefreshDoneToast);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "[Simple] SmpSave background refresh failed");
                // Don't clear the "Saved" toast with an error — leave the
                // success toast visible and let the user retry via Refresh.
            }
        }
    }

    /// <summary>
    /// v2.32.0 parity audit F-11 (2026-05-09): set <see cref="SmpToastText"/>,
    /// schedule a 2500 ms revert. Multiple calls cancel any pending revert so
    /// rapid Save → Refresh shows the latest message rather than blinking.
    /// </summary>
    private void ShowSmpToast(string message)
    {
        SmpToastText = message;
        var token = ++_smpToastToken;
        _ = System.Threading.Tasks.Task.Delay(2500).ContinueWith(_ =>
        {
            if (token == _smpToastToken)
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (token == _smpToastToken) SmpToastText = string.Empty;
                });
            }
        });
    }

    private int _smpToastToken;

    /// <summary>
    /// v2.32.0 parity audit F-11 (2026-05-09): Simple-page-specific Refresh
    /// wrapper. Delegates the actual fetching to
    /// <see cref="RefreshAllSubscriptionsAsync"/> (the Subscribe page command)
    /// then surfaces a toast so the user has explicit "did anything happen?"
    /// feedback. Pre-fix the SimplePage Refresh button was bound directly to
    /// RefreshAllSubscriptionsCommand, which silently completed in the
    /// background — Android's equivalent shows an inline "Subscription
    /// refreshed" status.
    /// </summary>
    [RelayCommand]
    private async Task SmpRefreshAsync()
    {
        SmpErrorText = string.Empty;
        ShowSmpToast(Strings.Syncing);
        try
        {
            await RefreshAllSubscriptionsAsync();
            ShowSmpToast(Strings.SmpRefreshDoneToast);
            // Refresh changed Subscriptions[].Servers — re-evaluate gating
            // properties so the button states match the new data.
            OnPropertyChanged(nameof(SmpRefreshEnabled));
            OnPropertyChanged(nameof(SmpConnectEnabled));
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[Simple] SmpRefresh failed");
            SmpToastText = string.Empty;
            SmpErrorText = IsRussian
                ? $"Не удалось получить подписку: {ex.Message}"
                : $"Couldn't fetch the subscription: {ex.Message}";
        }
    }

    /// <summary>
    /// Parse a single <c>vless://</c> URI and write it as the only VLESS
    /// server in settings. Returns false and sets SmpErrorText on failure.
    /// </summary>
    private bool TryApplyVless(string uri)
    {
        try
        {
            // v2.30.1-r3: dispatch by scheme (vless / hysteria2 / hy2 / tuic / ss).
            var entry = ServerUriParser.Parse(uri);

            // v2.30.1-r3 bug fix: write BOTH the settings model AND the VM
            // observable collection. SaveSettings rebuilds
            // _settings.Vless.Servers from VM Servers (line 2912) right
            // before the YAML write — so mutating only the settings side
            // gets immediately undone, and the just-pasted server is
            // wiped out. The next ToggleConnectionAsync then connects
            // with whatever was in the OLD Servers list, and the user's
            // pasted URL never surfaces in settings.yaml. Mirrors the
            // pattern in TryApplySubscriptionUrl.
            //
            // User report 2026-05-01: "вставил vless ссылку … но у меня
            // запустилась не она, а какой-то влесс конфиг из уже
            // сохраненных. а она даже не сохранилась".
            _settings.Vless.Servers = new List<VlessServerEntry> { entry };
            _settings.Vless.ActiveServer = entry.Name ?? string.Empty;

            // Replace VM Servers collection so SaveSettings persists the
            // freshly-pasted entry.
            Servers.Clear();
            var vm = new ServerViewModel(entry);
            Servers.Add(vm);
            SelectedServer = vm;

            _settings.App.ConfigMode = "generated";
            _settings.App.ActiveSubscriptionServer = string.Empty;
            IsSubscribeMode = false;
            IsVlessMode = true;
            return true;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[Simple] Server URI parse failed");
            SmpErrorText = IsRussian
                ? "Некорректная ссылка. Поддерживаются vless:// / hysteria2:// / tuic:// / ss://, должна заканчиваться '#имя'."
                : "Invalid server link. Supported: vless:// / hysteria2:// / tuic:// / ss://, must end with '#name'.";
            return false;
        }
    }

    /// <summary>
    /// Replace the subscriptions list with a single enabled entry pointing
    /// at the pasted URL.
    ///
    /// <para>
    /// v2.21.3 bug fix: we must update BOTH <c>_settings.App.Subscriptions</c>
    /// AND the VM's observable <see cref="Subscriptions"/> collection.
    /// <see cref="SaveSettings"/> rebuilds <c>_settings.App.Subscriptions</c>
    /// from the VM collection right before the YAML write, so mutating
    /// only the settings side was immediately undone — the pasted URL
    /// would vanish after Save, and users had to add the subscription
    /// through Advanced mode instead. Clear + add a fresh
    /// SubscriptionViewModel to keep both sides in sync.
    /// </para>
    /// </summary>
    private bool TryApplySubscriptionUrl(string url)
    {
        var entry = new SubscriptionEntry
        {
            Name = "simple",
            Url = url,
            Enabled = true,
            Servers = new List<VlessServerEntry>(),
        };
        _settings.App.Subscriptions = new List<SubscriptionEntry> { entry };

        // Keep VM side in sync — SaveSettings() reads from here.
        Subscriptions.Clear();
        Subscriptions.Add(new SubscriptionViewModel(entry));

        _settings.App.ConfigMode = "subscribe";
        IsSubscribeMode = true;
        IsVlessMode = false;
        return true;
    }

    // ── Split/Full toggle auto-apply (v2.17.6) ────────────────────────────
    /// <summary>
    /// Simple-mode expects an immediate effect from the tunnel mode radio,
    /// unlike Advanced which has an explicit 'Apply' button. So the handler
    /// here:
    ///   - Always saves the new RoutingMode to YAML.
    ///   - Marks HasPendingAppChanges so Advanced users (if they toggled via
    ///     Simple → switched back) still see the amber Apply button.
    ///   - In Simple mode: if currently connected, auto-calls
    ///     ApplyPendingChangesAsync — which already handles
    ///     IsServiceManagedVpn correctly (from v2.16.8) by saving + showing
    ///     'Stop and Start VPN to apply' instead of fighting TUN ownership.
    ///
    /// This fixes the v2.17.5 bug where toggling Full↔Split while VPN ran
    /// via the Windows Service silently did nothing.
    /// </summary>
    partial void OnIsSplitTunnelChanged(bool value)
    {
        if (_isLoadingUI) return;

        _settings.App.RoutingMode = value ? "split" : "full";
        if (value)
            _settings.ActiveProfile = SimpleSplitProfile;
        SaveSettings();

        HasPendingAppChanges = IsConnected;

        if (IsSimpleMode && IsConnected && !IsConnecting)
        {
            // v2.20.4: force a full restart instead of hot-reload.
            // sing-box's Clash API PUT /configs accepts split↔full config
            // changes but DOES NOT rebuild the TUN routing table, so users
            // saw the toggle do nothing on both Windows and macOS. Calling
            // the internal entry point with forceRestart=true stops the
            // process and relaunches it with the new config.
            _ = ApplyPendingChangesInternalAsync(forceRestart: true);
        }
    }

    // ── Autostart wiring (v2.17.3) ────────────────────────────────────────
    /// <summary>
    /// When the Simple-mode 'Start with Windows' toggle changes, mirror
    /// the two underlying knobs:
    ///   - ServiceVm.AutostartChecked — installs/removes the Windows Service
    ///     (and starts it on install). Existing ServiceVm code already
    ///     handles UAC, elevation, idempotency.
    ///   - _settings.App.AutostartVpn — flag the Service reads on boot to
    ///     decide whether to auto-start the VPN. Off = service is installed
    ///     but sits idle until the user manually starts VPN.
    /// </summary>
    // OnSmpAutostartCheckedChanged removed — logic folded into the
    // SmpAutostartChecked setter above now that the property is computed.
}
