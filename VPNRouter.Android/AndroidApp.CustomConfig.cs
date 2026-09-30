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


    private void ApplyCcModeVisuals()
    {
        StyleSegment(_ccModeSubBtn, _ccMode == "subscribe");
        StyleSegment(_ccModeManualBtn, _ccMode == "manual");
        StyleSegment(_ccModeCustomBtn, _ccMode == "custom");
        if (_ccUriSection is not null) _ccUriSection.IsVisible = _ccMode != "custom";
        if (_ccCustomSection is not null) _ccCustomSection.IsVisible = _ccMode == "custom";
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
                    FontSize = UiScale.Fs(12),
                    FontWeight = FontWeight.Medium,
                };
                name.BindToken(TextBlock.ForegroundProperty, "TextPrimaryBrush");
                var sub = new TextBlock
                {
                    Text = $"{item?.Server}:{item?.Port}  ·  {item?.Protocol ?? "vless"}",
                    FontSize = UiScale.Fs(10),
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
            FontSize = UiScale.Fs(11),
            FontWeight = FontWeight.SemiBold,
        };
        _autostartCardTitleText.BindToken(TextBlock.ForegroundProperty, "TextPrimaryBrush");

        _autostartCardSubText = new TextBlock
        {
            Text = Localization.SmpAutostartCardSubtitle,
            FontSize = UiScale.Fs(9),
            TextWrapping = TextWrapping.Wrap,
        };
        _autostartCardSubText.BindToken(TextBlock.ForegroundProperty, "TextMutedBrush");

        var chevron = new TextBlock
        {
            Text = "›",
            FontSize = UiScale.Fs(14),
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



}
