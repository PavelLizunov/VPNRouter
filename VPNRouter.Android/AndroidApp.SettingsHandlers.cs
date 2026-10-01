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

    private void ShowSettings()
    {
        OpenAdvancedShell(AdvancedTab.Settings);
    }

    private void ReseedNetworkTabState()
    {
        _settingsLoading = true;
        try
        {
            var routing = AndroidStorage.GetRoutingMode();
            if (_settingsSplitRadio is not null) _settingsSplitRadio.IsChecked = routing == "split";
            if (_settingsFullRadio is not null) _settingsFullRadio.IsChecked = routing == "full";
            if (_settingsBypassRu is not null) _settingsBypassRu.IsChecked = AndroidStorage.GetBypassRussianTraffic();
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
        _ = RunUpdateCheckAsync(manual: true);
    }

    private void OnSettingsCheckUpdatesClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _ = RunUpdateCheckAsync(manual: true);
    }


    private void MarkSettingsDirty()
    {
        if (_settingsDirty) return;
        _settingsDirty = true;
        UpdateSettingsFooterVisibility();
    }

    private void OnSettingsApplyClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _settingsDirty = false;
        UpdateSettingsFooterVisibility();

        var activity = MainActivity.Instance;
        if (activity is null) return;
        if (MainActivity.IntendedConnected)
        {
            activity.RequestDisconnect();
        }
    }

    private void UpdateSettingsFooterVisibility()
    {
        if (_settingsAutoSavedBadge is not null)
        {
            var appears = !_settingsAutoSavedBadge.IsVisible && !_settingsDirty;
            _settingsAutoSavedBadge.IsVisible = !_settingsDirty;
            if (appears) _settingsAutoSavedCheck?.PlayReveal();
        }
        if (_settingsApplyButton is not null)
            _settingsApplyButton.IsVisible = _settingsDirty;
    }

}
