using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;

namespace VPNRouter.Android;

public partial class AndroidApp
{
    private Control BuildTelegramTabContent()
    {
        var bg          = GetBrush("SurfaceAppBrush");
        var subtle      = GetBrush("BorderSubtleBrush");
        var defaultB    = GetBrush("BorderDefaultBrush");
        var card        = GetBrush("SurfaceBaseBrush");
        var textP       = GetBrush("TextPrimaryBrush");
        var textS       = GetBrush("TextSecondaryBrush");
        var textM       = GetBrush("TextMutedBrush");
        var radiusSm    = GetRadius("RadiusSm");

        var tgDescription = new TextBlock
        {
            Text = Localization.AndroidTgProxyNotApplicable,
            FontSize = 11,
            LineHeight = 16,
            Opacity = 0.8,
            Foreground = textS,
            TextWrapping = TextWrapping.Wrap,
        };

        var tgInfoDot = new Ellipse
        {
            Width = 8,
            Height = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Fill = textM,
        };
        var tgInfoText = new TextBlock
        {
            Text = Localization.AutostartTgProxyNotPorted,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = textS,
        };
        var tgInfoRow = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { tgInfoDot, tgInfoText },
        };
        var tgInfoBanner = new Border
        {
            Padding = new Thickness(10, 8),
            CornerRadius = new CornerRadius(radiusSm),
            Background = card,
            BorderBrush = subtle,
            BorderThickness = new Thickness(1),
            Child = tgInfoRow,
        };

        var tgGithubBtn = new Avalonia.Controls.Button
        {
            Content = "GitHub: Flowseal/tg-ws-proxy",
            FontSize = 10,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(0, 8),
            CornerRadius = new CornerRadius(radiusSm),
            Background = Brushes.Transparent,
            BorderBrush = defaultB,
            BorderThickness = new Thickness(1),
            Foreground = GetBrush("AccentFgBrush"),
        };
        tgGithubBtn.Click += (_, _) =>
        {
            try
            {
                var intent = new global::Android.Content.Intent(
                    global::Android.Content.Intent.ActionView,
                    global::Android.Net.Uri.Parse("https://github.com/Flowseal/tg-ws-proxy"));
                intent.SetFlags(global::Android.Content.ActivityFlags.NewTask);
                global::Android.App.Application.Context.StartActivity(intent);
            }
            catch { }
        };

        var tgSectionTitle = new TextBlock
        {
            Text = Localization.ToolsTabTgProxy,
            FontWeight = FontWeight.Bold,
            FontSize = 13,
            Foreground = textP,
        };

        var tgBodyStack = new StackPanel
        {
            Spacing = 10,
            Children = { tgSectionTitle, tgInfoBanner, tgDescription, tgGithubBtn },
        };

        return new ScrollViewer
        {
            Content = new Border
            {
                Padding = new Thickness(12, 10, 12, 12),
                Child = tgBodyStack,
            },
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = bg,
        };
    }

    private static string ZapretStatusLabelForCurrentMode()
    {
        return AndroidStorage.GetDpiBypassMode() switch
        {
            "standard"   => Localization.AndroidZapretStatusStandard,
            "aggressive" => Localization.AndroidZapretStatusAggressive,
            _            => Localization.AndroidZapretStatusOff,
        };
    }

    private int _toolsSelectedSubTab;

    private Avalonia.Controls.Button? _toolsSubTabZapret;
    private Avalonia.Controls.Button? _toolsSubTabTelegram;
    private Control? _toolsZapretBody;
    private Control? _toolsTelegramBody;

    private Avalonia.Controls.RadioButton? _toolsZapretModeOff;
    private Avalonia.Controls.RadioButton? _toolsZapretModeStandard;
    private Avalonia.Controls.RadioButton? _toolsZapretModeAggressive;
    private Ellipse? _toolsZapretFooterDot;
    private TextBlock? _toolsZapretFooterText;
    private Avalonia.Controls.Button? _toolsZapretFooterToggleBtn;

    private bool _toolsLoading;

    private Control BuildToolsTabContent()
    {
        var bg          = GetBrush("SurfaceAppBrush");
        var defaultB    = GetBrush("BorderDefaultBrush");
        var sunken      = GetBrush("SurfaceSunkenBrush");
        var radiusSm    = GetRadius("RadiusSm");
        var accentSolid = GetBrush("AccentSolidBrush");
        var accentOnSolid = GetBrush("AccentOnSolidBrush");
        var textS       = GetBrush("TextSecondaryBrush");

        _toolsSubTabZapret   = MakeAdvancedSubTabButton(Localization.AdvToolsSubTabZapret,   active: true,
                                                        (_, _) => SelectToolsSubTab(0));
        _toolsSubTabTelegram = MakeAdvancedSubTabButton(Localization.AdvToolsSubTabTelegram, active: false,
                                                        (_, _) => SelectToolsSubTab(1));

        var subTabRow = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 4,
            Margin = new Thickness(6, 4, 6, 4),
            Children = { _toolsSubTabZapret, _toolsSubTabTelegram },
        };

        _toolsZapretBody   = BuildToolsZapretBody(bg, sunken, defaultB, accentSolid, accentOnSolid, radiusSm, textS);
        _toolsTelegramBody = BuildToolsTelegramBody(bg, sunken, defaultB, radiusSm, textS);
        _toolsTelegramBody.IsVisible = false;

        var bodyArea = new Grid
        {
            Children = { _toolsZapretBody, _toolsTelegramBody },
        };

        var dock = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(subTabRow, Dock.Top);
        dock.Children.Add(subTabRow);
        dock.Children.Add(bodyArea);

        UpdateToolsZapretFooterState();

        return new Border
        {
            Background = bg,
            Child = dock,
        };
    }

    private Control BuildToolsZapretBody(
        IBrush bg, IBrush sunken, IBrush defaultB,
        IBrush accentSolid, IBrush accentOnSolid,
        double radiusSm, IBrush textS)
    {
        var textP    = GetBrush("TextPrimaryBrush");
        var textM    = GetBrush("TextMutedBrush");
        var card     = GetBrush("SurfaceBaseBrush");
        var subtle   = GetBrush("BorderSubtleBrush");

        var explainerTitle = new TextBlock
        {
            Text = Localization.AdvToolsSubTabZapret,
            FontWeight = FontWeight.Bold,
            FontSize = 13,
            Foreground = textP,
        };
        var explainerText = new TextBlock
        {
            Text = Localization.AdvToolsZapretAndroidExplainer,
            FontSize = 11,
            LineHeight = 16,
            Opacity = 0.8,
            Foreground = textS,
            TextWrapping = TextWrapping.Wrap,
        };
        var explainerCard = new Border
        {
            Padding = new Thickness(10, 8),
            CornerRadius = new CornerRadius(radiusSm),
            Background = sunken,
            BorderBrush = subtle,
            BorderThickness = new Thickness(1),
            Child = new StackPanel
            {
                Spacing = 6,
                Children = { explainerTitle, explainerText },
            },
        };

        var currentMode = AndroidStorage.GetDpiBypassMode();
        _toolsZapretModeOff = MakeToolsZapretRadio(
            Localization.SettingsDpiBypassOff,        currentMode == "off",        textP);
        _toolsZapretModeStandard = MakeToolsZapretRadio(
            Localization.SettingsDpiBypassStandard,   currentMode == "standard",   textP);
        _toolsZapretModeAggressive = MakeToolsZapretRadio(
            Localization.SettingsDpiBypassAggressive, currentMode == "aggressive", textP);

        _toolsZapretModeOff.IsCheckedChanged        += OnToolsZapretModeChanged;
        _toolsZapretModeStandard.IsCheckedChanged   += OnToolsZapretModeChanged;
        _toolsZapretModeAggressive.IsCheckedChanged += OnToolsZapretModeChanged;

        var modePicker = new Border
        {
            Padding = new Thickness(10, 8),
            CornerRadius = new CornerRadius(radiusSm),
            Background = sunken,
            BorderBrush = subtle,
            BorderThickness = new Thickness(1),
            Child = new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    _toolsZapretModeOff,
                    _toolsZapretModeStandard,
                    _toolsZapretModeAggressive,
                },
            },
        };

        var bodyStack = new StackPanel
        {
            Spacing = 10,
            Children = { explainerCard, modePicker },
        };

        var bodyScroller = new ScrollViewer
        {
            Content = new Border
            {
                Padding = new Thickness(12, 10, 12, 12),
                Child = bodyStack,
            },
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = bg,
        };

        _toolsZapretFooterDot = new Ellipse
        {
            Width = 8,
            Height = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _toolsZapretFooterText = new TextBlock
        {
            Text = ZapretStatusLabelForCurrentMode(),
            FontSize = 10,
            FontWeight = FontWeight.SemiBold,
            Foreground = textS,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var footerStatusRow = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _toolsZapretFooterDot, _toolsZapretFooterText },
        };

        _toolsZapretFooterToggleBtn = new Avalonia.Controls.Button
        {
            FontSize = 10,
            FontWeight = FontWeight.SemiBold,
            Padding = new Thickness(10, 5),
            CornerRadius = new CornerRadius(radiusSm),
            Background = accentSolid,
            Foreground = accentOnSolid,
            BorderThickness = new Thickness(0),
            Content = currentMode == "off"
                ? Localization.AndroidDpiBypassFooterToggleOn
                : Localization.AndroidDpiBypassFooterToggleOff,
        };
        _toolsZapretFooterToggleBtn.Click += OnToolsZapretFooterToggleClicked;

        var footerGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            ColumnSpacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(footerStatusRow, 0);
        Grid.SetColumn(_toolsZapretFooterToggleBtn, 1);
        footerGrid.Children.Add(footerStatusRow);
        footerGrid.Children.Add(_toolsZapretFooterToggleBtn);

        var footer = new Border
        {
            Padding = new Thickness(14, 7, 14, 8),
            BorderThickness = new Thickness(0, 1, 0, 0),
            BorderBrush = defaultB,
            Background = sunken,
            Child = footerGrid,
        };

        var dock = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(footer, Dock.Bottom);
        dock.Children.Add(footer);
        dock.Children.Add(bodyScroller);

        return dock;
    }

    private Avalonia.Controls.RadioButton MakeToolsZapretRadio(
        string label, bool isChecked, IBrush textP)
    {
        return new Avalonia.Controls.RadioButton
        {
            GroupName = "ToolsZapretMode",
            IsChecked = isChecked,
            MinHeight = 0,
            Padding = new Thickness(4, 0),
            Content = new TextBlock
            {
                Text = label,
                FontSize = 11,
                Foreground = textP,
                TextWrapping = TextWrapping.Wrap,
            },
        };
    }

    private Control BuildToolsTelegramBody(
        IBrush bg, IBrush sunken, IBrush defaultB, double radiusSm, IBrush textS)
    {
        var textP  = GetBrush("TextPrimaryBrush");
        var textM  = GetBrush("TextMutedBrush");
        var card   = GetBrush("SurfaceBaseBrush");
        var subtle = GetBrush("BorderSubtleBrush");
        var accentSolid   = GetBrush("AccentSolidBrush");
        var accentOnSolid = GetBrush("AccentOnSolidBrush");
        var accentFg      = GetBrush("AccentFgBrush");

        var explainerTitle = new TextBlock
        {
            Text = Localization.AdvToolsSubTabTelegram,
            FontWeight = FontWeight.Bold,
            FontSize = 13,
            Foreground = textP,
        };
        var explainerText = new TextBlock
        {
            Text = Localization.AdvToolsTelegramAndroidExplainer,
            FontSize = 11,
            LineHeight = 16,
            Opacity = 0.8,
            Foreground = textS,
            TextWrapping = TextWrapping.Wrap,
        };
        var explainerCard = new Border
        {
            Padding = new Thickness(10, 8),
            CornerRadius = new CornerRadius(radiusSm),
            Background = sunken,
            BorderBrush = subtle,
            BorderThickness = new Thickness(1),
            Child = new StackPanel
            {
                Spacing = 6,
                Children = { explainerTitle, explainerText },
            },
        };

        var openTgBtn = new Avalonia.Controls.Button
        {
            Content = Localization.AdvToolsOpenTelegram,
            FontSize = 13,
            FontWeight = FontWeight.Bold,
            Padding = new Thickness(0, 10),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Background = accentSolid,
            Foreground = accentOnSolid,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(radiusSm),
        };
        openTgBtn.Click += OnToolsOpenTelegramClicked;

        var githubBtn = new Avalonia.Controls.Button
        {
            Content = "GitHub: Flowseal/tg-ws-proxy",
            FontSize = 10,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(0, 8),
            CornerRadius = new CornerRadius(radiusSm),
            Background = Brushes.Transparent,
            BorderBrush = defaultB,
            BorderThickness = new Thickness(1),
            Foreground = accentFg,
        };
        githubBtn.Click += (_, _) =>
        {
            try
            {
                var intent = new global::Android.Content.Intent(
                    global::Android.Content.Intent.ActionView,
                    global::Android.Net.Uri.Parse("https://github.com/Flowseal/tg-ws-proxy"));
                intent.SetFlags(global::Android.Content.ActivityFlags.NewTask);
                global::Android.App.Application.Context.StartActivity(intent);
            }
            catch { }
        };

        var bodyStack = new StackPanel
        {
            Spacing = 10,
            Children = { explainerCard, openTgBtn, githubBtn },
        };

        return new ScrollViewer
        {
            Content = new Border
            {
                Padding = new Thickness(12, 10, 12, 12),
                Child = bodyStack,
            },
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = bg,
        };
    }

    private void SelectToolsSubTab(int index)
    {
        _toolsSelectedSubTab = index;
        if (_toolsZapretBody   is not null) _toolsZapretBody.IsVisible   = index == 0;
        if (_toolsTelegramBody is not null) _toolsTelegramBody.IsVisible = index == 1;
        if (_toolsSubTabZapret is not null)
            StyleAdvShellTab(_toolsSubTabZapret, index == 0);
        if (_toolsSubTabTelegram is not null)
            StyleAdvShellTab(_toolsSubTabTelegram, index == 1);
    }

    private void OnToolsZapretModeChanged(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_toolsLoading) return;
        string mode = "off";
        if (_toolsZapretModeStandard?.IsChecked == true)        mode = "standard";
        else if (_toolsZapretModeAggressive?.IsChecked == true) mode = "aggressive";
        else                                                     mode = "off";

        AndroidStorage.SetDpiBypassMode(mode);

        var index = mode switch
        {
            "standard"   => 1,
            "aggressive" => 2,
            _            => 0,
        };
        if (_settingsDpiBypassMode is not null)
        {
            _settingsLoading = true;
            try { _settingsDpiBypassMode.SelectedIndex = index; }
            finally { _settingsLoading = false; }
        }
        if (_dpiStrategyComboBox is not null)
        {
            _dpiStrategyComboBox.SelectedIndex = index;
        }

        UpdateZapretChipFromState();
        UpdateToolsZapretFooterState();
        UpdateDpiFooterState();
    }

    private void OnToolsZapretFooterToggleClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var current = AndroidStorage.GetDpiBypassMode();
        var next = current == "off" ? "standard" : "off";
        AndroidStorage.SetDpiBypassMode(next);

        _toolsLoading = true;
        try
        {
            if (_toolsZapretModeOff is not null)        _toolsZapretModeOff.IsChecked        = next == "off";
            if (_toolsZapretModeStandard is not null)   _toolsZapretModeStandard.IsChecked   = next == "standard";
            if (_toolsZapretModeAggressive is not null) _toolsZapretModeAggressive.IsChecked = next == "aggressive";
        }
        finally { _toolsLoading = false; }

        var index = next switch
        {
            "standard"   => 1,
            "aggressive" => 2,
            _            => 0,
        };
        if (_settingsDpiBypassMode is not null)
        {
            _settingsLoading = true;
            try { _settingsDpiBypassMode.SelectedIndex = index; }
            finally { _settingsLoading = false; }
        }
        if (_dpiStrategyComboBox is not null)
            _dpiStrategyComboBox.SelectedIndex = index;

        UpdateZapretChipFromState();
        UpdateToolsZapretFooterState();
        UpdateDpiFooterState();
    }

    private void UpdateToolsZapretFooterState()
    {
        var mode = AndroidStorage.GetDpiBypassMode();
        var enabled = mode != "off";
        if (_toolsZapretFooterText is not null)
            _toolsZapretFooterText.Text = ZapretStatusLabelForCurrentMode();
        if (_toolsZapretFooterDot is not null)
            _toolsZapretFooterDot.Fill = GetBrush(enabled
                ? "SuccessSolidBrush" : "TextMutedBrush");
        if (_toolsZapretFooterToggleBtn is not null)
            _toolsZapretFooterToggleBtn.Content = enabled
                ? Localization.AndroidDpiBypassFooterToggleOff
                : Localization.AndroidDpiBypassFooterToggleOn;
    }

    private void ReseedToolsTabState()
    {
        var mode = AndroidStorage.GetDpiBypassMode();
        _toolsLoading = true;
        try
        {
            if (_toolsZapretModeOff is not null)        _toolsZapretModeOff.IsChecked        = mode == "off";
            if (_toolsZapretModeStandard is not null)   _toolsZapretModeStandard.IsChecked   = mode == "standard";
            if (_toolsZapretModeAggressive is not null) _toolsZapretModeAggressive.IsChecked = mode == "aggressive";
        }
        finally { _toolsLoading = false; }
        UpdateToolsZapretFooterState();
    }

    private void OnToolsOpenTelegramClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            var ctx = global::Android.App.Application.Context;
            var pm  = ctx?.PackageManager;
            var launchIntent = pm?.GetLaunchIntentForPackage("org.telegram.messenger");
            if (launchIntent is not null)
            {
                launchIntent.SetFlags(global::Android.Content.ActivityFlags.NewTask);
                ctx!.StartActivity(launchIntent);
                return;
            }

            ShowMenuFeedback(Localization.AdvToolsTelegramNotInstalled);
            var marketIntent = new global::Android.Content.Intent(
                global::Android.Content.Intent.ActionView,
                global::Android.Net.Uri.Parse("market://details?id=org.telegram.messenger"));
            marketIntent.SetFlags(global::Android.Content.ActivityFlags.NewTask);
            ctx!.StartActivity(marketIntent);
        }
        catch
        {
        }
    }
}
