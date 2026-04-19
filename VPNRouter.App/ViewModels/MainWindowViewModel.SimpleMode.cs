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

    // ── Autostart toggle ─────────────────────────────────────────────────
    /// <summary>
    /// Simple-mode 'Start with Windows' checkbox. Encapsulates two things
    /// Advanced shows separately:
    ///   1. Windows Service install + start (via ServiceVm.AutostartChecked).
    ///   2. AppSettings.App.AutostartVpn = true (so the service actually
    ///      auto-starts the VPN at boot, not just sits there idle).
    /// Unchecking removes the service and disables AutostartVpn.
    /// </summary>
    [ObservableProperty] private bool _smpAutostartChecked;

    /// <summary>
    /// Simple-mode split profile — comma-separated list of built-in profile
    /// names. ProfileManager's merge path unions their process rules. Covers
    /// the 'Discord + Browsers + Work apps' default approved with the user
    /// on 2026-04-20.
    /// </summary>
    public const string SimpleSplitProfile = "Browsers,Discord_Privacy,Work_Suite";

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
                    ? "Вставь vless://-ссылку или URL подписки (http:// / https://)."
                    : "Paste a vless:// link or a subscription URL (http:// / https://).";
                return;
            }
        }
        else if (kind == SmpInputKind.Vless)
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
    /// Parse a single <c>vless://</c> URI and write it as the only VLESS
    /// server in settings. Returns false and sets SmpErrorText on failure.
    /// </summary>
    private bool TryApplyVless(string uri)
    {
        try
        {
            var entry = VlessUriParser.Parse(uri);
            _settings.Vless.Servers = new List<VlessServerEntry> { entry };
            _settings.Vless.ActiveServer = entry.Name ?? string.Empty;
            _settings.App.ConfigMode = "generated";
            _settings.App.ActiveSubscriptionServer = string.Empty;
            IsSubscribeMode = false;
            IsVlessMode = true;
            return true;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[Simple] VLESS URI parse failed");
            SmpErrorText = IsRussian
                ? "Некорректная vless-ссылка. Проверь что начинается на 'vless://' и заканчивается '#имя'."
                : "Invalid VLESS link. Make sure it starts with 'vless://' and ends with '#name'.";
            return false;
        }
    }

    /// <summary>
    /// Replace the subscriptions list with a single enabled entry pointing
    /// at the pasted URL. Returns false (currently unreachable — HTTP
    /// validation happens later during RefreshAllSubscriptionsAsync).
    /// </summary>
    private bool TryApplySubscriptionUrl(string url)
    {
        _settings.App.Subscriptions = new List<SubscriptionEntry>
        {
            new()
            {
                Name = "simple",
                Url = url,
                Enabled = true,
                Servers = new List<VlessServerEntry>(),
            }
        };
        _settings.App.ConfigMode = "subscribe";
        IsSubscribeMode = true;
        IsVlessMode = false;
        return true;
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
    partial void OnSmpAutostartCheckedChanged(bool value)
    {
        if (_isLoadingUI) return;

        // Mirror to ServiceVm → install+start or stop+uninstall.
        if (ServiceVm.AutostartChecked != value)
            ServiceVm.AutostartChecked = value;

        // Flip AppSettings.AutostartVpn so the running Service knows whether
        // to bring up VPN at boot (service re-reads config.yaml on start).
        _settings.App.AutostartVpn = value;
        SaveSettings();
    }
}
