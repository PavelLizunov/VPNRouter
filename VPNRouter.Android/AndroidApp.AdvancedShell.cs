using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace VPNRouter.Android;

public partial class AndroidApp
{
    private enum AdvancedTab
    {
        Servers,
        Subscribe,
        Settings,
        Applications,
        Tools,
        Public,
    }

    private Border? _advShellOverlay;
    private Grid? _advShellContentHost;
    private AdvancedTab _advShellSelectedTab = AdvancedTab.Servers;
    private readonly Dictionary<AdvancedTab, Avalonia.Controls.Button> _advShellTabButtons = new();
    private readonly Dictionary<AdvancedTab, Control> _advShellTabContent = new();

    private TextBlock? _advBrandTitle;
    private Image? _advMascotImage;
    private Avalonia.Controls.Button? _advSimpleToggleBtn;
    private Avalonia.Controls.Button? _advKebabMenuBtn;
    private TextBlock? _advVpnChip;
    private TextBlock? _advZapretChip;

    private Ellipse? _advFooterStatusDot;
    private TextBlock? _advFooterStatusText;
    private Avalonia.Controls.Button? _advFooterConnectBtn;
    private Border? _advFooterActionsHost;
    private Border? _advHeaderBorder;
    private Border? _advFooterBorder;

    private Border BuildAdvancedShellOverlay()
    {
        var bg       = GetBrush("SurfaceAppBrush");
        var raised   = GetBrush("SurfaceRaisedBrush");
        var subtle   = GetBrush("BorderSubtleBrush");
        var defaultB = GetBrush("BorderDefaultBrush");

        _advHeaderBorder = BuildAdvancedHeader();
        var headerBorder = _advHeaderBorder;

        var visibleTabs = System.Enum.GetValues(typeof(AdvancedTab))
            .Cast<AdvancedTab>()
            .Where(t => t != AdvancedTab.Tools)
            .ToList();
        var tabPanel = new UniformGrid
        {
            Columns = visibleTabs.Count,
            Rows = 1,
            Margin = new Thickness(6, 6, 6, 6),
        };
        foreach (var tab in visibleTabs)
        {
            var btn = MakeAdvShellTabButton(tab);
            _advShellTabButtons[tab] = btn;
            tabPanel.Children.Add(btn);
        }
        var tabScroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = tabPanel,
        };
        var tabStripBorder = new Border
        {
            Background = Brushes.Transparent,
            BorderBrush = defaultB,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = tabScroll,
        };

        _advShellContentHost = new Grid
        {
            Background = bg,
        };

        _advFooterBorder = BuildAdvancedFooter();
        var footerBorder = _advFooterBorder;

        var dock = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(headerBorder, Dock.Top);
        DockPanel.SetDock(tabStripBorder, Dock.Top);
        DockPanel.SetDock(footerBorder, Dock.Bottom);
        dock.Children.Add(headerBorder);
        dock.Children.Add(tabStripBorder);
        dock.Children.Add(footerBorder);
        dock.Children.Add(_advShellContentHost);

        ApplyAdvancedFooterConnectionState(MainActivity.IntendedConnected);

        return new Border
        {
            Background = bg,
            IsVisible = false,
            Child = dock,
        };
    }

    private Border BuildAdvancedHeader()
    {
        var raised   = GetBrush("SurfaceRaisedBrush");
        var subtle   = GetBrush("BorderSubtleBrush");
        var defaultB = GetBrush("BorderDefaultBrush");

        _advMascotImage = new Image
        {
            Source = LoadMascot(),
            Stretch = Stretch.Uniform,
            Width = 26,
            Height = 26,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        RenderOptions.SetBitmapInterpolationMode(_advMascotImage, BitmapInterpolationMode.HighQuality);
        var mascotContainer = new Border
        {
            Width = 28,
            Height = 28,
            CornerRadius = new CornerRadius(GetRadius("RadiusSm")),
            VerticalAlignment = VerticalAlignment.Center,
            ClipToBounds = true,
            Child = _advMascotImage,
        };
        mascotContainer.BindToken(Border.BackgroundProperty, "AccentBgSubtleBrush");

        _advBrandTitle = new TextBlock
        {
            Text = Localization.BrandTitle,
            FontSize = UiScale.Fs(12),
            FontWeight = FontWeight.Bold,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        _advBrandTitle.BindToken(TextBlock.ForegroundProperty, "TextPrimaryBrush");

        _advVpnChip = MakeChip("VPN", "SurfaceSunkenBrush", "TextMutedBrush");
        _advZapretChip = MakeChip("Zapret", "SurfaceSunkenBrush", "TextMutedBrush");
        SetVpnChipState(_vpnChipState, force: true);
        SetZapretChipState(_zapretChipState, force: true);
        var vpnChip = _advVpnChip;
        var chipRow = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { vpnChip },
        };

        var brandStack = new StackPanel
        {
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _advBrandTitle, chipRow },
        };

        _advSimpleToggleBtn = new Avalonia.Controls.Button
        {
            Content = Localization.AdvSimpleToggle,
            Padding = new Thickness(8, 4),
            MinHeight = 0,
            FontSize = UiScale.Fs(11),
            FontWeight = FontWeight.SemiBold,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(GetRadius("RadiusSm")),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
        };
        _advSimpleToggleBtn.BindToken(Avalonia.Controls.Button.ForegroundProperty, "AccentFgBrush");
        _advSimpleToggleBtn.Click += (_, _) => CloseAdvancedShell();

        _advKebabMenuBtn = new Avalonia.Controls.Button
        {
            Content = "⋮",
            FontSize = UiScale.Fs(22),
            FontWeight = FontWeight.Bold,
            Width = 32,
            Height = 32,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _advKebabMenuBtn.BindToken(Avalonia.Controls.Button.ForegroundProperty, "TextSecondaryBrush");
        _advKebabMenuBtn.Click += OnAdvancedKebabClicked;

        var headerGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("28,*,Auto,Auto"),
            ColumnSpacing = 10,
            Margin = new Thickness(12, 8),
        };
        Grid.SetColumn(mascotContainer, 0);
        Grid.SetColumn(brandStack, 1);
        Grid.SetColumn(_advSimpleToggleBtn, 2);
        Grid.SetColumn(_advKebabMenuBtn, 3);
        headerGrid.Children.Add(mascotContainer);
        headerGrid.Children.Add(brandStack);
        headerGrid.Children.Add(_advSimpleToggleBtn);
        headerGrid.Children.Add(_advKebabMenuBtn);

        return new Border
        {
            Background = raised,
            BorderBrush = defaultB,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = headerGrid,
        };
    }

    private Border BuildAdvancedFooter()
    {
        var raised   = GetBrush("SurfaceRaisedBrush");
        var defaultB = GetBrush("BorderDefaultBrush");

        _advFooterStatusDot = new Ellipse
        {
            Width = 8,
            Height = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _advFooterStatusDot.BindToken(Ellipse.FillProperty, "TextMutedBrush");

        _advFooterStatusText = new TextBlock
        {
            Text = Localization.SimpleStatusTitleOff,
            FontSize = UiScale.Fs(11),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        _advFooterStatusText.BindToken(TextBlock.ForegroundProperty, "TextSecondaryBrush");

        var statusStack = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 7,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _advFooterStatusDot, _advFooterStatusText },
        };

        _advFooterConnectBtn = new Avalonia.Controls.Button
        {
            Content = Localization.StartVPN,
            FontSize = UiScale.Fs(11),
            FontWeight = FontWeight.Bold,
            Padding = new Thickness(14, 5),
            MinHeight = 0,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(GetRadius("RadiusSm")),
        };
        _advFooterConnectBtn.BindToken(Avalonia.Controls.Button.BackgroundProperty, "AccentSolidBrush");
        _advFooterConnectBtn.BindToken(Avalonia.Controls.Button.ForegroundProperty, "AccentOnSolidBrush");
        _advFooterConnectBtn.Click += OnConnectClicked;

        var connectRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            ColumnSpacing = 10,
        };
        Grid.SetColumn(statusStack, 0);
        Grid.SetColumn(_advFooterConnectBtn, 1);
        connectRow.Children.Add(statusStack);
        connectRow.Children.Add(_advFooterConnectBtn);

        _advFooterActionsHost = new Border
        {
            Padding = new Thickness(12, 6, 12, 0),
            IsVisible = false,
            Child = null,
        };

        var footerStack = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Vertical,
            Children = { _advFooterActionsHost, connectRow },
        };

        return new Border
        {
            Background = raised,
            BorderBrush = defaultB,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(12, 7),
            Child = footerStack,
        };
    }

    internal void SetAdvancedFooterActions(Control? actions)
    {
        if (_advFooterActionsHost is null) return;
        _advFooterActionsHost.Child = actions;
        _advFooterActionsHost.IsVisible = actions is not null;
    }

    private void OnAdvancedKebabClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_kebabPopup is null) return;
        if (_kebabPopup.IsOpen)
        {
            _kebabPopup.IsOpen = false;
            return;
        }
        _kebabPopup.PlacementTarget = _advKebabMenuBtn;
        _kebabPopup.IsOpen = true;
        _resetConfirmPending = false;
        if (_menuResetSettingsItem is not null)
            _menuResetSettingsItem.Content = Localization.MenuItemResetSettings;
    }

    private void OpenAdvancedShell(AdvancedTab tab)
    {
        if (_advShellOverlay is null) return;
        SelectAdvancedTab(tab);
        _advShellOverlay.IsVisible = true;
        if (_advFooterStatusText is not null && _connectionStartedAt is DateTime startUtc)
        {
            var elapsed = DateTime.UtcNow - startUtc;
            _advFooterStatusText.Text = string.Format(
                Localization.SimpleStatusTitleOnWithUptime,
                FormatUptime(elapsed));
        }
    }

    private void CloseAdvancedShell()
    {
        if (_advShellOverlay is not null)
            _advShellOverlay.IsVisible = false;

        StopServersTabBackgroundWork();
        StopFreeConfigsBackgroundWork();

        ReloadServerList();
        UpdateConfigSummary();

        var routing = AndroidStorage.GetRoutingMode();
        if (_splitRadio is not null) _splitRadio.IsChecked = routing == "split";
        if (_fullRadio is not null) _fullRadio.IsChecked = routing == "full";
        UpdatePerAppFormCountLabel();
    }

    private void SelectAdvancedTab(AdvancedTab tab)
    {
        if (_advShellContentHost is null) return;
        _advShellSelectedTab = tab;
        EnsureTabContentBuilt(tab);

        foreach (var kv in _advShellTabContent)
            kv.Value.IsVisible = kv.Key == tab;

        foreach (var kv in _advShellTabButtons)
            StyleAdvShellTab(kv.Value, kv.Key == tab);

        SetAdvancedFooterActions(null);

        switch (tab)
        {
            case AdvancedTab.Servers:      ReseedServersTabState();     break;
            case AdvancedTab.Subscribe:    ReseedSubscribeTabState();   break;
            case AdvancedTab.Settings:     ReseedNetworkTabState();     break;
            case AdvancedTab.Applications: ReseedAppPickerTabState();   break;
            case AdvancedTab.Tools:        ReseedDpiBypassTabState();   break;
            case AdvancedTab.Public:       ReseedFreeConfigsTabState(); break;
        }

        AndroidStorage.SetAdvancedActiveTab(tab.ToString());
    }

    private void EnsureTabContentBuilt(AdvancedTab tab)
    {
        if (_advShellContentHost is null) return;
        if (_advShellTabContent.ContainsKey(tab)) return;

        Control content = tab switch
        {
            AdvancedTab.Servers      => BuildServersTabContent(),
            AdvancedTab.Subscribe    => BuildSubscribeTabContent(),
            AdvancedTab.Settings     => BuildSettingsTabContent(),
            AdvancedTab.Applications => BuildAppPickerTabContent(),
            AdvancedTab.Tools        => BuildToolsTabContent(),
            AdvancedTab.Public       => BuildPublicTabContent(),
            _                        => new TextBlock { Text = "?" },
        };
        content.IsVisible = false;
        _advShellTabContent[tab] = content;
        _advShellContentHost.Children.Add(content);
    }

    private Control BuildSettingsTabContent() => BuildNetworkTabContent();

    private Avalonia.Controls.Button MakeAdvShellTabButton(AdvancedTab tab)
    {
        var label = new TextBlock
        {
            Text = AdvancedTabLabel(tab),
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var btn = new Avalonia.Controls.Button
        {
            Content = label,
            FontSize = UiScale.Fs(10),
            FontWeight = FontWeight.SemiBold,
            Padding = new Thickness(0, 8),
            MinHeight = 44,
            MinWidth = 62,
            Margin = new Thickness(1, 0),
            CornerRadius = new CornerRadius(GetRadius("RadiusSm")),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        btn.Click += (_, _) => SelectAdvancedTab(tab);
        StyleAdvShellTab(btn, tab == _advShellSelectedTab);
        return btn;
    }

    private void StyleAdvShellTab(Avalonia.Controls.Button btn, bool active)
    {
        if (active)
        {
            btn.Background = GetBrush("AccentBgSubtleBrush");
            btn.Foreground = GetBrush("AccentFgBrush");
            btn.BorderBrush = GetBrush("BorderAccentBrush");
        }
        else
        {
            btn.Background = GetBrush("SurfaceSunkenBrush");
            btn.Foreground = GetBrush("TextSecondaryBrush");
            btn.BorderBrush = GetBrush("BorderSubtleBrush");
        }
    }

    internal Avalonia.Controls.Button MakeAdvancedSubTabButton(
        string label,
        bool active,
        EventHandler<Avalonia.Interactivity.RoutedEventArgs> onClick)
    {
        var btn = new Avalonia.Controls.Button
        {
            Content = label,
            FontSize = UiScale.Fs(11),
            FontWeight = FontWeight.SemiBold,
            Padding = new Thickness(10, 4),
            CornerRadius = new CornerRadius(GetRadius("RadiusSm")),
            BorderThickness = new Thickness(1),
        };
        StyleAdvShellTab(btn, active);
        btn.Click += onClick;
        return btn;
    }

    private static string AdvancedTabLabel(AdvancedTab tab)
    {
        return tab switch
        {
            AdvancedTab.Servers      => Localization.TabAdvServers,
            AdvancedTab.Subscribe    => Localization.TabAdvSubscribe,
            AdvancedTab.Settings     => Localization.TabAdvSettings,
            AdvancedTab.Applications => Localization.TabAdvApplications,
            AdvancedTab.Tools        => Localization.TabAdvTools,
            AdvancedTab.Public       => Localization.TabAdvPublic,
            _                        => string.Empty,
        };
    }

    private void RefreshAdvancedShellStrings()
    {
        if (_advBrandTitle is not null)
            _advBrandTitle.Text = Localization.BrandTitle;
        if (_advSimpleToggleBtn is not null)
            _advSimpleToggleBtn.Content = Localization.AdvSimpleToggle;
        foreach (var kv in _advShellTabButtons)
        {
            if (kv.Value.Content is TextBlock tb)
                tb.Text = AdvancedTabLabel(kv.Key);
            else
                kv.Value.Content = AdvancedTabLabel(kv.Key);
        }
        ApplyAdvancedFooterConnectionState(MainActivity.IntendedConnected);

        if (_settingsSubSectionButtons is not null)
        {
            for (int i = 0; i < _settingsSubSectionButtons.Length; i++)
            {
                var btn = _settingsSubSectionButtons[i];
                if (btn is not null) btn.Content = SettingsSubSectionLabel(i);
            }
        }
        if (_settingsApplyButton is not null)
            _settingsApplyButton.Content = Localization.ApplyNowReloadVpn;
        try
        {
            if (_advShellContentHost is not null)
            {
                foreach (var kv in _advShellTabContent)
                    _advShellContentHost.Children.Remove(kv.Value);
            }
            _advShellTabContent.Clear();
            if (_advShellOverlay is not null && _advShellOverlay.IsVisible)
                SelectAdvancedTab(_advShellSelectedTab);
        }
        catch (System.Exception ex)
        {
            try
            {
                global::Android.Util.Log.Warn("VpnRouter.Lang",
                    $"Advanced-tab language rebuild failed: {ex.GetType().Name}: {ex.Message}");
            }
            catch { }
        }
    }

    private void ApplyAdvancedFooterConnectionState(bool connected)
    {
        if (_advFooterStatusDot is not null)
        {
            _advFooterStatusDot.Bind(
                Ellipse.FillProperty,
                new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension(
                    connected ? "SuccessSolidBrush" : "TextMutedBrush"));
        }
        if (_advFooterStatusText is not null)
        {
            _advFooterStatusText.Text = connected
                ? Localization.SimpleStatusTitleOn
                : Localization.SimpleStatusTitleOff;
        }
        if (_advFooterConnectBtn is not null)
        {
            _advFooterConnectBtn.Content = connected
                ? Localization.StopVPN
                : Localization.StartVPN;
        }
    }

    internal void ApplyAdvancedShellSafeArea(Thickness insets)
    {
        if (_advHeaderBorder is not null)
        {
            var topPad = Math.Max(8.0, insets.Top + 4.0);
            _advHeaderBorder.Padding = new Thickness(0, topPad, 0, 0);
        }

        if (_advFooterBorder is not null)
        {
            var bottomPad = Math.Max(7.0, insets.Bottom + 6.0);
            _advFooterBorder.Padding = new Thickness(12, 7, 12, bottomPad);
        }
    }
}
