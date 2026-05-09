using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

namespace VPNRouter.Android;

/// <summary>
/// AND-MIGRATE-OVERLAYS (2026-05-09) — Advanced shell. Single fullscreen
/// overlay that hosts the previously-separate per-feature surfaces
/// (Servers / Subscriptions / Apps / Network / DPI bypass / Telegram /
/// Public configs) as a tab strip with one content host.
///
/// <para>Replaces the kebab → feature-page navigation pattern that
/// diverged from desktop. After this lands, the user opens
/// «Расширенные настройки ▸» from the Simple page → tab strip → tab →
/// feature renders inline. Kebab keeps only utility items
/// (Appearance / Language / Routing profiles / Diagnostics actions /
/// Troubleshooting / About).</para>
///
/// <para>Tab content is built lazily on first activation. The shell
/// caches each Control so subsequent activations of the same tab don't
/// rebuild the widget tree (preserves scroll position + ListBox
/// virtualization state). Switching tabs flips which child of the
/// content-host Grid has IsVisible=true.</para>
/// </summary>
public partial class AndroidApp
{
    /// <summary>
    /// Tab identifier — one entry per top-level Advanced surface. Order
    /// mirrors desktop MainWindow.axaml left-nav (Servers → Subscriptions →
    /// Apps → Network → DPI bypass → Telegram → Public configs).
    /// </summary>
    private enum AdvancedTab
    {
        Servers,
        Subscriptions,
        Apps,
        Network,
        DpiBypass,
        Telegram,
        FreeConfigs,
    }

    private Border? _advShellOverlay;
    private TextBlock? _advShellTitle;
    private Grid? _advShellContentHost;
    private AdvancedTab _advShellSelectedTab = AdvancedTab.Servers;
    private readonly Dictionary<AdvancedTab, Avalonia.Controls.Button> _advShellTabButtons = new();
    private readonly Dictionary<AdvancedTab, Control> _advShellTabContent = new();

    /// <summary>
    /// Build the fullscreen Advanced shell overlay. Called once at
    /// startup from <c>BuildPage</c>; layered above the main scroller in
    /// the same way the per-feature overlays used to be.
    /// </summary>
    private Border BuildAdvancedShellOverlay()
    {
        var bg     = GetBrush("SurfaceAppBrush");
        var raised = GetBrush("SurfaceRaisedBrush");
        var subtle = GetBrush("BorderSubtleBrush");
        var textP  = GetBrush("TextPrimaryBrush");
        var textS  = GetBrush("TextSecondaryBrush");

        // ── Title bar ───────────────────────────────────────────────────
        _advShellTitle = new TextBlock
        {
            Text = Localization.SmpAdvCardTitle,
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            Foreground = textP,
            VerticalAlignment = VerticalAlignment.Center,
        };
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
            Foreground = textS,
        };
        closeBtn.Click += (_, _) => CloseAdvancedShell();

        var titleBarGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(8, 4, 4, 4),
        };
        Grid.SetColumn(_advShellTitle, 0);
        Grid.SetColumn(closeBtn, 1);
        titleBarGrid.Children.Add(_advShellTitle);
        titleBarGrid.Children.Add(closeBtn);

        var titleBarBorder = new Border
        {
            Background = raised,
            BorderBrush = subtle,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(8, 4),
            Child = titleBarGrid,
        };

        // ── Tab strip — horizontally-scrollable so 7 tabs fit on narrow
        //    phones without forced text-truncation. Each tab is a chip
        //    styled by StyleAdvShellTab (active/inactive paint). Click
        //    selects + builds the tab content lazily.
        var tabPanel = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 4,
            Margin = new Thickness(8, 6, 8, 6),
        };
        foreach (AdvancedTab tab in System.Enum.GetValues(typeof(AdvancedTab)))
        {
            var btn = MakeAdvShellTabButton(tab);
            _advShellTabButtons[tab] = btn;
            tabPanel.Children.Add(btn);
        }
        var tabScroller = new ScrollViewer
        {
            Content = tabPanel,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Background = raised,
        };
        var tabStripBorder = new Border
        {
            Background = raised,
            BorderBrush = subtle,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = tabScroller,
        };

        // ── Content host — Grid with one child per tab. Built lazily as
        //    tabs are first activated; cached in _advShellTabContent.
        _advShellContentHost = new Grid
        {
            Background = bg,
        };

        var dock = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(titleBarBorder, Dock.Top);
        DockPanel.SetDock(tabStripBorder, Dock.Top);
        dock.Children.Add(titleBarBorder);
        dock.Children.Add(tabStripBorder);
        dock.Children.Add(_advShellContentHost);

        return new Border
        {
            Background = bg,
            IsVisible = false,
            Child = dock,
        };
    }

    /// <summary>
    /// Open the shell on the requested tab. If the shell is already
    /// visible just switches the tab — handles deeplinks (kebab Settings,
    /// "Choose apps" button, sub-card drill-in) and the Simple-page
    /// «Advanced settings ▸» CTA equally.
    /// </summary>
    private void OpenAdvancedShell(AdvancedTab tab)
    {
        if (_advShellOverlay is null) return;
        SelectAdvancedTab(tab);
        _advShellOverlay.IsVisible = true;
    }

    /// <summary>
    /// Close the shell + run any tab-specific teardown (cancel in-flight
    /// background work, refresh the Simple-page reflection of subs/server
    /// state, etc.). Public so partials like FreeConfigs can request a
    /// close after applying a chosen config.
    /// </summary>
    private void CloseAdvancedShell()
    {
        if (_advShellOverlay is not null)
            _advShellOverlay.IsVisible = false;

        // Best-effort teardown for tabs that ran background work — same
        // contract the old per-feature close handlers used to honour.
        StopServersTabBackgroundWork();
        StopFreeConfigsBackgroundWork();

        // Refresh SimplePage's reflection of any state the user may have
        // mutated inside the shell (sub list, server selection, config
        // summary). Mirrors the old OnSubsCloseClicked /
        // CloseServerListOverlay tail.
        ReloadServerList();
        UpdateConfigSummary();
    }

    /// <summary>
    /// Switch to a tab. Builds the content lazily on first activation so
    /// startup stays cheap; subsequent activations just flip IsVisible.
    /// Re-seeds the activated tab's state from <see cref="AndroidStorage"/>
    /// so persistent values stay fresh across navigation.
    /// </summary>
    private void SelectAdvancedTab(AdvancedTab tab)
    {
        if (_advShellContentHost is null) return;
        _advShellSelectedTab = tab;
        EnsureTabContentBuilt(tab);

        // Toggle visibility on each tab's cached Control. New child added
        // to the content host on first show.
        foreach (var kv in _advShellTabContent)
            kv.Value.IsVisible = kv.Key == tab;

        // Update tab strip styling so the active chip stands out.
        foreach (var kv in _advShellTabButtons)
            StyleAdvShellTab(kv.Value, kv.Key == tab);

        // Per-tab re-seed. Each helper lives in its own partial.
        switch (tab)
        {
            case AdvancedTab.Servers:       ReseedServersTabState();      break;
            case AdvancedTab.Subscriptions: ReseedSubscribeTabState();    break;
            case AdvancedTab.Apps:          ReseedAppPickerTabState();    break;
            case AdvancedTab.Network:       ReseedNetworkTabState();      break;
            case AdvancedTab.DpiBypass:     ReseedDpiBypassTabState();    break;
            case AdvancedTab.FreeConfigs:   ReseedFreeConfigsTabState();  break;
            case AdvancedTab.Telegram:      /* static body — no re-seed */ break;
        }
    }

    private void EnsureTabContentBuilt(AdvancedTab tab)
    {
        if (_advShellContentHost is null) return;
        if (_advShellTabContent.ContainsKey(tab)) return;

        Control content = tab switch
        {
            AdvancedTab.Servers       => BuildServersTabContent(),
            AdvancedTab.Subscriptions => BuildSubscribeTabContent(),
            AdvancedTab.Apps          => BuildAppPickerTabContent(),
            AdvancedTab.Network       => BuildNetworkTabContent(),
            AdvancedTab.DpiBypass     => BuildDpiBypassTabContent(),
            AdvancedTab.Telegram      => BuildTelegramTabContent(),
            AdvancedTab.FreeConfigs   => BuildFreeConfigsTabContent(),
            _                         => new TextBlock { Text = "?" },
        };
        content.IsVisible = false;
        _advShellTabContent[tab] = content;
        _advShellContentHost.Children.Add(content);
    }

    private Avalonia.Controls.Button MakeAdvShellTabButton(AdvancedTab tab)
    {
        var btn = new Avalonia.Controls.Button
        {
            Content = AdvancedTabLabel(tab),
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            Padding = new Thickness(12, 6),
            CornerRadius = new CornerRadius(GetRadius("RadiusSm")),
            BorderThickness = new Thickness(1),
        };
        btn.Click += (_, _) => SelectAdvancedTab(tab);
        StyleAdvShellTab(btn, tab == _advShellSelectedTab);
        return btn;
    }

    /// <summary>Active = accent-tinted chip, inactive = neutral sunken pill.</summary>
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

    /// <summary>Localized tab label. Reuses existing strings where possible.</summary>
    private static string AdvancedTabLabel(AdvancedTab tab)
    {
        return tab switch
        {
            AdvancedTab.Servers       => Localization.TabServers,
            AdvancedTab.Subscriptions => Localization.SubscriptionsSection,
            AdvancedTab.Apps          => Localization.TabApps,
            AdvancedTab.Network       => Localization.TabNetwork,
            AdvancedTab.DpiBypass     => Localization.DpiBypassOverlayTitle,
            AdvancedTab.Telegram      => Localization.TabTelegram,
            AdvancedTab.FreeConfigs   => Localization.FcOverlayTitle,
            _                         => string.Empty,
        };
    }

    /// <summary>
    /// Re-paint shell strings on language toggle. Called from
    /// <see cref="ToggleLanguageAndRefresh"/>.
    /// </summary>
    private void RefreshAdvancedShellStrings()
    {
        if (_advShellTitle is not null)
            _advShellTitle.Text = Localization.SmpAdvCardTitle;
        foreach (var kv in _advShellTabButtons)
            kv.Value.Content = AdvancedTabLabel(kv.Key);
    }
}
