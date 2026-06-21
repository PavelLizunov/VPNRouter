using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.UI.Controls;

namespace VPNRouter.Android;

public partial class AndroidApp
{
    // ── Settings (Network) tab event handlers ───────────────────────────
    // Settings now lives inside the Advanced shell as the Network tab. The
    // standalone fullscreen overlay is gone — the kebab "Settings" entry
    // and the Simple-page autostart inline card both deeplink here.

    private void OnMenuSettingsClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_kebabPopup is not null) _kebabPopup.IsOpen = false;
        ShowSettings();
    }

    /// <summary>
    /// Deeplink: open the Advanced shell on the Settings tab. Re-seeds
    /// control state if the tab body has already been built. Replaces the
    /// old fullscreen Settings overlay path (gone in AND-MIGRATE-OVERLAYS).
    /// AND-ADV-CHROME (2026-05-10): tab renamed Network → Settings to
    /// match desktop v2.32.0.
    /// </summary>
    private void ShowSettings()
    {
        OpenAdvancedShell(AdvancedTab.Settings);
    }

    /// <summary>
    /// Re-seed Network-tab controls from <see cref="AndroidStorage"/> when
    /// the tab is selected. Called by the Advanced shell on tab switch +
    /// shell open. Mirrors the old ShowSettings re-seed body.
    /// </summary>
    private void ReseedNetworkTabState()
    {
        _settingsLoading = true;
        try
        {
            var routing = AndroidStorage.GetRoutingMode();
            if (_settingsSplitRadio is not null) _settingsSplitRadio.IsChecked = routing == "split";
            if (_settingsFullRadio is not null) _settingsFullRadio.IsChecked = routing == "full";
            if (_settingsBypassRu is not null) _settingsBypassRu.IsChecked = AndroidStorage.GetBypassRussianTraffic();
            if (_settingsBlockOnVpnFail is not null) _settingsBlockOnVpnFail.IsChecked = AndroidStorage.GetBlockOnVpnFail();
            if (_settingsBlockAds is not null) _settingsBlockAds.IsChecked = AndroidStorage.GetBlockAds();
            if (_settingsDnsStrategy is not null)
            {
                _settingsDnsStrategy.SelectedIndex = AndroidStorage.GetDnsStrategy() switch
                {
                    "prefer_ipv4" => 1,
                    "prefer_ipv6" => 2,
                    _ => 0,
                };
            }
            if (_settingsReceivePrereleases is not null)
                _settingsReceivePrereleases.IsChecked = AndroidStorage.GetUpdateChannel() == "experimental";
            if (_settingsCurrentVersion is not null) _settingsCurrentVersion.Text = VPNRouter.Core.AppVersion.Version;
            if (_settingsAutostartVpn is not null) _settingsAutostartVpn.IsChecked = AndroidStorage.GetAutostartVpn();
            if (_settingsAutostartZapret is not null) _settingsAutostartZapret.IsChecked = AndroidStorage.GetAutostartZapret();
            if (_settingsAutostartTgProxy is not null) _settingsAutostartTgProxy.IsChecked = AndroidStorage.GetAutostartTgProxy();
            if (_settingsDpiBypassMode is not null)
            {
                _settingsDpiBypassMode.SelectedIndex = AndroidStorage.GetDpiBypassMode() switch
                {
                    "standard" => 1,
                    "aggressive" => 2,
                    _ => 0,
                };
            }
            if (_reliabilityAutoReconnect is not null)
                _reliabilityAutoReconnect.IsChecked = AndroidStorage.GetAutoReconnectOnNetworkChange();
            UpdateBatteryOptimizationStatus();

            // Phase C (2026-05-10): re-applying stored values via the
            // setters above doesn't go through the OnSettings*Changed
            // path (we're inside _settingsLoading), so we don't pick up
            // a spurious dirty mark. Restore the persisted sub-section
            // selection so the user lands on the same pane they last
            // visited, and refresh the footer in case this is the first
            // open of the tab (badges weren't built before now).
            var persisted = AndroidStorage.GetSettingsActiveSubSection();
            if (persisted != _settingsSelectedSubSection)
                SelectSettingsSubSection(persisted);
            UpdateSettingsFooterVisibility();
        }
        finally
        {
            _settingsLoading = false;
        }
    }

    private void OnSettingsRoutingChanged(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_settingsLoading) return;
        // RadioButton group fires IsCheckedChanged on both the now-off and
        // now-on members; we react only to the new "on" state to avoid
        // double-write. Falls back to "split" if neither radio is checked
        // (initial transient state during construction).
        var splitOn = _settingsSplitRadio?.IsChecked == true;
        var fullOn = _settingsFullRadio?.IsChecked == true;
        if (!splitOn && !fullOn) return;
        var newMode = splitOn ? "split" : "full";
        if (AndroidStorage.GetRoutingMode() == newMode) return;
        AndroidStorage.SetRoutingMode(newMode);
        MarkSettingsDirty();
    }

    private void OnSettingsBypassRuChanged(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_settingsLoading || _settingsBypassRu is null) return;
        AndroidStorage.SetBypassRussianTraffic(_settingsBypassRu.IsChecked == true);
        MarkSettingsDirty();
    }

    private void OnSettingsBlockOnVpnFailChanged(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_settingsLoading || _settingsBlockOnVpnFail is null) return;
        AndroidStorage.SetBlockOnVpnFail(_settingsBlockOnVpnFail.IsChecked == true);
        MarkSettingsDirty();
    }

    private void OnSettingsBlockAdsChanged(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_settingsLoading || _settingsBlockAds is null) return;
        AndroidStorage.SetBlockAds(_settingsBlockAds.IsChecked == true);
        MarkSettingsDirty();
    }

    private void OnSettingsDnsStrategyChanged(object? sender, Avalonia.Controls.SelectionChangedEventArgs e)
    {
        if (_settingsLoading || _settingsDnsStrategy is null) return;
        var value = _settingsDnsStrategy.SelectedIndex switch
        {
            1 => "prefer_ipv4",
            2 => "prefer_ipv6",
            _ => "ipv4_only",
        };
        AndroidStorage.SetDnsStrategy(value);
        MarkSettingsDirty();
    }

    private void OnSettingsChannelChanged(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_settingsLoading || _settingsReceivePrereleases is null) return;
        AndroidStorage.SetUpdateChannel(_settingsReceivePrereleases.IsChecked == true ? "experimental" : "stable");
        // Update channel doesn't affect the running tunnel — no need to
        // mark dirty. Auto-saved badge stays.
        //
        // v2.36.0-r3 UX-1 fix (EOStārāTheia 2026-05-23): trigger an
        // update check immediately after the channel flip. Pre-r3 the
        // user had to RELAUNCH the app (or wait for the next periodic
        // check) before the newly-eligible prerelease showed up in the
        // banner — confusing because the toggle felt like a no-op. Now
        // the banner reflects the new channel within seconds.
        _ = RunUpdateCheckAsync(manual: true);
    }

    private void OnSettingsCheckUpdatesClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        // v2.32.0 (2026-05-07) — wires the Settings > Updates button to
        // the real flow. The Settings overlay stays open so the user
        // sees the result inline; banner appears under the status card
        // (it's behind the overlay, but visible if user dismisses).
        _ = RunUpdateCheckAsync(manual: true);
    }

    private void OnSettingsAutostartVpnChanged(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_settingsLoading || _settingsAutostartVpn is null) return;
        AndroidStorage.SetAutostartVpn(_settingsAutostartVpn.IsChecked == true);
        // Boot-time autostart flag — affects only the future BootCompletedReceiver
        // path, not the currently-running tunnel. No dirty mark.
    }

    private void OnSettingsAutostartZapretChanged(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_settingsLoading || _settingsAutostartZapret is null) return;
        AndroidStorage.SetAutostartZapret(_settingsAutostartZapret.IsChecked == true);
    }

    private void OnSettingsAutostartTgProxyChanged(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_settingsLoading || _settingsAutostartTgProxy is null) return;
        AndroidStorage.SetAutostartTgProxy(_settingsAutostartTgProxy.IsChecked == true);
    }

    /// <summary>
    /// v2.32.0 (AND-ZAPRET) — DPI bypass mode picker. Persists the new
    /// value + refreshes the Zapret chip in the sub-header so the
    /// state visualisation stays in sync without waiting for the next
    /// VPN connect cycle.
    /// </summary>
    private void OnSettingsDpiBypassModeChanged(object? sender, Avalonia.Controls.SelectionChangedEventArgs e)
    {
        if (_settingsLoading || _settingsDpiBypassMode is null) return;
        var value = _settingsDpiBypassMode.SelectedIndex switch
        {
            1 => "standard",
            2 => "aggressive",
            _ => "off",
        };
        AndroidStorage.SetDpiBypassMode(value);
        UpdateZapretChipFromState();
        MarkSettingsDirty();
    }

    // OnReliabilityAlwaysOnClicked / OnReliabilityBatteryClicked /
    // OnReliabilityAutoReconnectChanged moved to AndroidApp.Permissions.cs
    // (Phase 2C Wave 9, 2026-05-18).

    /// <summary>
    /// Phase C (2026-05-10) — Mark Settings as having pending changes that
    /// need a tunnel reload to take effect (routing mode flip, DNS strategy,
    /// bypass-RU, ad-block, DPI bypass mode, block-on-VPN-fail). Swaps the
    /// "✓ Auto-saved" footer badge for an [Apply] button. Called from each
    /// affected OnSettings*Changed handler. Idempotent — re-marking already-
    /// dirty state is a no-op. The flag is kept tab-local; switching to
    /// another Advanced tab and back keeps the dirty state, which is the
    /// expected UX (a half-applied change shouldn't quietly clear because
    /// the user navigated elsewhere).
    /// </summary>
    private void MarkSettingsDirty()
    {
        if (_settingsDirty) return;
        _settingsDirty = true;
        UpdateSettingsFooterVisibility();
    }

    /// <summary>
    /// Apply button click. Clears the dirty flag and, if the tunnel is
    /// currently running, kicks a reconnect cycle so the running config
    /// picks up the new settings (Android has no sing-box hot-reload path
    /// — disconnect + reconnect is the only way to apply mid-flight). When
    /// disconnected, the click just clears the badge — the next user-
    /// initiated Connect will rebuild the config from fresh storage anyway.
    /// </summary>
    private void OnSettingsApplyClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _settingsDirty = false;
        UpdateSettingsFooterVisibility();

        // If the tunnel is running, restart it so the new config takes
        // effect. RequestDisconnect → IntentChanged(false) → user can tap
        // Connect again. We deliberately don't auto-reconnect because that
        // would be surprising — desktop's Apply button reloads in place,
        // but the equivalent on Android is a hard cycle that kills + rebuilds
        // the VpnService. A one-tap surprise reconnect would feel jarring.
        var activity = MainActivity.Instance;
        if (activity is null) return;
        if (MainActivity.IntendedConnected)
        {
            activity.RequestDisconnect();
        }
    }

    /// <summary>
    /// Show the Auto-saved badge when there are no pending changes; show
    /// the [Apply] button when there are. The two never co-exist in the
    /// footer — they swap so the user's eye is drawn to the actionable
    /// state (apply needed vs. nothing to do).
    /// </summary>
    private void UpdateSettingsFooterVisibility()
    {
        if (_settingsAutoSavedBadge is not null)
            _settingsAutoSavedBadge.IsVisible = !_settingsDirty;
        if (_settingsApplyButton is not null)
            _settingsApplyButton.IsVisible = _settingsDirty;
    }

    // UpdateBatteryOptimizationStatus / IsIgnoringBatteryOptimizations
    // moved to AndroidApp.Permissions.cs (Phase 2C Wave 9, 2026-05-18).

}
