using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VPNRouter.App.Localization;

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
    /// Mirror of the Simple-mode autostart checkbox. v2.17.3 wires this
    /// into ServiceInstaller.Install / Uninstall + AppSettings.AutostartVpn.
    /// For the skeleton release it's a plain observable value only.
    /// </summary>
    [ObservableProperty] private bool _smpAutostartChecked;

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

    // ── Big Start/Stop command (STUB in v2.17.1) ─────────────────────────

    /// <summary>
    /// v2.17.1 stub. v2.17.2 replaces with real input parsing + connect.
    /// </summary>
    [RelayCommand]
    private Task SmpToggleConnectAsync()
    {
        SmpErrorText = string.Empty;
        StatusText = IsRussian
            ? "Логика Start/Stop будет доступна в v2.17.2. Сейчас это заглушка."
            : "Start/Stop logic lands in v2.17.2. This is currently a stub.";
        _logger?.Information("[Simple] SmpToggleConnect stub triggered — input length={Len}, split={Split}", _smpInput.Length, IsSplitTunnel);
        return Task.CompletedTask;
    }

    // ── Change-tracking hooks so the header button caption + colour refresh
    //    when IsConnected flips ───────────────────────────────────────────
    partial void OnSmpAutostartCheckedChanged(bool value)
    {
        // v2.17.3: actually install/remove service here. v2.17.1 is just a log.
        _logger?.Information("[Simple] Autostart checkbox → {Value} (no-op until v2.17.3)", value);
    }
}
