using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;

namespace VPNRouter.Android;

/// <summary>
/// F-13 (2026-05-09) — Android DPI Bypass overlay. Mirrors desktop's
/// <c>VPNRouter.App/Views/Pages/DpiBypassPage.axaml</c>: master-detail
/// layout (sidebar + Status / Strategy / Hosts / Filters / Advanced
/// sections) with a footer Apply bar (status indicator + toggle button).
///
/// <para>Mobile adaptation per CLAUDE.md guidance: «Android may collapse
/// sidebar to top tabs given narrow viewport — keep functional parity.»
/// The 140-px sidebar moves to a horizontal sub-tab strip at the top.
/// Hosts and Filters sections are skipped (they manage Windows hosts file
/// + ipset filter — both winws.exe-only). Status / Strategy / Advanced
/// each get a section.</para>
///
/// <para>The Strategy ComboBox + warning banner already render inside
/// Settings → Routing via <c>BuildDpiBypassCard</c>. This overlay reuses
/// the same AndroidStorage state but presents it as a dedicated page so
/// the structural shell matches desktop. Both surfaces stay in sync via
/// the shared <c>SetDpiBypassMode</c> writer.</para>
/// </summary>
public partial class AndroidApp
{
    // AND-MIGRATE-OVERLAYS (2026-05-09): the standalone DPI Bypass
    // overlay is gone — content moves into the Advanced shell as the
    // "DPI bypass" tab.

    // Top-tab strip (replaces sidebar list — Status / Strategy / Advanced).
    private int _dpiSelectedTab;
    private Avalonia.Controls.Button? _dpiTabStatus;
    private Avalonia.Controls.Button? _dpiTabStrategy;
    private Avalonia.Controls.Button? _dpiTabAdvanced;
    private Control? _dpiBodyStatus;
    private Control? _dpiBodyStrategy;
    private Control? _dpiBodyAdvanced;

    // Strategy section ComboBox — kept in sync with the Settings overlay's
    // _settingsDpiBypassMode so flipping mode in either surface refreshes
    // the other on its next show.
    private Avalonia.Controls.ComboBox? _dpiStrategyComboBox;

    // Footer (Apply-bar) widgets — mirror desktop DpiBypassPage footer
    // lines 379-407.
    private Ellipse? _dpiFooterStatusDot;
    private TextBlock? _dpiFooterStatusText;
    private Avalonia.Controls.Button? _dpiFooterToggleBtn;

    // Status-bar widgets cached for re-seed during ReseedDpiBypassTabState.
    private Ellipse? _dpiStatusBarDot;
    private TextBlock? _dpiStatusBarText;

    /// <summary>
    /// AND-MIGRATE-OVERLAYS (2026-05-09) — body content for the DPI bypass
    /// tab inside the Advanced shell. Returns the inner sub-tab strip
    /// (Status / Strategy / Advanced) + bodies + footer Apply bar. The
    /// outer shell provides the title bar / close button.
    /// </summary>
    private Control BuildDpiBypassTabContent()
    {
        var bg          = GetBrush("SurfaceAppBrush");
        var subtle      = GetBrush("BorderSubtleBrush");
        var defaultB    = GetBrush("BorderDefaultBrush");
        var sunken      = GetBrush("SurfaceSunkenBrush");
        var textP       = GetBrush("TextPrimaryBrush");
        var textS       = GetBrush("TextSecondaryBrush");
        var textM       = GetBrush("TextMutedBrush");
        var warningBg   = GetBrush("WarningBgBrush");
        var warningBd   = GetBrush("WarningBorderBrush");
        var warningFg   = GetBrush("WarningFgBrush");
        var accentSolid = GetBrush("AccentSolidBrush");
        var accentOnSolid = GetBrush("AccentOnSolidBrush");
        var radiusSm    = GetRadius("RadiusSm");

        // ── Top-tab strip (collapsed sidebar) ────────────────────────────
        // POL-1: matches desktop ListBox sub-tab pattern via the shared
        // MakeAdvancedSubTabButton (Padding="10,4" FontSize="11" RadiusSm).
        // Pre-POL-1 used the kebab MakeSegmentButton helper which sized
        // each chip Padding="0,6" FontSize=12 RadiusXs.
        _dpiTabStatus   = MakeAdvancedSubTabButton(Localization.ZapretSecStatus,   active: true,
                                                   (_, _) => SelectDpiTab(0));
        _dpiTabStrategy = MakeAdvancedSubTabButton(Localization.ZapretSecStrategy, active: false,
                                                   (_, _) => SelectDpiTab(1));
        _dpiTabAdvanced = MakeAdvancedSubTabButton(Localization.ZapretSecAdvanced, active: false,
                                                   (_, _) => SelectDpiTab(2));

        var tabRow = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 4,
            Margin = new Thickness(6, 4, 6, 4),
            Children = { _dpiTabStatus, _dpiTabStrategy, _dpiTabAdvanced },
        };

        // ── Status section ───────────────────────────────────────────────
        // Mirrors DpiBypassPage Status pane (lines 82-146):
        //   • section title
        //   • description blurb (LblDpiDescription)
        //   • status indicator border (sunken bg + dot + text)
        //   • warning banner (yellow bg + ⚠ + LblDpiWarning)
        var statusTitle = new TextBlock
        {
            Text = Localization.ZapretSecStatus,
            FontWeight = FontWeight.Bold,
            FontSize = 13,
            Foreground = textP,
        };
        var statusDesc = new TextBlock
        {
            Text = Localization.SettingsDpiBypassHint,
            FontSize = 10,
            Opacity = 0.7,
            Foreground = textS,
            TextWrapping = TextWrapping.Wrap,
        };

        _dpiStatusBarDot = new Ellipse
        {
            Width = 8,
            Height = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _dpiStatusBarText = new TextBlock
        {
            Text = ZapretStatusLabelForCurrentMode(),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = textP,
        };
        var statusInnerRow = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 8,
            Children = { _dpiStatusBarDot, _dpiStatusBarText },
        };
        var statusBar = new Border
        {
            Padding = new Thickness(10, 8),
            CornerRadius = new CornerRadius(radiusSm),
            Background = sunken,
            Child = statusInnerRow,
        };

        var warningIcon = new TextBlock
        {
            Text = "⚠",
            FontSize = 11,
            FontWeight = FontWeight.Bold,
            Foreground = warningFg,
            VerticalAlignment = VerticalAlignment.Top,
        };
        var warningText = new TextBlock
        {
            Text = Localization.SettingsDpiBypassWarning,
            FontSize = 10,
            TextWrapping = TextWrapping.Wrap,
            Foreground = warningFg,
        };
        var warningGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            ColumnSpacing = 8,
        };
        Grid.SetColumn(warningIcon, 0);
        Grid.SetColumn(warningText, 1);
        warningGrid.Children.Add(warningIcon);
        warningGrid.Children.Add(warningText);
        var warningBanner = new Border
        {
            Padding = new Thickness(10, 8),
            CornerRadius = new CornerRadius(radiusSm),
            Background = warningBg,
            BorderBrush = warningBd,
            BorderThickness = new Thickness(1),
            Child = warningGrid,
        };

        var statusStack = new StackPanel
        {
            Spacing = 10,
            Children = { statusTitle, statusDesc, statusBar, warningBanner },
        };
        _dpiBodyStatus = new ScrollViewer
        {
            Content = new Border
            {
                Padding = new Thickness(12, 10, 12, 12),
                Child = statusStack,
            },
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = bg,
        };

        // ── Strategy section ─────────────────────────────────────────────
        // Mirrors DpiBypassPage Strategy pane (lines 158-209):
        //   • section title
        //   • 1-line description (ZapretSecStrategyDesc)
        //   • ComboBox picker (Off / Standard / Aggressive)
        //   • version blurb (ZapretVersionText desktop equivalent — on
        //     Android it's just "in-tunnel via sing-box" since there's
        //     no separate binary).
        var strategyTitle = new TextBlock
        {
            Text = Localization.ZapretSecStrategy,
            FontWeight = FontWeight.Bold,
            FontSize = 13,
            Foreground = textP,
        };
        var strategyDesc = new TextBlock
        {
            Text = Localization.ZapretSecStrategyDesc,
            FontSize = 10,
            Opacity = 0.7,
            Foreground = textS,
            TextWrapping = TextWrapping.Wrap,
        };

        _dpiStrategyComboBox = new Avalonia.Controls.ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(8, 4),
            FontSize = 11,
            ItemsSource = new List<string>
            {
                Localization.SettingsDpiBypassOff,
                Localization.SettingsDpiBypassStandard,
                Localization.SettingsDpiBypassAggressive,
            },
            SelectedIndex = AndroidStorage.GetDpiBypassMode() switch
            {
                "standard"   => 1,
                "aggressive" => 2,
                _            => 0,
            },
        };
        _dpiStrategyComboBox.SelectionChanged += OnDpiStrategyChanged;

        var versionRow = new TextBlock
        {
            Text = IsRu()
                ? "Встроенный sing-box (tls_fragment) — без отдельной службы"
                : "Built-in sing-box (tls_fragment) — no separate service",
            FontSize = 10,
            Opacity = 0.5,
            Foreground = textM,
            TextWrapping = TextWrapping.Wrap,
        };

        var strategyStack = new StackPanel
        {
            Spacing = 10,
            Children = { strategyTitle, strategyDesc, _dpiStrategyComboBox, versionRow },
        };
        _dpiBodyStrategy = new ScrollViewer
        {
            Content = new Border
            {
                Padding = new Thickness(12, 10, 12, 12),
                Child = strategyStack,
            },
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = bg,
            IsVisible = false,
        };

        // ── Advanced section ─────────────────────────────────────────────
        // Mirrors desktop's Advanced pane (lines 303-348) — diagnostics +
        // service controls. On Android only Run health check + Open log
        // make sense (no Zapret service to remove, no clear-cache for the
        // browser sandbox, no service.bat menu).
        var advTitle = new TextBlock
        {
            Text = Localization.ZapretSecAdvanced,
            FontWeight = FontWeight.Bold,
            FontSize = 13,
            Foreground = textP,
        };
        var advDesc = new TextBlock
        {
            Text = Localization.ZapretSecAdvancedDesc,
            FontSize = 10,
            Opacity = 0.7,
            Foreground = textS,
            TextWrapping = TextWrapping.Wrap,
        };

        var advHealthBtn = new Avalonia.Controls.Button
        {
            Content = Localization.AndroidToolsRunHealthCheck,
            FontSize = 10,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(10, 6),
            CornerRadius = new CornerRadius(radiusSm),
            Background = Brushes.Transparent,
            BorderBrush = defaultB,
            BorderThickness = new Thickness(1),
            Foreground = textP,
        };
        advHealthBtn.Click += (s, e) =>
        {
            CloseAdvancedShell();
            OnMenuHealthCheckClicked(s, e);
        };

        var advLogBtn = new Avalonia.Controls.Button
        {
            Content = Localization.AndroidToolsOpenLog,
            FontSize = 10,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(10, 6),
            CornerRadius = new CornerRadius(radiusSm),
            Background = Brushes.Transparent,
            BorderBrush = defaultB,
            BorderThickness = new Thickness(1),
            Foreground = textP,
        };
        advLogBtn.Click += (s, e) =>
        {
            CloseAdvancedShell();
            OnMenuOpenLogClicked(s, e);
        };

        var advLeakBtn = new Avalonia.Controls.Button
        {
            Content = Localization.AndroidToolsCheckLeak,
            FontSize = 10,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(10, 6),
            CornerRadius = new CornerRadius(radiusSm),
            Background = Brushes.Transparent,
            BorderBrush = defaultB,
            BorderThickness = new Thickness(1),
            Foreground = textP,
        };
        advLeakBtn.Click += (s, e) =>
        {
            CloseAdvancedShell();
            OnMenuCheckLeaksClicked(s, e);
        };

        // "Hosts/Filters not applicable" footer card — explains why those
        // sections from desktop's sidebar aren't present here.
        var notApplicable = new Border
        {
            Padding = new Thickness(10, 8),
            CornerRadius = new CornerRadius(radiusSm),
            Background = sunken,
            BorderBrush = subtle,
            BorderThickness = new Thickness(1),
            Child = new TextBlock
            {
                Text = Localization.AndroidZapretSectionNotApplicable,
                FontSize = 10,
                Opacity = 0.7,
                Foreground = textS,
                TextWrapping = TextWrapping.Wrap,
            },
        };

        var advStack = new StackPanel
        {
            Spacing = 6,
            Children = { advTitle, advDesc, advHealthBtn, advLogBtn, advLeakBtn, notApplicable },
        };
        _dpiBodyAdvanced = new ScrollViewer
        {
            Content = new Border
            {
                Padding = new Thickness(12, 10, 12, 12),
                Child = advStack,
            },
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = bg,
            IsVisible = false,
        };

        var bodyArea = new Grid
        {
            Children = { _dpiBodyStatus, _dpiBodyStrategy, _dpiBodyAdvanced },
        };

        // ── Footer (Apply bar) ───────────────────────────────────────────
        // Mirrors DpiBypassPage footer (lines 379-407): SurfaceSunkenBrush
        // bg, 14,7 padding, divider above. Status indicator on left
        // (color dot + label), toggle button on right (compact accent-blue).
        _dpiFooterStatusDot = new Ellipse
        {
            Width = 8,
            Height = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _dpiFooterStatusText = new TextBlock
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
            Children = { _dpiFooterStatusDot, _dpiFooterStatusText },
        };

        _dpiFooterToggleBtn = new Avalonia.Controls.Button
        {
            FontSize = 10,
            FontWeight = FontWeight.SemiBold,
            Padding = new Thickness(10, 5),
            CornerRadius = new CornerRadius(radiusSm),
            Background = accentSolid,
            Foreground = accentOnSolid,
            BorderThickness = new Thickness(0),
            Content = AndroidStorage.GetDpiBypassMode() == "off"
                ? Localization.AndroidDpiBypassFooterToggleOn
                : Localization.AndroidDpiBypassFooterToggleOff,
        };
        _dpiFooterToggleBtn.Click += OnDpiFooterToggleClicked;

        var footerGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            ColumnSpacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(footerStatusRow, 0);
        Grid.SetColumn(_dpiFooterToggleBtn, 1);
        footerGrid.Children.Add(footerStatusRow);
        footerGrid.Children.Add(_dpiFooterToggleBtn);

        var footer = new Border
        {
            Padding = new Thickness(14, 7, 14, 8),
            BorderThickness = new Thickness(0, 1, 0, 0),
            BorderBrush = defaultB,
            Background = sunken,
            Child = footerGrid,
        };

        // ── Compose: tabs + body + footer ────────────────────────────────
        var dock = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(tabRow, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        dock.Children.Add(tabRow);
        dock.Children.Add(footer);
        dock.Children.Add(bodyArea);

        UpdateDpiFooterState();

        return new Border
        {
            Background = bg,
            Child = dock,
        };
    }

    private void SelectDpiTab(int index)
    {
        _dpiSelectedTab = index;
        if (_dpiBodyStatus   is not null) _dpiBodyStatus.IsVisible   = index == 0;
        if (_dpiBodyStrategy is not null) _dpiBodyStrategy.IsVisible = index == 1;
        if (_dpiBodyAdvanced is not null) _dpiBodyAdvanced.IsVisible = index == 2;
        // POL-1: re-painted via StyleAdvShellTab (matches sibling sub-tab
        // strips). Pre-POL-1 used StyleSegmentButton — kebab pill shape.
        if (_dpiTabStatus   is not null) StyleAdvShellTab(_dpiTabStatus,   index == 0);
        if (_dpiTabStrategy is not null) StyleAdvShellTab(_dpiTabStrategy, index == 1);
        if (_dpiTabAdvanced is not null) StyleAdvShellTab(_dpiTabAdvanced, index == 2);
    }

    /// <summary>
    /// AND-MIGRATE-OVERLAYS (2026-05-09): the standalone DPI Bypass
    /// overlay close handler is gone — the Advanced shell owns the close
    /// affordance now. Tab activation calls <see cref="ReseedDpiBypassTabState"/>
    /// to refresh the visible widgets from the persisted mode.
    /// </summary>
    private void ReseedDpiBypassTabState()
    {
        var mode = AndroidStorage.GetDpiBypassMode();
        if (_dpiStrategyComboBox is not null)
        {
            _dpiStrategyComboBox.SelectedIndex = mode switch
            {
                "standard"   => 1,
                "aggressive" => 2,
                _            => 0,
            };
        }
        if (_dpiStatusBarText is not null)
            _dpiStatusBarText.Text = ZapretStatusLabelForCurrentMode();
        if (_dpiStatusBarDot is not null)
            _dpiStatusBarDot.Fill = GetBrush(mode != "off"
                ? "SuccessSolidBrush" : "TextMutedBrush");
        UpdateDpiFooterState();
    }

    /// <summary>
    /// Strategy ComboBox writer. Persists the new mode + refreshes the
    /// footer indicator + the in-Settings ComboBox (so toggling here also
    /// surfaces correctly when the user reopens Settings → Routing).
    /// </summary>
    private void OnDpiStrategyChanged(object? sender, Avalonia.Controls.SelectionChangedEventArgs e)
    {
        if (_dpiStrategyComboBox is null) return;
        var value = _dpiStrategyComboBox.SelectedIndex switch
        {
            1 => "standard",
            2 => "aggressive",
            _ => "off",
        };
        AndroidStorage.SetDpiBypassMode(value);

        // Mirror the change into the Settings overlay's ComboBox so the
        // two surfaces stay in sync when both are open in the user's
        // recent-tasks stack.
        if (_settingsDpiBypassMode is not null)
        {
            _settingsLoading = true;
            try
            {
                _settingsDpiBypassMode.SelectedIndex = _dpiStrategyComboBox.SelectedIndex;
            }
            finally { _settingsLoading = false; }
        }

        UpdateZapretChipFromState();
        UpdateDpiFooterState();
    }

    /// <summary>
    /// Footer toggle: flip between "off" and the last-non-off mode (default
    /// Standard if the user has never picked anything else). Mirrors
    /// desktop's <c>ToggleZapretCommand</c> behaviour — single tap toggles
    /// without entering the strategy picker.
    /// </summary>
    private void OnDpiFooterToggleClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var current = AndroidStorage.GetDpiBypassMode();
        var next = current == "off" ? "standard" : "off";
        AndroidStorage.SetDpiBypassMode(next);

        if (_dpiStrategyComboBox is not null)
        {
            _dpiStrategyComboBox.SelectedIndex = next switch
            {
                "standard"   => 1,
                "aggressive" => 2,
                _            => 0,
            };
        }
        if (_settingsDpiBypassMode is not null)
        {
            _settingsLoading = true;
            try { _settingsDpiBypassMode.SelectedIndex = _dpiStrategyComboBox?.SelectedIndex ?? 0; }
            finally { _settingsLoading = false; }
        }

        UpdateZapretChipFromState();
        UpdateDpiFooterState();
    }

    private void UpdateDpiFooterState()
    {
        var mode = AndroidStorage.GetDpiBypassMode();
        var enabled = mode != "off";
        if (_dpiFooterStatusText is not null)
            _dpiFooterStatusText.Text = ZapretStatusLabelForCurrentMode();
        if (_dpiFooterStatusDot is not null)
            _dpiFooterStatusDot.Fill = GetBrush(enabled
                ? "SuccessSolidBrush" : "TextMutedBrush");
        if (_dpiFooterToggleBtn is not null)
            _dpiFooterToggleBtn.Content = enabled
                ? Localization.AndroidDpiBypassFooterToggleOff
                : Localization.AndroidDpiBypassFooterToggleOn;
    }

    /// <summary>
    /// Helper to detect current locale without exposing the private Ru
    /// flag from <c>Strings</c>. We just probe a known RU-vs-EN string —
    /// the result is true iff the Russian variant is active.
    /// </summary>
    private static bool IsRu()
    {
        // ZapretSecStrategy → "Стратегия" / "Strategy" — the cheapest
        // reliable signal we already export.
        return Localization.ZapretSecStrategy.StartsWith("Стр", StringComparison.Ordinal);
    }
}
