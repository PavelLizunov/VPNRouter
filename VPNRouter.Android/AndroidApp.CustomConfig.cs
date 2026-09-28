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
    private void OnCcModeSubClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => SetCcMode("subscribe");
    private void OnCcModeManualClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => SetCcMode("manual");
    private void OnCcModeCustomClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => SetCcMode("custom");

    private void SetCcMode(string mode)
    {
        if (mode != "subscribe" && mode != "manual" && mode != "custom")
            return;
        if (_ccMode == mode) return;
        _ccMode = mode;
        AndroidStorage.SetConfigMode(mode);
        ApplyCcModeVisuals();
        UpdateConfigSummary();
    }

    private void ApplyCcModeVisuals()
    {
        StyleSegment(_ccModeSubBtn, _ccMode == "subscribe");
        StyleSegment(_ccModeManualBtn, _ccMode == "manual");
        StyleSegment(_ccModeCustomBtn, _ccMode == "custom");
        if (_ccUriSection is not null) _ccUriSection.IsVisible = _ccMode != "custom";
        if (_ccCustomSection is not null) _ccCustomSection.IsVisible = _ccMode == "custom";
    }

    private void OnCcValidateClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_ccCustomInput is null || _ccCustomStatus is null) return;
        var raw = (_ccCustomInput.Text ?? string.Empty).Trim();
        _ccCustomStatus.IsVisible = true;

        if (string.IsNullOrEmpty(raw))
        {
            _ccCustomStatus.Text = Localization.CcSaveStatusEmpty;
            _ccCustomStatus.BindToken(TextBlock.ForegroundProperty, "DangerFgBrush");
            return;
        }

        try
        {
            var (isValid, errors) = VPNRouter.Core.Services.CustomConfigInjector.Validate(raw);
            if (!isValid)
            {
                _ccCustomStatus.Text = string.Format(
                    Localization.CcValidationFailed,
                    string.Join("; ", errors));
                _ccCustomStatus.BindToken(TextBlock.ForegroundProperty, "DangerFgBrush");
                return;
            }
            var (protocols, server) = VPNRouter.Core.Services.CustomConfigInjector.ParseConfigInfo(raw);
            _ccCustomStatus.Text = string.Format(Localization.CcValidationOk, protocols, server);
            _ccCustomStatus.BindToken(TextBlock.ForegroundProperty, "SuccessFgBrush");
        }
        catch (Exception ex)
        {
            _ccCustomStatus.Text = string.Format(Localization.CcValidationParseError, ex.Message);
            _ccCustomStatus.BindToken(TextBlock.ForegroundProperty, "DangerFgBrush");
        }
    }

    private void OnCcSaveCustomClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_ccCustomInput is null || _ccCustomStatus is null) return;
        var raw = (_ccCustomInput.Text ?? string.Empty).Trim();
        _ccCustomStatus.IsVisible = true;

        if (string.IsNullOrEmpty(raw))
        {
            _ccCustomStatus.Text = Localization.CcSaveStatusEmpty;
            _ccCustomStatus.BindToken(TextBlock.ForegroundProperty, "DangerFgBrush");
            return;
        }

        var (isValid, errors) = VPNRouter.Core.Services.CustomConfigInjector.Validate(raw);
        AndroidStorage.SetCustomConfigJson(raw);
        AndroidStorage.SetConfigMode("custom");
        _ccMode = "custom";
        ApplyCcModeVisuals();
        UpdateConfigSummary();

        if (!isValid)
        {
            _ccCustomStatus.Text = string.Format(
                Localization.CcSaveStatusInvalid + " ({0})",
                string.Join("; ", errors));
            _ccCustomStatus.BindToken(TextBlock.ForegroundProperty, "WarningFgBrush");
            return;
        }

        _ccCustomStatus.Text = Localization.CcSaveStatusOk;
        _ccCustomStatus.BindToken(TextBlock.ForegroundProperty, "SuccessFgBrush");
    }

    private void OnCcClearCustomClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_ccCustomInput is not null) _ccCustomInput.Text = string.Empty;
        if (_ccCustomStatus is not null) _ccCustomStatus.IsVisible = false;
        AndroidStorage.SetCustomConfigJson(null);
        UpdateConfigSummary();
    }

    private async void OnRefreshClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_serverInputError is null) return;
        var url = AndroidStorage.GetSubscriptionUrl();
        if (string.IsNullOrEmpty(url) && _serverInput is not null)
        {
            var raw = (_serverInput.Text ?? string.Empty).Trim();
            if (raw.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                AndroidStorage.SetSubscriptionUrl(raw);
                url = raw;
            }
        }
        if (string.IsNullOrWhiteSpace(url))
        {
            _serverInputError.Text = Localization.RefreshNeedsUrl;
            _serverInputError.IsVisible = true;
            return;
        }

        _serverInputError.IsVisible = false;
        try
        {
            var servers = await SubscriptionFetcher.FetchAsync(url, logger: null, ct: System.Threading.CancellationToken.None).ConfigureAwait(true);
            var list = new List<VlessServerEntry>(servers);
            AndroidStorage.SetServers(list);
            _cachedServers = list;
            UpdateServerListView();
            var prevSelected = AndroidStorage.GetSelectedServerName();
            var hasPrev = !string.IsNullOrEmpty(prevSelected) &&
                          list.Exists(s => string.Equals(s.Name, prevSelected, StringComparison.OrdinalIgnoreCase));
            if (!hasPrev && list.Count > 0)
            {
                AndroidStorage.SetSelectedServerName(list[0].Name);
                if (_serverList is not null) _serverList.SelectedIndex = 0;
            }
            UpdateConfigSummary();
        }
        catch (Exception ex)
        {
            _serverInputError.Text = string.Format(Localization.RefreshFailed, ex.Message);
            _serverInputError.IsVisible = true;
        }
    }

    private void OnServerSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_serverList?.SelectedItem is VlessServerEntry entry)
            AndroidStorage.SetSelectedServerName(entry.Name);
    }

    private async void ReloadServerList()
    {
        try
        {
            _cachedServers = await System.Threading.Tasks.Task.Run(AndroidStorage.GetServers);
        }
        catch
        {
            _cachedServers = new List<VlessServerEntry>();
        }
        UpdateServerListView();
    }

    private void UpdateServerListView()
    {
        if (_serverList is null || _serverListHeader is null) return;
        var visible = _cachedServers.Count > 0;
        _serverList.IsVisible = visible;
        _serverListHeader.IsVisible = visible;
        _serverList.ItemsSource = _cachedServers;
        _serverList.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<VlessServerEntry>(
            (item, _) =>
            {
                var name = new TextBlock
                {
                    Text = string.IsNullOrEmpty(item?.Name) ? (item?.Server ?? "?") : item.Name,
                    FontSize = 12,
                    FontWeight = FontWeight.Medium,
                };
                name.BindToken(TextBlock.ForegroundProperty, "TextPrimaryBrush");
                var sub = new TextBlock
                {
                    Text = $"{item?.Server}:{item?.Port}  ·  {item?.Protocol ?? "vless"}",
                    FontSize = 10,
                };
                sub.BindToken(TextBlock.ForegroundProperty, "TextMutedBrush");
                return new StackPanel
                {
                    Spacing = 2,
                    Margin = new Thickness(8, 6),
                    Children = { name, sub }
                };
            }, supportsRecycling: true);
        var sel = AndroidStorage.GetSelectedServerName();
        if (!string.IsNullOrEmpty(sel))
        {
            for (int i = 0; i < _cachedServers.Count; i++)
            {
                if (string.Equals(_cachedServers[i].Name, sel, StringComparison.OrdinalIgnoreCase))
                {
                    _serverList.SelectedIndex = i;
                    break;
                }
            }
        }
    }

    private void OnAdvCardClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        OpenAdvancedShell(AdvancedTab.Servers);
    }

    private Control BuildAutostartInlineCard(double radiusSm)
    {
        _autostartCardTitleText = new TextBlock
        {
            Text = Localization.SmpAutostartCardTitle,
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
        };
        _autostartCardTitleText.BindToken(TextBlock.ForegroundProperty, "TextPrimaryBrush");

        _autostartCardSubText = new TextBlock
        {
            Text = Localization.SmpAutostartCardSubtitle,
            FontSize = 9,
            TextWrapping = TextWrapping.Wrap,
        };
        _autostartCardSubText.BindToken(TextBlock.ForegroundProperty, "TextMutedBrush");

        var chevron = new TextBlock
        {
            Text = "›",
            FontSize = 14,
            FontWeight = FontWeight.Bold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        chevron.BindToken(TextBlock.ForegroundProperty, "AccentFgBrush");

        var inner = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            ColumnSpacing = 10,
            Margin = new Thickness(10, 8),
        };
        var stack = new StackPanel
        {
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _autostartCardTitleText, _autostartCardSubText },
        };
        Grid.SetColumn(stack, 0);
        Grid.SetColumn(chevron, 1);
        inner.Children.Add(stack);
        inner.Children.Add(chevron);

        var btn = new Avalonia.Controls.Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(radiusSm),
            Content = inner,
        };
        btn.BindToken(Avalonia.Controls.Button.BackgroundProperty, "SurfaceSunkenBrush");
        btn.BindToken(Avalonia.Controls.Button.BorderBrushProperty, "BorderDefaultBrush");
        btn.Click += (_, _) => ShowSettings();
        return btn;
    }

    private void OnMenuExportConfigClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_kebabPopup is not null) _kebabPopup.IsOpen = false;
        ShowExportOverlay();
    }

    private void OnMenuImportConfigClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_kebabPopup is not null) _kebabPopup.IsOpen = false;
        ShowImportOverlay();
    }

}
