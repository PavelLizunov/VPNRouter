using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using System;

namespace VPNRouter.Android;

public partial class AndroidApp
{
    private Control BuildNetworkTabContent()
    {
        var sideNav = BuildSettingsSideNav();
        var contentPane = BuildSettingsContentPane();
        var footerBar = BuildSettingsFooterBar();

        var body = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            Background = GetBrush("SurfaceAppBrush"),
        };
        Grid.SetRow(sideNav, 0);
        Grid.SetRow(contentPane, 1);
        Grid.SetRow(footerBar, 2);
        body.Children.Add(sideNav);
        body.Children.Add(contentPane);
        body.Children.Add(footerBar);

        return body;
    }

    private Border BuildSettingsSideNav()
    {
        var stack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Margin = new Thickness(10, 8, 10, 8),
        };
        for (int i = 0; i < 6; i++)
        {
            var button = MakeSettingsSubSectionButton(i);
            _settingsSubSectionButtons[i] = button;
            stack.Children.Add(button);
        }

        var scroller = new ScrollViewer
        {
            Content = stack,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Background = Brushes.Transparent,
        };

        return new Border
        {
            BorderBrush = GetBrush("BorderDefaultBrush"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Background = GetBrush("SurfaceBaseBrush"),
            Child = scroller,
        };
    }

    private Avalonia.Controls.Button MakeSettingsSubSectionButton(int index)
    {
        var btn = new Avalonia.Controls.Button
        {
            Content = MakeSettingsSubSectionLabel(SettingsSubSectionLabel(index)),
            FontSize = UiScale.Fs(11),
            FontWeight = FontWeight.SemiBold,
            Padding = new Thickness(14, 8),
            MinHeight = 40,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(20),
        };
        btn.Click += (_, _) => SelectSettingsSubSection(index);
        StyleSettingsSubSectionButton(btn, index == _settingsSelectedSubSection);
        return btn;
    }

    private static TextBlock MakeSettingsSubSectionLabel(string text) =>
        new() { Text = text, TextWrapping = TextWrapping.NoWrap };

    private static string SettingsSubSectionLabel(int index) => index switch
    {
        0 => Localization.SettingsSectionRouting,
        1 => Localization.SettingsSectionRules,
        2 => Localization.SettingsSectionLeak,
        3 => Localization.SettingsSectionContent,
        4 => Localization.SettingsSectionUpdates,
        5 => Localization.SettingsSectionAutostart,
        _ => string.Empty,
    };

    private void StyleSettingsSubSectionButton(Avalonia.Controls.Button btn, bool active)
    {
        if (active)
        {
            btn.Background = GetBrush("AccentBgMutedBrush");
            btn.Foreground = GetBrush("AccentFgBrush");
        }
        else
        {
            btn.Background = GetBrush("SurfaceSunkenBrush");
            btn.Foreground = GetBrush("TextSecondaryBrush");
        }
    }

    private Control BuildSettingsContentPane()
    {
        _settingsSelectedSubSection = AndroidStorage.GetSettingsActiveSubSection();

        var host = new Grid
        {
            Background = GetBrush("SurfaceAppBrush"),
        };

        _settingsSubSectionPanels[0] = WrapSubSectionScroller(BuildSettingsRoutingSection());
        _settingsSubSectionPanels[1] = WrapSubSectionScroller(BuildSettingsRulesSection());
        _settingsSubSectionPanels[2] = WrapSubSectionScroller(BuildSettingsLeakSection());
        _settingsSubSectionPanels[3] = WrapSubSectionScroller(BuildSettingsContentSection());
        _settingsSubSectionPanels[4] = WrapSubSectionScroller(BuildSettingsUpdatesSection());

        _settingsSubSectionPanels[5] = WrapSubSectionScroller(BuildSettingsAutostartSection());

        for (int i = 0; i < _settingsSubSectionPanels.Length; i++)
        {
            var panel = _settingsSubSectionPanels[i];
            if (panel is null) continue;
            panel.IsVisible = i == _settingsSelectedSubSection;
            host.Children.Add(panel);
        }

        return host;
    }

    private ScrollViewer WrapSubSectionScroller(Control content)
    {
        var inner = new StackPanel
        {
            Spacing = 12,
            Margin = new Thickness(14, 10, 14, 12),
            Children = { content },
        };

        return new ScrollViewer
        {
            Content = inner,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = Brushes.Transparent,
        };
    }

    private void SelectSettingsSubSection(int index)
    {
        if (index < 0 || index >= 6) return;
        _settingsSelectedSubSection = index;
        AndroidStorage.SetSettingsActiveSubSection(index);

        for (int i = 0; i < _settingsSubSectionPanels.Length; i++)
        {
            var panel = _settingsSubSectionPanels[i];
            if (panel is not null) panel.IsVisible = i == index;
        }
        for (int i = 0; i < _settingsSubSectionButtons.Length; i++)
        {
            var btn = _settingsSubSectionButtons[i];
            if (btn is not null) StyleSettingsSubSectionButton(btn, i == index);
        }
    }

    private Border BuildSettingsFooterBar()
    {
        var checkGlyph = new TextBlock
        {
            Text = "✓",
            FontSize = UiScale.Fs(11),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = GetBrush("SuccessSolidBrush"),
        };
        var badgeText = new TextBlock
        {
            Text = Localization.SettingsAutosaved,
            FontSize = UiScale.Fs(10),
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = GetBrush("SuccessFgBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        _settingsAutoSavedBadge = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                Spacing = 5,
                Children = { checkGlyph, badgeText },
            },
        };

        _settingsApplyButton = new Avalonia.Controls.Button
        {
            Content = Localization.ApplyNowReloadVpn,
            FontSize = UiScale.Fs(10),
            FontWeight = FontWeight.SemiBold,
            Padding = new Thickness(10, 5),
            CornerRadius = new CornerRadius(GetRadius("RadiusSm")),
            Background = GetBrush("AccentSolidBrush"),
            Foreground = GetBrush("AccentOnSolidBrush"),
            HorizontalAlignment = HorizontalAlignment.Right,
            IsVisible = false,
        };
        _settingsApplyButton.Click += OnSettingsApplyClicked;

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            ColumnSpacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(_settingsAutoSavedBadge, 0);
        Grid.SetColumn(_settingsApplyButton, 1);
        grid.Children.Add(_settingsAutoSavedBadge);
        grid.Children.Add(_settingsApplyButton);

        return new Border
        {
            Padding = new Thickness(14, 7, 14, 8),
            BorderThickness = new Thickness(0, 1, 0, 0),
            BorderBrush = GetBrush("BorderDefaultBrush"),
            Background = GetBrush("SurfaceSunkenBrush"),
            Child = grid,
        };
    }

    private Control BuildSettingsRoutingSection()
    {
        var sectionTitle = MakeSectionTitle(Localization.SettingsSectionRouting);
        var description = new TextBlock
        {
            Text = Localization.RoutingDescription,
            FontSize = UiScale.Fs(11),
            Foreground = GetBrush("TextSecondaryBrush"),
            TextWrapping = TextWrapping.Wrap,
        };

        var routingMode = AndroidStorage.GetRoutingMode();

        _settingsSplitRadio = new Avalonia.Controls.RadioButton
        {
            GroupName = "SettingsRouting",
            IsChecked = routingMode == "split",
            MinHeight = 0,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(0),
        };
        _settingsSplitRadio.IsCheckedChanged += OnSettingsRoutingChanged;
        var splitCard = MakeRadioCard(_settingsSplitRadio,
            Localization.SplitTunnelTitle, Localization.SplitTunnelSubtitle);

        _settingsFullRadio = new Avalonia.Controls.RadioButton
        {
            GroupName = "SettingsRouting",
            IsChecked = routingMode == "full",
            MinHeight = 0,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(0),
        };
        _settingsFullRadio.IsCheckedChanged += OnSettingsRoutingChanged;
        var fullCard = MakeRadioCard(_settingsFullRadio,
            Localization.FullTunnelTitle, Localization.FullTunnelSubtitle);

        _settingsBypassRu = new Avalonia.Controls.CheckBox
        {
            IsChecked = AndroidStorage.GetBypassRussianTraffic(),
            MinHeight = 0,
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 1, 0, 0),
        };
        _settingsBypassRu.IsCheckedChanged += OnSettingsBypassRuChanged;
        var bypassCard = MakeCheckboxCard(_settingsBypassRu,
            Localization.BypassRussianTrafficLabel, Localization.BypassRussianTrafficHint);

        var stack = new StackPanel
        {
            Spacing = 10,
            Children = { sectionTitle, description, splitCard, fullCard, bypassCard }
        };
        return WrapSection(stack);
    }

    private Control BuildSettingsRulesSection()
    {
        var sectionTitle = MakeSectionTitle(Localization.SettingsSectionRules);

        var note = new TextBlock
        {
            Text = Localization.AdvSettingsRulesAndroidNote,
            FontSize = UiScale.Fs(11),
            Foreground = GetBrush("TextSecondaryBrush"),
            TextWrapping = TextWrapping.Wrap,
        };

        var card = new Border
        {
            Padding = new Thickness(10, 8),
            CornerRadius = new CornerRadius(GetRadius("RadiusSm")),
            Background = GetBrush("SurfaceSunkenBrush"),
            BorderBrush = GetBrush("BorderSubtleBrush"),
            BorderThickness = new Thickness(1),
            Child = note,
        };

        var stack = new StackPanel
        {
            Spacing = 10,
            Children = { sectionTitle, card }
        };
        return WrapSection(stack);
    }

    private Control BuildSettingsLeakSection()
    {
        var sectionTitle = MakeSectionTitle(Localization.SettingsSectionLeak);

        var blockHint = new TextBlock
        {
            Text = Localization.BlockOnVpnFailHint,
            FontSize = UiScale.Fs(10),
            Foreground = GetBrush("TextMutedBrush"),
            TextWrapping = TextWrapping.Wrap,
        };
        var lockdownBtn = new Avalonia.Controls.Button
        {
            Content = Localization.ReliabilityAlwaysOnButton,
            FontSize = UiScale.Fs(10),
            Padding = new Thickness(10, 5),
            MinHeight = 0,
            CornerRadius = new CornerRadius(GetRadius("RadiusSm")),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        lockdownBtn.Click += OnReliabilityAlwaysOnClicked;

        var leakInner = new Border
        {
            Padding = new Thickness(8, 6),
            CornerRadius = new CornerRadius(GetRadius("RadiusSm")),
            Background = GetBrush("SurfaceSunkenBrush"),
            BorderBrush = GetBrush("BorderDefaultBrush"),
            BorderThickness = new Thickness(1),
            Child = new StackPanel
            {
                Spacing = 4,
                Children = { blockHint, lockdownBtn }
            }
        };

        _settingsDnsStrategy = new Avalonia.Controls.ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            FontSize = UiScale.Fs(12),
            ItemsSource = new[]
            {
                Localization.DnsStrategyIpv4Only,
                Localization.DnsStrategyPreferIpv4,
                Localization.DnsStrategyPreferIpv6,
            },
            SelectedIndex = AndroidStorage.GetDnsStrategy() switch
            {
                "prefer_ipv4" => 1,
                "prefer_ipv6" => 2,
                _ => 0,
            },
        };
        _settingsDnsStrategy.SelectionChanged += OnSettingsDnsStrategyChanged;

        var dnsHeader = new TextBlock
        {
            Text = Localization.DnsStrategyHeader,
            FontWeight = FontWeight.SemiBold,
            FontSize = UiScale.Fs(11),
            Opacity = 0.8,
            TextWrapping = TextWrapping.Wrap,
        };
        var dnsHint = new TextBlock
        {
            Text = Localization.DnsStrategyHint,
            FontSize = UiScale.Fs(10),
            Foreground = GetBrush("TextMutedBrush"),
            TextWrapping = TextWrapping.Wrap,
        };

        var dnsInner = new Border
        {
            Padding = new Thickness(8, 6),
            CornerRadius = new CornerRadius(GetRadius("RadiusSm")),
            Background = GetBrush("SurfaceSunkenBrush"),
            BorderBrush = GetBrush("BorderDefaultBrush"),
            BorderThickness = new Thickness(1),
            Child = new StackPanel
            {
                Spacing = 4,
                Children = { dnsHeader, _settingsDnsStrategy, dnsHint }
            }
        };

        var stack = new StackPanel
        {
            Spacing = 10,
            Children = { sectionTitle, leakInner, dnsInner }
        };
        return WrapSection(stack);
    }

    private Control BuildSettingsContentSection()
    {
        var sectionTitle = MakeSectionTitle(Localization.SettingsSectionContent);

        _settingsBlockAds = new Avalonia.Controls.CheckBox
        {
            IsChecked = AndroidStorage.GetBlockAds(),
            MinHeight = 0,
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 1, 0, 0),
        };
        _settingsBlockAds.IsCheckedChanged += OnSettingsBlockAdsChanged;
        var card = MakeCheckboxCard(_settingsBlockAds,
            Localization.SettingsBlockAdsLabel,
            Localization.SettingsBlockAdsHint);

        var stack = new StackPanel
        {
            Spacing = 10,
            Children = { sectionTitle, card }
        };
        return WrapSection(stack);
    }

    private Control BuildSettingsUpdatesSection()
    {
        var sectionTitle = MakeSectionTitle(Localization.SettingsSectionUpdates);

        var channelHeader = new TextBlock
        {
            Text = Localization.UpdateChannelHeader,
            FontWeight = FontWeight.SemiBold,
            FontSize = UiScale.Fs(11),
            Opacity = 0.8,
            TextWrapping = TextWrapping.Wrap,
        };

        _settingsReceivePrereleases = new Avalonia.Controls.CheckBox
        {
            IsChecked = AndroidStorage.GetUpdateChannel() == "experimental",
            MinHeight = 0,
            Padding = new Thickness(4, 0),
            Content = new TextBlock
            {
                Text = Localization.ReceivePrereleasesLabel,
                TextWrapping = TextWrapping.Wrap,
                FontSize = UiScale.Fs(11),
            }
        };
        _settingsReceivePrereleases.IsCheckedChanged += OnSettingsChannelChanged;

        var channelInner = new Border
        {
            Padding = new Thickness(8, 6),
            CornerRadius = new CornerRadius(GetRadius("RadiusSm")),
            Background = GetBrush("SurfaceSunkenBrush"),
            BorderBrush = GetBrush("BorderDefaultBrush"),
            BorderThickness = new Thickness(1),
            Child = new StackPanel
            {
                Spacing = 3,
                Children = { channelHeader, _settingsReceivePrereleases }
            }
        };

        _settingsCurrentVersion = new TextBlock
        {
            Text = VPNRouter.Core.AppVersion.Version,
            FontSize = UiScale.Fs(12),
            FontWeight = FontWeight.SemiBold,
            Foreground = GetBrush("TextPrimaryBrush"),
        };
        var versionLabel = new TextBlock
        {
            Text = Localization.CurrentVersionLabel,
            FontSize = UiScale.Fs(10),
            Opacity = 0.7,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var versionStack = new StackPanel
        {
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { versionLabel, _settingsCurrentVersion }
        };

        var checkBtn = new Avalonia.Controls.Button
        {
            Content = Localization.CheckForUpdatesButton,
            FontSize = UiScale.Fs(10),
            Padding = new Thickness(10, 5),
            MinHeight = 0,
            CornerRadius = new CornerRadius(GetRadius("RadiusSm")),
            VerticalAlignment = VerticalAlignment.Center,
        };
        checkBtn.Click += OnSettingsCheckUpdatesClicked;

        var versionRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            ColumnSpacing = 8,
        };
        Grid.SetColumn(versionStack, 0);
        Grid.SetColumn(checkBtn, 1);
        versionRow.Children.Add(versionStack);
        versionRow.Children.Add(checkBtn);

        var versionInner = new Border
        {
            Padding = new Thickness(8, 6),
            CornerRadius = new CornerRadius(GetRadius("RadiusSm")),
            Background = GetBrush("SurfaceSunkenBrush"),
            BorderBrush = GetBrush("BorderDefaultBrush"),
            BorderThickness = new Thickness(1),
            Child = versionRow,
        };

        var stack = new StackPanel
        {
            Spacing = 10,
            Children = { sectionTitle, channelInner, versionInner }
        };
        return WrapSection(stack);
    }

    private Control BuildSettingsAutostartSection()
    {
        var sectionTitle = MakeSectionTitle(Localization.SettingsSectionAutostart);

        var androidIntro = new TextBlock
        {
            Text = Localization.AdvSettingsAutostartAndroidIntro,
            FontSize = UiScale.Fs(11),
            Foreground = GetBrush("TextSecondaryBrush"),
            TextWrapping = TextWrapping.Wrap,
        };

        var alwaysOnTitle = new TextBlock
        {
            Text = Localization.ReliabilityAlwaysOnTitle,
            FontWeight = FontWeight.SemiBold,
            FontSize = UiScale.Fs(11),
            Foreground = GetBrush("TextPrimaryBrush"),
        };
        var alwaysOnHint = new TextBlock
        {
            Text = Localization.ReliabilityAlwaysOnHint,
            FontSize = UiScale.Fs(10),
            Foreground = GetBrush("TextMutedBrush"),
            TextWrapping = TextWrapping.Wrap,
        };
        var alwaysOnBtn = new Avalonia.Controls.Button
        {
            Content = Localization.ReliabilityAlwaysOnButton,
            FontSize = UiScale.Fs(10),
            Padding = new Thickness(10, 5),
            MinHeight = 0,
            CornerRadius = new CornerRadius(GetRadius("RadiusSm")),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        alwaysOnBtn.Click += OnReliabilityAlwaysOnClicked;
        var alwaysOnRow = new StackPanel
        {
            Spacing = 4,
            Children = { alwaysOnTitle, alwaysOnHint, alwaysOnBtn },
        };

        var batteryTitle = new TextBlock
        {
            Text = Localization.ReliabilityBatteryOptTitle,
            FontWeight = FontWeight.SemiBold,
            FontSize = UiScale.Fs(11),
            Foreground = GetBrush("TextPrimaryBrush"),
        };
        _reliabilityBatteryStatusLabel = new TextBlock
        {
            FontSize = UiScale.Fs(10),
            TextWrapping = TextWrapping.Wrap,
        };
        var batteryHint = new TextBlock
        {
            Text = Localization.ReliabilityBatteryOptHint,
            FontSize = UiScale.Fs(10),
            Foreground = GetBrush("TextMutedBrush"),
            TextWrapping = TextWrapping.Wrap,
        };
        _reliabilityBatteryButton = new Avalonia.Controls.Button
        {
            FontSize = UiScale.Fs(10),
            Padding = new Thickness(10, 5),
            MinHeight = 0,
            CornerRadius = new CornerRadius(GetRadius("RadiusSm")),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        _reliabilityBatteryButton.Click += OnReliabilityBatteryClicked;
        UpdateBatteryOptimizationStatus();
        var batteryRow = new StackPanel
        {
            Spacing = 4,
            Children =
            {
                batteryTitle,
                _reliabilityBatteryStatusLabel,
                batteryHint,
                _reliabilityBatteryButton,
            },
        };

        _reliabilityAutoReconnect = new Avalonia.Controls.CheckBox
        {
            IsChecked = AndroidStorage.GetAutoReconnectOnNetworkChange(),
            MinHeight = 0,
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 1, 0, 0),
        };
        _reliabilityAutoReconnect.IsCheckedChanged += OnReliabilityAutoReconnectChanged;
        var autoReconnectCard = MakeCheckboxCard(_reliabilityAutoReconnect,
            Localization.ReliabilityAutoReconnectTitle,
            Localization.ReliabilityAutoReconnectHint);

        _externalControlToggle = new Avalonia.Controls.CheckBox
        {
            IsChecked = AndroidStorage.GetExternalControlEnabled(),
            MinHeight = 0,
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 1, 0, 0),
        };
        _externalControlToggle.IsCheckedChanged += OnExternalControlChanged;
        var externalControlCard = MakeCheckboxCard(_externalControlToggle,
            Localization.ExternalControlTitle,
            Localization.ExternalControlHint);

        var stack = new StackPanel
        {
            Spacing = 14,
            Children =
            {
                sectionTitle,
                androidIntro,
                alwaysOnRow,
                batteryRow,
                autoReconnectCard,
                externalControlCard,
            }
        };
        return WrapSection(stack);
    }

    private TextBlock MakeSectionTitle(string text) => new TextBlock
    {
        Text = text,
        FontWeight = FontWeight.Bold,
        FontSize = UiScale.Fs(13),
        Foreground = GetBrush("TextPrimaryBrush"),
    };

    private Border WrapSection(Control content) => new Border
    {
        Padding = new Thickness(12),
        CornerRadius = new CornerRadius(GetRadius("RadiusSm")),
        Background = GetBrush("SurfaceBaseBrush"),
        BorderBrush = GetBrush("BorderSubtleBrush"),
        BorderThickness = new Thickness(1),
        Child = content,
    };

    private Border MakeRadioCard(Avalonia.Controls.RadioButton radio, string title, string subtitle)
    {
        var titleText = new TextBlock
        {
            Text = title,
            FontWeight = FontWeight.SemiBold,
            FontSize = UiScale.Fs(11),
            Foreground = GetBrush("TextPrimaryBrush"),
        };
        var subText = new TextBlock
        {
            Text = subtitle,
            FontSize = UiScale.Fs(10),
            Foreground = GetBrush("TextSecondaryBrush"),
            TextWrapping = TextWrapping.Wrap,
        };
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("24,*"),
            ColumnSpacing = 8,
        };
        Grid.SetColumn(radio, 0);
        var rightStack = new StackPanel { Spacing = 2, Children = { titleText, subText } };
        Grid.SetColumn(rightStack, 1);
        grid.Children.Add(radio);
        grid.Children.Add(rightStack);

        var card = new Border
        {
            Padding = new Thickness(10, 8),
            CornerRadius = new CornerRadius(GetRadius("RadiusSm")),
            Background = GetBrush("SurfaceSunkenBrush"),
            BorderBrush = GetBrush("BorderSubtleBrush"),
            BorderThickness = new Thickness(1),
            Child = grid,
        };
        card.PointerPressed += (_, __) =>
        {
            radio.IsChecked = true;
        };
        return card;
    }

    private Border MakeCheckboxCard(Avalonia.Controls.CheckBox cb, string title, string subtitle)
    {
        var titleText = new TextBlock
        {
            Text = title,
            FontWeight = FontWeight.SemiBold,
            FontSize = UiScale.Fs(11),
            Foreground = GetBrush("TextPrimaryBrush"),
            TextWrapping = TextWrapping.Wrap,
        };
        var subText = new TextBlock
        {
            Text = subtitle,
            FontSize = UiScale.Fs(10),
            Foreground = GetBrush("TextSecondaryBrush"),
            TextWrapping = TextWrapping.Wrap,
        };
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("24,*"),
            ColumnSpacing = 8,
        };
        Grid.SetColumn(cb, 0);
        var rightStack = new StackPanel { Spacing = 2, Children = { titleText, subText } };
        Grid.SetColumn(rightStack, 1);
        grid.Children.Add(cb);
        grid.Children.Add(rightStack);

        var card = new Border
        {
            Padding = new Thickness(10, 8),
            CornerRadius = new CornerRadius(GetRadius("RadiusSm")),
            Background = GetBrush("SurfaceSunkenBrush"),
            BorderBrush = GetBrush("BorderSubtleBrush"),
            BorderThickness = new Thickness(1),
            Child = grid,
        };
        card.PointerPressed += (_, __) =>
        {
            cb.IsChecked = !(cb.IsChecked == true);
        };
        return card;
    }
}
