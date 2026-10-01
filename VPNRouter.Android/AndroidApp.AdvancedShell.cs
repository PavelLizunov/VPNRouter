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
using Avalonia.VisualTree;
using UiIcons = VPNRouter.Core.Services.UiIcons;

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
    private Border? _advTabStripBorder;
    private bool _advNavCompact;

    // A tab page is never laid out lower than this. Below it (landscape, small phones, an open keyboard) the page scrolls
    // as a whole instead of squeezing its lists to nothing and drawing rows over each other.
    private const double AdvMinPageHeight = 520;
    // Below this shell height (landscape) the navigation bar puts the label next to the icon and gets 20 dp lower.
    private const double AdvCompactNavBelow = 600;

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
            Margin = new Thickness(6, 4, 6, 4),
        };
        foreach (var tab in visibleTabs)
        {
            var btn = MakeAdvShellTabButton(tab);
            _advShellTabButtons[tab] = btn;
            tabPanel.Children.Add(btn);
        }
        var tabStripBorder = new Border
        {
            Background = GetBrush("SurfaceBaseBrush"),
            BorderBrush = defaultB,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = tabPanel,
        };
        _advTabStripBorder = tabStripBorder;

        _advShellContentHost = new Grid
        {
            Background = bg,
        };
        _advShellContentHost.SizeChanged += (_, _) => SizeAdvTabPages();

        _advFooterBorder = BuildAdvancedFooter();
        var footerBorder = _advFooterBorder;

        var dock = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(headerBorder, Dock.Top);
        DockPanel.SetDock(tabStripBorder, Dock.Bottom);
        DockPanel.SetDock(footerBorder, Dock.Bottom);
        dock.Children.Add(headerBorder);
        dock.Children.Add(tabStripBorder);
        dock.Children.Add(footerBorder);
        dock.Children.Add(_advShellContentHost);

        ApplyAdvancedFooterConnectionState(MainActivity.IntendedConnected);

        var overlay = new Border
        {
            Background = bg,
            IsVisible = false,
            Child = dock,
        };
        overlay.SizeChanged += (_, e) => SetAdvNavCompact(e.NewSize.Height < AdvCompactNavBelow);
        return overlay;
    }

    private void SizeAdvTabPages()
    {
        if (_advShellContentHost is null) return;
        var h = _advShellContentHost.Bounds.Height;
        if (h <= 0) return;
        foreach (var page in _advShellTabContent.Values)
            if (page is ScrollViewer { Content: Control content })
                content.Height = Math.Max(h, AdvMinPageHeight);
        // When the keyboard makes the page scroll, keep the field being typed into in view.
        var host = _advShellContentHost;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (TopLevel.GetTopLevel(host)?.FocusManager?.GetFocusedElement() is Control focused &&
                focused.IsVisible && host.IsVisualAncestorOf(focused))
                focused.BringIntoView();
        });
    }

    private void SetAdvNavCompact(bool compact)
    {
        if (compact == _advNavCompact) return;
        _advNavCompact = compact;
        foreach (var btn in _advShellTabButtons.Values)
            StyleAdvShellTabLayout(btn, compact);
    }

    private static void StyleAdvShellTabLayout(Avalonia.Controls.Button btn, bool compact)
    {
        btn.MinHeight = compact ? 44 : 56;
        btn.Padding = compact ? new Thickness(2, 4) : new Thickness(2, 6);
        if (btn.Content is StackPanel stack)
        {
            stack.Orientation = compact ? Avalonia.Layout.Orientation.Horizontal : Avalonia.Layout.Orientation.Vertical;
            stack.Spacing = compact ? 8 : 2;
        }
        if (btn.Tag is AdvTabVisual visual)
            visual.Label.VerticalAlignment = VerticalAlignment.Center;
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
            Content = IconText(UiIcons.ChevronLeft, Localization.AdvSimpleToggle, 14),
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
            Content = IconContent(UiIcons.EllipsisVertical, 22),
            Width = 44,
            Height = 44,
            Margin = new Thickness(0, -6, -8, -6),
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
            Content = IconText(UiIcons.Play, Localization.StartVPN, 14),
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
        UpdateMenuAdvancedToggle();
        _kebabPopup.IsOpen = true;
        _resetConfirmPending = false;
        if (_menuResetSettingsItem is not null)
            SetMenuItemText(_menuResetSettingsItem, Localization.MenuItemResetSettings);
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
        var page = new ScrollViewer
        {
            Content = content,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            IsVisible = false,
        };
        _advShellTabContent[tab] = page;
        _advShellContentHost.Children.Add(page);
        SizeAdvTabPages();
    }

    private Control BuildSettingsTabContent() => BuildNetworkTabContent();

    private sealed class AdvTabVisual
    {
        public AdvTabVisual(Avalonia.Controls.Shapes.Path icon, TextBlock label) { Icon = icon; Label = label; }
        public Avalonia.Controls.Shapes.Path Icon { get; }
        public TextBlock Label { get; }
    }

    // 24 x 24 stroke icons of the bottom navigation bar, drawn for this app (no third-party icon set)
    private static string AdvancedTabIconData(AdvancedTab tab) => tab switch
    {
        AdvancedTab.Servers => "M5,4 h14 a2,2 0 0 1 2,2 v2 a2,2 0 0 1 -2,2 h-14 a2,2 0 0 1 -2,-2 v-2 a2,2 0 0 1 2,-2 z M5,14 h14 a2,2 0 0 1 2,2 v2 a2,2 0 0 1 -2,2 h-14 a2,2 0 0 1 -2,-2 v-2 a2,2 0 0 1 2,-2 z M7,7 h0.01 M7,17 h0.01",
        AdvancedTab.Subscribe => "M4,11 a9,9 0 0 1 9,9 M4,4 a16,16 0 0 1 16,16 M5,19 h0.01",
        AdvancedTab.Settings => "M4,6 H7 M11,6 H20 M4,12 H13 M17,12 H20 M4,18 H6 M10,18 H20 M11,6 a2,2 0 1 0 -4,0 a2,2 0 1 0 4,0 M17,12 a2,2 0 1 0 -4,0 a2,2 0 1 0 4,0 M10,18 a2,2 0 1 0 -4,0 a2,2 0 1 0 4,0",
        AdvancedTab.Applications => "M4,4 h6 v6 h-6 z M14,4 h6 v6 h-6 z M4,14 h6 v6 h-6 z M14,14 h6 v6 h-6 z",
        AdvancedTab.Public => "M12,3 a9,9 0 1 0 0,18 a9,9 0 1 0 0,-18 M3,12 h18 M12,3 c3,3 3,15 0,18 c-3,-3 -3,-15 0,-18",
        _ => "M12,3 a9,9 0 1 0 0,18 a9,9 0 1 0 0,-18",
    };

    private Avalonia.Controls.Button MakeAdvShellTabButton(AdvancedTab tab)
    {
        var icon = new Avalonia.Controls.Shapes.Path
        {
            Data = Geometry.Parse(AdvancedTabIconData(tab)),
            StrokeThickness = 1.8,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
            Stretch = Stretch.Uniform,
            Width = 24,
            Height = 24,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        var label = new TextBlock
        {
            Text = AdvancedTabLabel(tab),
            FontSize = UiScale.Fs(9),
            FontWeight = FontWeight.SemiBold,
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        var btn = new Avalonia.Controls.Button
        {
            Content = new StackPanel
            {
                Spacing = 2,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Children = { icon, label },
            },
            Tag = new AdvTabVisual(icon, label),
            Padding = new Thickness(2, 6),
            MinHeight = 56,
            MinWidth = 56,
            Margin = new Thickness(2, 0),
            CornerRadius = new CornerRadius(GetRadius("RadiusLg")),
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        btn.Click += (_, _) => SelectAdvancedTab(tab);
        StyleAdvShellTab(btn, tab == _advShellSelectedTab);
        StyleAdvShellTabLayout(btn, _advNavCompact);
        return btn;
    }

    private void StyleAdvShellTab(Avalonia.Controls.Button btn, bool active)
    {
        var fg = GetBrush(active ? "AccentFgBrush" : "TextSecondaryBrush");
        btn.Background = active ? GetBrush("AccentBgMutedBrush") : Brushes.Transparent;
        btn.Foreground = fg;
        if (btn.Tag is AdvTabVisual visual)
        {
            visual.Icon.Stroke = fg;
            visual.Label.Foreground = fg;
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
            _advSimpleToggleBtn.Content = IconText(UiIcons.ChevronLeft, Localization.AdvSimpleToggle, 14);
        foreach (var kv in _advShellTabButtons)
        {
            if (kv.Value.Tag is AdvTabVisual visual)
                visual.Label.Text = AdvancedTabLabel(kv.Key);
        }
        ApplyAdvancedFooterConnectionState(MainActivity.IntendedConnected);

        if (_settingsSubSectionButtons is not null)
        {
            for (int i = 0; i < _settingsSubSectionButtons.Length; i++)
            {
                var btn = _settingsSubSectionButtons[i];
                if (btn is not null) btn.Content = MakeSettingsSubSectionLabel(SettingsSubSectionLabel(i));
            }
        }
        if (_settingsApplyButton is not null)
            _settingsApplyButton.Content = IconText(UiIcons.RefreshCw, Localization.ApplyNowReloadVpn, 14);
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
                ? IconText(UiIcons.Square, Localization.StopVPN, 14)
                : IconText(UiIcons.Play, Localization.StartVPN, 14);
        }
    }

    internal void ApplyAdvancedShellSafeArea(Thickness insets)
    {
        if (_advHeaderBorder is not null)
        {
            var topPad = Math.Max(8.0, insets.Top + 4.0);
            _advHeaderBorder.Padding = new Thickness(0, topPad, 0, 0);
        }

        // The tab strip is docked lowest, so it carries the bottom inset (the gesture bar); the status footer sits above it.
        if (_advTabStripBorder is not null)
            _advTabStripBorder.Padding = new Thickness(0, 0, 0, Math.Max(0.0, insets.Bottom));

        // While the keyboard is open the status footer and the navigation step aside, so the page keeps the room (in
        // landscape they would otherwise take all of it).
        var typing = _imeBottom > 0;
        if (_advTabStripBorder is not null) _advTabStripBorder.IsVisible = !typing;
        if (_advFooterBorder is not null) _advFooterBorder.IsVisible = !typing;
    }
}
