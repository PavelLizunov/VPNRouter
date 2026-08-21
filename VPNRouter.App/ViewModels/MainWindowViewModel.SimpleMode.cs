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
    [ObservableProperty] private string _smpInput = string.Empty;

    /// <summary>Inline error message shown below the input (empty = no error).</summary>
    [ObservableProperty] private string _smpErrorText = string.Empty;

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
            // G2: use the shared label resolver so auto-select shows the REAL
            // node (clash_api), not the nominal pick — name & ip stay consistent.
            var (name, ip) = DeriveConnectedServerLabel();
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

    /// <summary>
    /// v2.41.2-r3 (rectuspc): the post-start probe / AutoFailover can report the
    /// active config dead (server unreachable, no candidates) while the engine
    /// still holds IsConnected=true (TUN is up) — a silent dead "Connected".
    /// Simple Mode doesn't bind classic StatusText, so we surface that message
    /// through the status card here. Set by OnAutoFailoverMessage, cleared on the
    /// next connect attempt (OnIsConnectingChanged). Null = no active alert.
    /// </summary>
    private string? _lastConnectionAlert;

    /// <summary>True while a dead-config alert is active (overrides a deceptive
    /// green "Protected").</summary>
    private bool HasConnectionAlert => !string.IsNullOrEmpty(_lastConnectionAlert);

    /// <summary>Raise change-notification for every status-card member that
    /// depends on <see cref="_lastConnectionAlert"/>. Shared by the failover
    /// handler (sets the alert) and the connect hook (clears it).</summary>
    private void RaiseSimpleAlertProps()
    {
        OnPropertyChanged(nameof(SimpleStatusIsOn));
        OnPropertyChanged(nameof(SimpleStatusIsWarn));
        OnPropertyChanged(nameof(SimpleStatusIsOff));
        OnPropertyChanged(nameof(SimpleStatusTitle));
        OnPropertyChanged(nameof(SimpleStatusDescription));
    }

    /// <summary>MVVM Toolkit hook — a new connect attempt clears any stale
    /// dead-config alert from a previous session so the card reflects the
    /// fresh attempt, not the last failure.</summary>
    partial void OnIsConnectingChanged(bool value)
    {
        if (value && HasConnectionAlert)
        {
            _lastConnectionAlert = null;
            RaiseSimpleAlertProps();
        }
    }

    /// <summary>True when VPN is up, no transition in flight, and the tunnel
    /// hasn't been reported dead.</summary>
    public bool SimpleStatusIsOn   => IsConnected && !IsConnecting && !HasConnectionAlert;
    /// <summary>True during an active transition OR while the active config is
    /// reported dead.</summary>
    public bool SimpleStatusIsWarn => IsConnecting || HasConnectionAlert;
    /// <summary>True when idle and not currently transitioning.</summary>
    public bool SimpleStatusIsOff  => !IsConnected && !IsConnecting && !HasConnectionAlert;

    /// <summary>
    /// Status-card title — one word when possible. Mirrors the variant-A
    /// "Protected / Connecting… / Not connected" wording. A dead-config alert
    /// downgrades a deceptive "Protected" to "Not connected" — the tunnel is up
    /// but carries no traffic, so claiming "Protected" would be a lie.
    /// </summary>
    public string SimpleStatusTitle => IsConnecting
        ? Strings.SmpStatusConnecting
        : (IsConnected && !HasConnectionAlert)
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
            // Dead-config alert wins over both the connected and idle text —
            // it can be active while IsConnected is still true (silent dead
            // "Connected"). rectuspc v2.41.2-r3.
            if (HasConnectionAlert) return _lastConnectionAlert!;
            if (IsConnected)
            {
                // G2: shared resolver — auto-select shows the REAL node, not the
                // nominal pick (name & ip consistent).
                var (name, ip) = DeriveConnectedServerLabel();
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
            var configuredMode = _settings.App.ConfigMode ?? "generated";
            var configLabel = configuredMode.Equals("subscribe", StringComparison.OrdinalIgnoreCase)
                ? Strings.SmpCfgSubscribe
                : configuredMode.Equals("generated", StringComparison.OrdinalIgnoreCase)
                    ? Strings.SmpCfgManual
                    : Strings.SmpCfgCustom;
            var tunnelLabel = IsSplitTunnel ? Strings.SmpCfgSplit : Strings.SmpCfgFull;
            return $"{configLabel} · {tunnelLabel}";
        }
    }

    /// <summary>
    /// Bug-r9-F-DEFENSIVE (2026-05-11) — visible "via name@ip:port" line for
    /// the currently active proxy outbound. Empty when disconnected or when
    /// the engine hasn't reported an active address yet. Used by SimplePage
    /// to surface the server users are actually routing through, so a stale
    /// Custom Config Mode placeholder pointing to a hostile / dead IP can be
    /// spotted at a glance instead of silently leaking traffic.
    /// </summary>
    public string SimpleActiveOutboundLine
    {
        get
        {
            var ip = _engine?.ActiveServerAddress;
            if (string.IsNullOrEmpty(ip)) return string.Empty;

            var configuredMode = _settings.App.ConfigMode ?? "generated";
            string? name = configuredMode.Equals("subscribe", StringComparison.OrdinalIgnoreCase)
                ? (SelectedSubscriptionServer ?? SubscriptionServers.FirstOrDefault())?.DisplayName
                : configuredMode.Equals("generated", StringComparison.OrdinalIgnoreCase)
                    ? (SelectedServer ?? Servers.FirstOrDefault())?.DisplayName
                    : null; // Custom mode — no per-server name from settings.

            return string.IsNullOrEmpty(name)
                ? $"{Strings.SmpStatusConnectedVia} {ip}"
                : $"{Strings.SmpStatusConnectedVia} {name}@{ip}";
        }
    }

    /// <summary>
    /// True when the currently active proxy outbound dials a server that is
    /// NOT in any registered subscription, manual VLESS list, or legacy
    /// single-server field. Drives the red tint + warning glyph on
    /// SimplePage so silent Custom Config Mode placeholders are visible.
    /// Mirrors <see cref="VPNRouter.Core.Services.LeakProtection"/>'s
    /// known-server set logic.
    /// </summary>
    public bool SimpleActiveOutboundIsSuspect
    {
        get
        {
            var ip = _engine?.ActiveServerAddress;
            if (string.IsNullOrEmpty(ip) || _settings == null)
                return false;

            return !IsServerKnown(ip!, _settings);
        }
    }

    /// <summary>Visible when the active-outbound line should render in the
    /// normal (muted) style — connected, IP known.</summary>
    public bool SimpleActiveOutboundNormalVisible
        => !string.IsNullOrEmpty(SimpleActiveOutboundLine) && !SimpleActiveOutboundIsSuspect;

    /// <summary>Visible when the active-outbound line should render in the
    /// suspect (danger-tinted + ⚠) style — IP not in known servers.</summary>
    public bool SimpleActiveOutboundSuspectVisible
        => !string.IsNullOrEmpty(SimpleActiveOutboundLine) && SimpleActiveOutboundIsSuspect;

    private static bool IsServerKnown(string ip, AppSettings settings)
    {
        bool MatchSet(IEnumerable<VlessServerEntry>? list)
        {
            if (list == null) return false;
            foreach (var s in list)
            {
                if (s != null && !string.IsNullOrWhiteSpace(s.Server)
                    && string.Equals(s.Server.Trim(), ip, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        var legacy = settings.Vless?.Server;
        if (!string.IsNullOrWhiteSpace(legacy)
            && string.Equals(legacy!.Trim(), ip, StringComparison.OrdinalIgnoreCase))
            return true;

        if (MatchSet(settings.Vless?.Servers)) return true;
        if (MatchSet(settings.App?.SubscriptionServers)) return true;

        var subs = settings.App?.Subscriptions;
        if (subs != null)
        {
            foreach (var sub in subs)
            {
                if (MatchSet(sub?.Servers)) return true;
            }
        }

        return false;
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
                    ? "Вставь ссылку (vless:// / hysteria2:// / tuic:// / ss:// / naive://) или URL подписки (http:// / https://)."
                    : "Paste a server link (vless:// / hysteria2:// / tuic:// / ss:// / naive://) or a subscription URL (http:// / https://).";
                return;
            }
        }
        else if (kind == SmpInputKind.ServerUri)
        {
            if (!TryApplyVless(_smpInput.Trim())) return;
        }
        else if (kind == SmpInputKind.SubscriptionUrl)
        {
            if (!TryApplySubscriptionUrl(_smpInput.Trim())) return;
        }

        // Tunnel mode (Split vs Full) — already bound to IsSplitTunnel via radio.
        _settings.App.RoutingMode = IsSplitTunnel ? "split" : "full";

        // Simple-Split uses the hardcoded default profile. Full tunnel
        // ignores the profile field entirely.
        if (IsSplitTunnel)
            _settings.ActiveProfile = SimpleSplitProfile;

        SaveSettings();
        _settings = _settingsStore.Load(AppPaths.ConfigYamlPath);

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
                    ? $"Не удалось получить подписку: {CrashReporter.ScrubSecrets(ex.Message)}"
                    : $"Couldn't fetch the subscription: {CrashReporter.ScrubSecrets(ex.Message)}";
                return;
            }
        }

        // G1 Smart Connect: probe the subscription pool and land on a LIVE
        // server before bringing the tunnel up — never connect blind to a dead
        // one (the Latvia-HY2 i/o-timeout that caused "часто теряется"). Keeps
        // the active pick if it's alive; else fastest live; else honest error.
        // Inlined (no new VM member) so the characterization surface is unchanged;
        // the pick decision is unit-tested in ServerHealthProbe.PickForConnect.
        if ((_settings.App.ConfigMode ?? "generated")
            .Equals("subscribe", StringComparison.OrdinalIgnoreCase))
        {
            var candidates = (_settings.App.Subscriptions
                    ?? new System.Collections.Generic.List<SubscriptionEntry>())
                .Where(s => s.Enabled)
                .SelectMany(s => s.Servers ?? Enumerable.Empty<VlessServerEntry>())
                .ToList();

            if (candidates.Count > 0)
            {
                StatusText = IsRussian ? "Подбираем рабочий сервер…" : "Finding a working server…";
                try
                {
                    var results = await new ServerHealthProbe(_logger)
                        .ProbeAllAsync(candidates, TimeSpan.FromSeconds(4));
                    var chosen = ConnectionIntentScorer.PickServer(
                        results,
                        _settings.App.ConnectionIntent,
                        _settings.App.ActiveSubscriptionServer);

                    if (chosen == null)
                    {
                        SmpErrorText = IsRussian
                            ? "Все серверы недоступны — проверь подписку или интернет."
                            : "All servers are unreachable — check your subscription or internet.";
                        return;
                    }

                    if (!string.Equals(chosen.Name, _settings.App.ActiveSubscriptionServer, StringComparison.Ordinal))
                    {
                        _logger.Information(
                            "[SmartConnect] active server unreachable/unset — switching to live '{Name}'", chosen.Name);
                        _settings.App.ActiveSubscriptionServer = chosen.Name ?? _settings.App.ActiveSubscriptionServer;

                        // Sync UI selection to winner before SaveSettings re-derives from it.
                        var winnerVm = SubscriptionServers.FirstOrDefault(s => s.Name == chosen.Name);
                        if (winnerVm is not null)
                            SelectedSubscriptionServer = winnerVm;

                        SaveSettings();
                    }

                    if (ConnectionIntent.Normalize(_settings.App.ConnectionIntent) != ConnectionIntent.General)
                    {
                        StatusText = $"{ConnectionIntentStatusText} -> {chosen.Name}";
                    }
                }
                catch (Exception ex)
                {
                    // Probe failure must never block connect — fall through.
                    _logger.Warning(ex, "[SmartConnect] probe failed — connecting without pre-flight");
                }
            }
        }

        // Hand off to the shared Connect path — it already handles mode
        // dispatch, TUN conflicts, service-managed warnings, etc.
        await ToggleConnectionAsync();
    }

    /// <summary>
    /// Parse a single <c>vless://</c> URI and write it as the only VLESS
    /// server in settings. Returns false and sets SmpErrorText on failure.
    /// </summary>
    private bool TryApplyVless(string uri)
    {
        try
        {
            // r8 #4: NaiveProxy needs libcronet (Windows/Linux). On a platform
            // without it, say so clearly instead of letting the parser throw and
            // blaming a generic "invalid link".
            if (uri.StartsWith("naive", StringComparison.OrdinalIgnoreCase) &&
                !ServerUriParser.NaiveRuntimeAvailable)
            {
                SmpErrorText = IsRussian
                    ? "NaiveProxy работает только на Windows и Linux (нужен libcronet)."
                    : "NaiveProxy works only on Windows and Linux (needs libcronet).";
                return false;
            }

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
                ? "Некорректная ссылка. Поддерживаются vless:// / hysteria2:// / tuic:// / ss:// / naive://, должна заканчиваться '#имя'."
                : "Invalid server link. Supported: vless:// / hysteria2:// / tuic:// / ss:// / naive://, must end with '#name'.";
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

        MarkRoutingSettingsChanged();

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
