using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using System;
using System.Collections.Generic;
using Orientation = Avalonia.Layout.Orientation;

namespace VPNRouter.Android;

/// <summary>
/// AND-ADV-SHELL (2026-05-09) — tab-based Advanced overlay shell.
/// Mirrors desktop MainWindow.axaml's tab strip (Servers / Subscribe /
/// Network / Apps / Tools / Free Configs) so the mobile UI navigates
/// Advanced features through tabs instead of the kebab menu. This chip
/// builds the empty shell (header + tab strip + content host with stub
/// panes); the next chip (AND-ADV-MIGRATE) drops real content into each
/// pane and trims the kebab.
///
/// <para>Layout per prompt acceptance:
/// <list type="bullet">
///   <item>Header 56 dp: × close (left) + "Advanced" title</item>
///   <item>Tab strip 48 dp: 7 tabs in a horizontal ScrollViewer
///     (Hidden scrollbars), MinWidth 96 + Padding 16,12 per tab.
///     Active tab gets AccentBgSubtle bg + 2 dp AccentSolid underline.</item>
///   <item>Content host swaps between 7 stub panes; the active pane
///     is laid out in a ScrollViewer.</item>
///   <item>Last-active tab persists in SharedPreferences via
///     <see cref="AndroidStorage.GetAdvancedActiveTab"/> /
///     <see cref="AndroidStorage.SetAdvancedActiveTab"/>.</item>
/// </list></para>
///
/// <para>Desktop has 6 tabs (Tools hosts DPI bypass + Telegram as
/// sub-pages). Mobile splits Tools into two top-level tabs because the
/// Android overlay surface doesn't have horizontal sub-navigation room
/// on top of the main tab strip. Result is 7 tabs:
/// Servers / Subscribe / Apps / Network / DPI bypass / Telegram /
/// Public configs.</para>
/// </summary>
public partial class AndroidApp
{
    // Root overlay + per-tab content host. Sits in the Grid sibling list
    // alongside the other fullscreen overlays (subs / settings / freeConfigs /
    // tools / dpiBypass / serverList / configShare / profiles / appPicker /
    // logViewer). Same z-order treatment as those overlays.
    private Border? _advancedOverlay;
    private Border? _advTabContentHost;
    private TextBlock? _advHeaderTitle;
    private ScrollViewer? _advTabStripScroller;

    // Tab buttons + their stub panes. Index aligned: button[i] toggles
    // pane[i] visible. Tag on each button stashes (TextBlock, Border)
    // for the title text + 2 dp underline so RefreshAdvancedTabStrip
    // can flip styling without an extra dictionary lookup.
    private readonly List<Avalonia.Controls.Button> _advTabButtons = new();
    private readonly List<Border> _advTabPanes = new();
    private int _advActiveTabIndex;

    // 7-tab indices. Keep aliased constants so the next chip's content
    // wiring + the storage migration both reference the same numbers.
    private const int AdvancedTabCount        = 7;
    private const int AdvancedTabServers      = 0;
    private const int AdvancedTabSubscribe    = 1;
    private const int AdvancedTabApps         = 2;
    private const int AdvancedTabNetwork      = 3;
    private const int AdvancedTabDpiBypass    = 4;
    private const int AdvancedTabTelegram     = 5;
    private const int AdvancedTabFreeConfigs  = 6;

    /// <summary>
    /// Build the Advanced overlay Border. Header (× close + title) +
    /// tab strip (horizontally scrollable, 7 tabs) + content host
    /// with one stub pane per tab. Returns the Border with
    /// <c>IsVisible=false</c>; callers flip it in
    /// <see cref="OpenAdvancedOverlay"/>.
    /// </summary>
    private Border BuildAdvancedOverlay()
    {
        var header = BuildAdvancedHeader();
        var tabStrip = BuildAdvancedTabStrip();
        var contentHost = BuildAdvancedContentHost();

        var dock = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(tabStrip, Dock.Top);
        dock.Children.Add(header);
        dock.Children.Add(tabStrip);
        dock.Children.Add(contentHost);

        var overlay = new Border
        {
            IsVisible = false,
            Child = dock,
        };
        overlay.BindToken(Border.BackgroundProperty, "SurfaceAppBrush");
        return overlay;
    }

    private Border BuildAdvancedHeader()
    {
        var closeBtn = new Avalonia.Controls.Button
        {
            Content = "✕",
            FontSize = 16,
            Width = 36,
            Height = 36,
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        closeBtn.BindToken(Avalonia.Controls.Button.ForegroundProperty, "TextSecondaryBrush");
        closeBtn.Click += OnAdvancedCloseClicked;

        _advHeaderTitle = new TextBlock
        {
            // Reuse the existing "Advanced settings" / "Расширенные настройки"
            // string so EN+RU+visible-on-Simple-card stay in sync.
            Text = Localization.SmpAdvCardTitle,
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(8, 0, 0, 0),
        };
        _advHeaderTitle.BindToken(TextBlock.ForegroundProperty, "TextPrimaryBrush");

        var headerGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            Margin = new Thickness(4, 0),
            Height = 56,
        };
        Grid.SetColumn(closeBtn, 0);
        Grid.SetColumn(_advHeaderTitle, 1);
        headerGrid.Children.Add(closeBtn);
        headerGrid.Children.Add(_advHeaderTitle);

        var headerBorder = new Border
        {
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(4, 0),
            Child = headerGrid,
        };
        headerBorder.BindToken(Border.BackgroundProperty, "SurfaceRaisedBrush");
        headerBorder.BindToken(Border.BorderBrushProperty, "BorderSubtleBrush");
        return headerBorder;
    }

    private Border BuildAdvancedTabStrip()
    {
        var labels = GetAdvancedTabLabels();

        var tabRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 0,
        };

        for (int i = 0; i < AdvancedTabCount; i++)
        {
            int captureIndex = i;
            var tab = MakeAdvancedTabButton(labels[i]);
            tab.Click += (_, _) => SwitchAdvancedTab(captureIndex);
            _advTabButtons.Add(tab);
            tabRow.Children.Add(tab);
        }

        _advTabStripScroller = new ScrollViewer
        {
            Content = tabRow,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Height = 48,
            Padding = new Thickness(0),
        };

        var stripBorder = new Border
        {
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = _advTabStripScroller,
        };
        stripBorder.BindToken(Border.BackgroundProperty, "SurfaceBaseBrush");
        stripBorder.BindToken(Border.BorderBrushProperty, "BorderSubtleBrush");
        return stripBorder;
    }

    private Avalonia.Controls.Button MakeAdvancedTabButton(string label)
    {
        var text = new TextBlock
        {
            Text = label,
            FontSize = 13,
            FontWeight = FontWeight.Normal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        text.BindToken(TextBlock.ForegroundProperty, "TextMutedBrush");

        // 2 dp accent underline. Visible only when the tab is active —
        // Material standard. RefreshAdvancedTabStrip toggles IsVisible.
        var underline = new Border
        {
            Height = 2,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Bottom,
            IsVisible = false,
            Margin = new Thickness(0, 0, 0, 0),
        };
        underline.BindToken(Border.BackgroundProperty, "AccentSolidBrush");

        var grid = new Grid();
        grid.Children.Add(text);
        grid.Children.Add(underline);

        var btn = new Avalonia.Controls.Button
        {
            Content = grid,
            MinWidth = 96,
            MinHeight = 48,
            Padding = new Thickness(16, 12),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            // Stash text + underline so RefreshAdvancedTabStrip can mutate
            // them without re-walking the visual tree.
            Tag = new TabVisuals(text, underline),
        };
        return btn;
    }

    private Border BuildAdvancedContentHost()
    {
        // One stub pane per tab. The next chip (AND-ADV-MIGRATE) replaces
        // each pane's Child with the real per-tab content (Servers list,
        // Subscriptions card list, Apps picker, Settings sections, DPI
        // bypass + Telegram tools, Free Configs catalog).
        for (int i = 0; i < AdvancedTabCount; i++)
        {
            var stub = new TextBlock
            {
                Text = "(content TBD by AND-ADV-MIGRATE chip)",
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(24),
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
            };
            stub.BindToken(TextBlock.ForegroundProperty, "TextMutedBrush");

            var pane = new Border
            {
                Padding = new Thickness(0),
                Child = stub,
                IsVisible = false,
            };
            _advTabPanes.Add(pane);
        }

        var paneGrid = new Grid();
        foreach (var p in _advTabPanes) paneGrid.Children.Add(p);

        var scroller = new ScrollViewer
        {
            Content = paneGrid,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        _advTabContentHost = new Border
        {
            Child = scroller,
        };
        _advTabContentHost.BindToken(Border.BackgroundProperty, "SurfaceAppBrush");
        return _advTabContentHost;
    }

    private static string[] GetAdvancedTabLabels() => new[]
    {
        // Order mirrors desktop MainWindow.axaml ListBox order:
        // Servers · Subscribe · Apps · Network · Tools · FreeConfigs.
        // Tools is split here into DPI bypass + Telegram so each gets a
        // top-level tab (mobile overlay can't host horizontal sub-tabs
        // on top of the main strip without burning vertical room).
        Localization.TabServers,        // "Серверы" / "Servers"
        Localization.ModeSubscribe,     // "Подписка" / "Subscribe"
        Localization.TabApps,           // "Приложения" / "Applications"
        Localization.TabSettings,       // "Настройки" / "Settings"
        Localization.ToolsTabZapret,    // "DPI bypass"
        Localization.TabTelegram,       // "Telegram"
        Localization.TabFreeConfigs,    // "Публичные" / "Public"
    };

    /// <summary>
    /// Programmatic tab switch. Persists the new index so reopen lands
    /// on the same tab (acceptance criterion). Out-of-range indices are
    /// silently ignored — the storage path also clamps when reading.
    /// </summary>
    private void SwitchAdvancedTab(int index)
    {
        if (index < 0 || index >= AdvancedTabCount) return;
        _advActiveTabIndex = index;
        AndroidStorage.SetAdvancedActiveTab(index);
        RefreshAdvancedTabStrip();
    }

    /// <summary>
    /// Apply active styling to the active tab + show its pane. Called
    /// after every tab switch and on overlay open (so a fresh open lands
    /// on the persisted tab visually). Auto-scrolls the strip so the
    /// active tab is visible — relevant when the user fled past it on
    /// the previous open or the persisted index is far-right.
    /// </summary>
    private void RefreshAdvancedTabStrip()
    {
        for (int i = 0; i < _advTabButtons.Count; i++)
        {
            if (_advTabButtons[i].Tag is not TabVisuals visuals) continue;
            bool active = i == _advActiveTabIndex;

            visuals.Text.FontWeight = active ? FontWeight.Medium : FontWeight.Normal;
            visuals.Text.Bind(TextBlock.ForegroundProperty,
                new DynamicResourceExtension(active ? "AccentFgBrush" : "TextMutedBrush"));

            if (active)
            {
                _advTabButtons[i].Bind(Avalonia.Controls.Button.BackgroundProperty,
                    new DynamicResourceExtension("AccentBgSubtleBrush"));
            }
            else
            {
                _advTabButtons[i].Background = Brushes.Transparent;
            }

            visuals.Underline.IsVisible = active;
            if (i < _advTabPanes.Count) _advTabPanes[i].IsVisible = active;
        }

        // Bring the active tab into view if the strip is wider than the
        // viewport. BringIntoView uses the visual tree to compute scroll
        // offset; safe to call on a non-displayed overlay (the layout
        // pass runs lazily but the eventual scroll lands correctly).
        if (_advActiveTabIndex >= 0 &&
            _advActiveTabIndex < _advTabButtons.Count &&
            _advTabStripScroller is not null)
        {
            _advTabButtons[_advActiveTabIndex].BringIntoView();
        }
    }

    /// <summary>
    /// Open the overlay. Restores the persisted active-tab index,
    /// applies styling, closes the kebab popup if it's open (overlays
    /// always layer above and the popup looks orphaned otherwise), and
    /// flips IsVisible. Wired to the "Расширенные настройки ▸" /
    /// "Advanced settings ▸" card on Simple page; see
    /// <see cref="OnAdvCardClicked"/>.
    /// </summary>
    private void OpenAdvancedOverlay()
    {
        if (_advancedOverlay is null) return;

        var stored = AndroidStorage.GetAdvancedActiveTab();
        _advActiveTabIndex = (stored >= 0 && stored < AdvancedTabCount) ? stored : AdvancedTabServers;
        RefreshAdvancedTabStrip();

        if (_kebabPopup is not null) _kebabPopup.IsOpen = false;
        _advancedOverlay.IsVisible = true;
    }

    private void OnAdvancedCloseClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_advancedOverlay is not null) _advancedOverlay.IsVisible = false;
    }

    /// <summary>
    /// Pair of visual elements stashed on each tab Button's Tag. Lets
    /// <see cref="RefreshAdvancedTabStrip"/> flip the title weight /
    /// foreground + underline visibility without re-walking the Grid
    /// children every refresh.
    /// </summary>
    private sealed record TabVisuals(TextBlock Text, Border Underline);
}
