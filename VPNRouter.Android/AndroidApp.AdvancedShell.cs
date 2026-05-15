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

/// <summary>
/// AND-MIGRATE-OVERLAYS (2026-05-09) — Advanced shell. Single fullscreen
/// overlay that hosts the previously-separate per-feature surfaces
/// (Servers / Subscribe / Settings / Applications / Tools / Public) as a
/// tab strip with one content host.
///
/// <para>AND-ADV-CHROME (2026-05-10) rebuilt the shell chrome to mirror
/// desktop v2.32.0 stable (commit <c>7d9707b</c>): the previous title bar
/// "Advanced settings + ×" is replaced by a brand row (mascot + "Virtual
/// Penguin Network" + VPN/Zapret/TG chips) with a "+ Simple" link button +
/// ⋮ kebab in the top-right; a persistent footer (status dot + status text
/// on the left, accent Start VPN / Stop VPN button on the right) is always
/// visible regardless of which tab is active. Tab labels were renamed to
/// match desktop's six-tab strip (Servers / Subscribe / Settings /
/// Applications / Tools / Public) — Tools merges the previous DPI bypass +
/// Telegram top-level tabs.</para>
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
    /// Tab identifier — one entry per top-level Advanced surface. Order +
    /// names mirror desktop v2.32.0 MainWindow.axaml's ListBoxItem strip:
    /// Servers / Subscribe / Settings / Applications / Tools / Public.
    /// AND-ADV-CHROME renamed pre-2026-05-10 names (Subscriptions / Apps /
    /// Network / DpiBypass / Telegram / FreeConfigs) and merged DPI bypass
    /// + Telegram into a single Tools tab. AndroidStorage migrates legacy
    /// stored values on read so existing users don't lose their last-active
    /// tab.
    /// </summary>
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

    // Header chrome (AND-ADV-CHROME 2026-05-10) — brand row + Simple
    // toggle + kebab. Mirrors desktop MainWindow.axaml lines 281-580.
    private TextBlock? _advBrandTitle;
    private Image? _advMascotImage;
    private Avalonia.Controls.Button? _advSimpleToggleBtn;
    private Avalonia.Controls.Button? _advKebabMenuBtn;
    // Bug #3 fix (2026-05-11) — Advanced header chips now state-bound.
    // Pre-fix they were local-scoped TextBlocks in BuildAdvancedHeader,
    // so SetVpnChipState / SetZapretChipState only ever updated the
    // Simple page chips. With these fields wired and the brushes
    // re-bound on every state flip we get live chip parity between the
    // two surfaces. (MakeChip returns TextBlock — see SimplePage builder.)
    private TextBlock? _advVpnChip;
    private TextBlock? _advZapretChip;
    private TextBlock? _advTgChip;

    // Persistent footer chrome (AND-ADV-CHROME 2026-05-10) — status dot +
    // text on the left, accent Start VPN button on the right. Mirrors
    // desktop MainWindow.axaml lines 686-720.
    private Ellipse? _advFooterStatusDot;
    private TextBlock? _advFooterStatusText;
    private Avalonia.Controls.Button? _advFooterConnectBtn;
    // Per-tab action row slot (T6). Each tab's Build*TabContent builder
    // can drop a Control here by exposing _advFooterActions = ... via the
    // shell's SetFooterActions helper. Phase A leaves it empty; Phases
    // B-E populate it from their own tab content.
    private Border? _advFooterActionsHost;

    /// <summary>
    /// Build the fullscreen Advanced shell overlay. Called once at
    /// startup from <c>BuildPage</c>; layered above the main scroller in
    /// the same way the per-feature overlays used to be.
    /// </summary>
    private Border BuildAdvancedShellOverlay()
    {
        var bg       = GetBrush("SurfaceAppBrush");
        var raised   = GetBrush("SurfaceRaisedBrush");
        var subtle   = GetBrush("BorderSubtleBrush");
        var defaultB = GetBrush("BorderDefaultBrush");

        // ── Header (brand row + Simple toggle + kebab) ──────────────────
        // T1-T3 — replaces the v2.32.0-pre "Advanced settings + ×" title
        // bar. Two reasons for a fresh build (rather than reusing the
        // Simple page's _kebabMenuButton / _kebabPopup): (1) the Simple
        // page's _kebabPopup belongs to a different visual subtree and
        // re-targeting PlacementTarget mid-flight is brittle in Avalonia;
        // (2) the Advanced shell's brand row needs a "+ Simple" toggle
        // that the Simple page deliberately doesn't carry.
        var headerBorder = BuildAdvancedHeader();

        // ── Tab strip — POL-2-TABS (2026-05-10) replaced horizontal
        //    ScrollViewer with UniformGrid so all 6 tabs distribute evenly
        //    across the viewport. No swipe/scroll needed — every tab is
        //    visible + tappable in one row at any phone width. Active
        //    chip's visual distinction comes from StyleAdvShellTab
        //    (accent background/foreground), not from being wider than
        //    its siblings. Long labels (e.g. "Applications" / "Инструменты")
        //    fall back to character ellipsis when the per-tab cell is
        //    narrower than the rendered string.
        // POL-1-CARDS overlay (2026-05-10): the wrapping Border uses
        // Background=Transparent + BorderBrush=BorderDefault (was
        // SurfaceRaised + BorderSubtle) so the strip reads like the
        // desktop ListBox sub-tab pattern instead of a coloured banner.
        // The UniformGrid keeps POL-2's compress-to-fit structure.
        // 2026-05-15 (Bug-AND-003, brat live-test): hide Tools tab on
        // Android. Zapret + Telegram-proxy are Windows-only features —
        // showing the tab as «coming soon» (current state) confuses
        // users who see «Инструменты» offer no actual function. Same
        // rationale as Mac/Linux where Tools isn't surfaced. Tab strip
        // contracts from 6 → 5 columns, giving each remaining label
        // ~20% more horizontal room (helps Bug-AND-001 truncation too).
        // The AdvancedTab.Tools enum value is kept so any existing code
        // path that still references it compiles, but the tab is never
        // rendered or activated.
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
        var tabStripBorder = new Border
        {
            Background = Brushes.Transparent,
            BorderBrush = defaultB,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = tabPanel,
        };

        // ── Content host — Grid with one child per tab. Built lazily as
        //    tabs are first activated; cached in _advShellTabContent.
        _advShellContentHost = new Grid
        {
            Background = bg,
        };

        // ── Persistent footer — T5/T6. Always visible. Phase A provides
        //    the chrome (status + Start VPN button) plus an empty action-
        //    row slot above; Phases B-E may populate the action row per
        //    tab as needed.
        var footerBorder = BuildAdvancedFooter();

        var dock = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(headerBorder, Dock.Top);
        DockPanel.SetDock(tabStripBorder, Dock.Top);
        DockPanel.SetDock(footerBorder, Dock.Bottom);
        dock.Children.Add(headerBorder);
        dock.Children.Add(tabStripBorder);
        dock.Children.Add(footerBorder);
        dock.Children.Add(_advShellContentHost);

        // Seed footer state from the current connection state so the
        // dot+text+button reflect reality on first overlay activation
        // (rather than locking into the disconnected default until the
        // next IntentChanged event fires).
        ApplyAdvancedFooterConnectionState(MainActivity.IntendedConnected);

        return new Border
        {
            Background = bg,
            IsVisible = false,
            Child = dock,
        };
    }

    /// <summary>
    /// Build the Advanced shell's top header — mirrors desktop v2.32.0
    /// SimplePage brand row (mascot + brand title + VPN/Zapret/TG chips)
    /// plus a "+ Simple" toggle and ⋮ kebab in the top-right. The chips
    /// reuse the Simple-page chip fields (<c>_vpnChip</c>, <c>_zapretChip</c>,
    /// <c>_tgChip</c>) — but those are already attached to the Simple page,
    /// so the Advanced header builds its own visual copies that aren't
    /// state-bound. The chip state on the Advanced header therefore
    /// reflects only the connection state at the time the overlay was
    /// built; full chip-state parity is deferred to a follow-up chip if
    /// the user requests live chip updates inside Advanced.
    /// </summary>
    private Border BuildAdvancedHeader()
    {
        var raised   = GetBrush("SurfaceRaisedBrush");
        var subtle   = GetBrush("BorderSubtleBrush");
        var defaultB = GetBrush("BorderDefaultBrush");

        // Mascot — uses LoadMascot() so the bitmap follows the current
        // theme (light/dark) the same way the Simple page header does.
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

        // Brand title — same 12 / Bold typography as Simple page
        // (matches desktop SimplePage.axaml line 60).
        _advBrandTitle = new TextBlock
        {
            Text = Localization.BrandTitle,
            FontSize = 12,
            FontWeight = FontWeight.Bold,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        _advBrandTitle.BindToken(TextBlock.ForegroundProperty, "TextPrimaryBrush");

        // Visual copies of VPN/Zapret/TG chips. These are static copies —
        // they show the connection state at the moment the overlay was
        // built. (Bug #3 fix 2026-05-11) chips are now state-bound — see
        // SetVpnChipState / SetZapretChipState which now mirror state onto
        // _advVpnChip / _advZapretChip alongside the Simple-page chips.
        _advVpnChip = MakeChip("VPN", "SurfaceSunkenBrush", "TextMutedBrush");
        _advZapretChip = MakeChip("Zapret", "SurfaceSunkenBrush", "TextMutedBrush");
        _advTgChip = MakeChip("TG", "SurfaceSunkenBrush", "TextMutedBrush");
        // Apply current state immediately so the chip looks right on
        // first overlay open instead of rendering Off until next change.
        // SetVpnChipState's force-rebind branch repaints both Simple and
        // Advanced chips through MirrorAdvancedChipState.
        SetVpnChipState(_vpnChipState, force: true);
        SetZapretChipState(_zapretChipState, force: true);
        // 2026-05-15 (Bug-AND-002 brat live-test, second code path):
        // Advanced shell has its own header chip row separate from
        // Simple-page chips. Same fix as in BuildSimpleMode header:
        // hide Zapret + TG chips on Android (not applicable platform).
        // _advZapretChip / _advTgChip kept allocated for legacy update
        // paths that touch them, just excluded from the visual row.
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

        // "+ Simple" toggle — top-right link-style button. Closes the
        // Advanced overlay (returns to Simple page). Mirrors desktop's
        // ToggleUiModeCommand path (MainWindow.axaml lines 386-412).
        _advSimpleToggleBtn = new Avalonia.Controls.Button
        {
            Content = Localization.AdvSimpleToggle,
            Padding = new Thickness(8, 4),
            MinHeight = 0,
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(GetRadius("RadiusSm")),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
        };
        _advSimpleToggleBtn.BindToken(Avalonia.Controls.Button.ForegroundProperty, "AccentFgBrush");
        _advSimpleToggleBtn.Click += (_, _) => CloseAdvancedShell();

        // ⋮ kebab — opens the same Simple-page kebab popup, just retargeted
        // so the flyout anchors below the Advanced shell's button. The
        // popup is constructed once in BuildSimplePageView (with all menu
        // items + their handlers) — re-using it here avoids duplicating
        // ~150 lines of menu construction and keeps state (theme/lang
        // segments) in one place.
        _advKebabMenuBtn = new Avalonia.Controls.Button
        {
            Content = "⋮",
            FontSize = 22,
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

    /// <summary>
    /// Build the Advanced shell's persistent footer — status dot + text
    /// on the left, primary Start VPN / Stop VPN button on the right.
    /// Mirrors desktop MainWindow.axaml lines 686-720 (the unified footer
    /// introduced in v2.25.0). Above the connect row sits an empty per-
    /// tab action row slot (T6) which Phases B-E populate as needed.
    /// </summary>
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
            FontSize = 11,
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

        // Single-button connect (matches desktop). Content + colors flip
        // via ApplyAdvancedFooterConnectionState — re-uses OnConnectClicked
        // so the engine path is identical to the Simple page CTA.
        _advFooterConnectBtn = new Avalonia.Controls.Button
        {
            Content = Localization.StartVPN,
            FontSize = 11,
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

        // Per-tab action row slot — sits above the connect row, empty by
        // default. Each tab's content builder (Phases B-E) may put a
        // Control here via SetAdvancedFooterActions(...). When empty its
        // Border collapses to zero height so the connect row visually
        // hugs the bottom edge of the overlay.
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

    /// <summary>
    /// Per-tab action row slot setter. Each Build*TabContent builder may
    /// call this to drop a Control above the persistent connect row — the
    /// pattern mirrors desktop's per-tab footer action rows (Test all /
    /// Refresh / + Add etc.) that sit just above the connection footer.
    /// Pass <c>null</c> to clear the slot when switching away from a tab.
    /// </summary>
    internal void SetAdvancedFooterActions(Control? actions)
    {
        if (_advFooterActionsHost is null) return;
        _advFooterActionsHost.Child = actions;
        _advFooterActionsHost.IsVisible = actions is not null;
    }

    /// <summary>
    /// Toggle the kebab popup attached to the Simple-page header so it
    /// appears below the Advanced shell's kebab button instead. Re-targets
    /// <see cref="_kebabPopup"/>'s PlacementTarget on each open so the
    /// flyout tracks whichever button (Simple or Advanced) the user
    /// tapped — mirrors how desktop's MainWindow.axaml uses one Flyout in
    /// both Simple + Advanced modes.
    /// </summary>
    private void OnAdvancedKebabClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_kebabPopup is null) return;
        // Close-and-reopen handles the toggle case (tapping the kebab
        // again to dismiss). Setting PlacementTarget while IsOpen=true
        // would not re-anchor cleanly in Avalonia.
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
        // Bug-AND-006 (2026-05-16) — OnDiagnosticsTick now skips the
        // _advFooterStatusText write while the shell is collapsed.
        // Force a one-shot sync here so the footer label shows the
        // current uptime the moment the shell opens (otherwise it
        // would display whatever was last written before the shell
        // was hidden, until the next second flip).
        if (_advFooterStatusText is not null && _connectionStartedAt is DateTime startUtc)
        {
            var elapsed = DateTime.UtcNow - startUtc;
            _advFooterStatusText.Text = string.Format(
                Localization.SimpleStatusTitleOnWithUptime,
                FormatUptime(elapsed));
        }
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

        // Per-tab action row reset — Phase A starts each tab with no
        // action row. Phases B-E will set this from inside their tab
        // content builders or re-seed helpers.
        SetAdvancedFooterActions(null);

        // Per-tab re-seed. Each helper lives in its own partial.
        // Settings + Public are renamed wrappers around the legacy
        // Network / FreeConfigs builders — they still use the original
        // re-seed helpers until Phase C/E rebuild their content.
        switch (tab)
        {
            case AdvancedTab.Servers:      ReseedServersTabState();     break;
            case AdvancedTab.Subscribe:    ReseedSubscribeTabState();   break;
            case AdvancedTab.Settings:     ReseedNetworkTabState();     break;
            case AdvancedTab.Applications: ReseedAppPickerTabState();   break;
            case AdvancedTab.Tools:        ReseedDpiBypassTabState();   break;
            case AdvancedTab.Public:       ReseedFreeConfigsTabState(); break;
        }

        // Persist the user's last-active tab so reopening the overlay
        // lands on the same surface (AND-ADV-CHROME enum-name storage).
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

    /// <summary>
    /// AND-ADV-CHROME (Phase A stub) — Settings tab content. Until Phase C
    /// rebuilds this with the desktop-style nested side-nav (Routing /
    /// Rules / Leak Protection / Content / Updates / Autostart), we
    /// delegate to the existing flat-list <see cref="BuildNetworkTabContent"/>
    /// builder so the tab still works.
    /// </summary>
    private Control BuildSettingsTabContent() => BuildNetworkTabContent();

    // BuildPublicTabContent — moved to AndroidApp.FreeConfigs.cs (Phase E real impl)
    // BuildToolsTabContent — moved to AndroidApp.Tools.cs (Phase E real impl)

    private Avalonia.Controls.Button MakeAdvShellTabButton(AdvancedTab tab)
    {
        // POL-2-TABS — TextBlock content (vs. plain string) lets the label
        // ellipsis-trim when the UniformGrid cell is narrower than the
        // rendered text (long labels: "Applications" / "Инструменты"
        // share a 6-column row on ~360 dp phones → ~60 dp per cell).
        // POL-1-CARDS note: the desktop ListBoxItem `Padding="10,4"` from
        // ServersPage / FreeConfigsPage was the original alignment target,
        // but POL-2's compress-to-fit overrides it — at 6-column UniformGrid
        // each cell is ~60 dp on phone width and 10,4 wouldn't leave room
        // for the label. Padding=2,8 + MinHeight=44 keeps the touch-target
        // spec while letting the ellipsis handle long labels.
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
            // 2026-05-15 (Bug-AND-001 round 2): FontSize 11→10 to ensure
            // «Приложения» (longest visible label at 10 chars) fits
            // without ellipsis on 1080-wide phones after 5-column drop.
            FontSize = 10,
            FontWeight = FontWeight.SemiBold,
            // 2026-05-15 (Bug-AND-001, brat live-test): after Bug-AND-003
            // dropped Tools tab (6 → 5 columns), all labels fit EXCEPT
            // «Приложения» (10 chars) still truncated to «Приложе...».
            // Trimmed Padding 2,8 → 0,8 + Margin 2,0 → 1,0 to claim
            // ~10 px more per tab — enough for «Приложения» SemiBold
            // FontSize=11 to render in full.
            Padding = new Thickness(0, 8),
            MinHeight = 44,
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

    /// <summary>
    /// POL-1 (2026-05-10) — sub-tab button factory shared by Tools / DPI
    /// bypass / Public sub-tab strips. Mirrors desktop's
    /// `ListBoxItem Padding="10,4" FontSize="11" CornerRadius="RadiusSm"`
    /// pattern used in ToolsPage.axaml / FreeConfigsPage.axaml so all
    /// inner sub-tab strips read consistently with desktop. Active state
    /// reuses <see cref="StyleAdvShellTab"/> for visual parity with the
    /// outer tab strip.
    /// </summary>
    internal Avalonia.Controls.Button MakeAdvancedSubTabButton(
        string label,
        bool active,
        EventHandler<Avalonia.Interactivity.RoutedEventArgs> onClick)
    {
        var btn = new Avalonia.Controls.Button
        {
            Content = label,
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            Padding = new Thickness(10, 4),
            CornerRadius = new CornerRadius(GetRadius("RadiusSm")),
            BorderThickness = new Thickness(1),
        };
        StyleAdvShellTab(btn, active);
        btn.Click += onClick;
        return btn;
    }

    /// <summary>Localized tab label — each AdvancedTab has its own TabAdv* key.</summary>
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

    /// <summary>
    /// Re-paint shell strings on language toggle. Called from
    /// <see cref="ToggleLanguageAndRefresh"/>.
    /// </summary>
    private void RefreshAdvancedShellStrings()
    {
        if (_advBrandTitle is not null)
            _advBrandTitle.Text = Localization.BrandTitle;
        if (_advSimpleToggleBtn is not null)
            _advSimpleToggleBtn.Content = Localization.AdvSimpleToggle;
        foreach (var kv in _advShellTabButtons)
        {
            // POL-2-TABS — Content is a TextBlock (for ellipsis). Update
            // the inner Text in place so the trimming behavior survives a
            // language toggle. Fallback path covers any future caller that
            // sets Content to a plain string.
            if (kv.Value.Content is TextBlock tb)
                tb.Text = AdvancedTabLabel(kv.Key);
            else
                kv.Value.Content = AdvancedTabLabel(kv.Key);
        }
        // Footer connect button + status text follow the connection state
        // (Start VPN / Stop VPN); refresh via the same helper that flips
        // them on connect/disconnect.
        ApplyAdvancedFooterConnectionState(MainActivity.IntendedConnected);

        // Phase C (2026-05-10) — Settings tab side-nav button labels and
        // footer Apply/Auto-saved row also need to flip on language toggle.
        // Sub-section button text + footer captions DO have stable
        // references, so we update them in place here.
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
        // Bug-AND-009 follow-up (2026-05-16) — most Advanced-shell tab
        // bodies bake localized strings into Border/TextBlock children
        // at construction time and don't expose per-string field refs
        // for in-place updates (only Settings sub-section + Apply
        // button are refreshable above). Rather than thread refresh
        // helpers through every tab, drop the lazy-build cache so the
        // currently-visible tab rebuilds itself in the new language.
        // _advShellContentHost.Children must be flushed too so the
        // old (stale-language) Control instance is not double-added.
        try
        {
            if (_advShellContentHost is not null)
            {
                foreach (var kv in _advShellTabContent)
                    _advShellContentHost.Children.Remove(kv.Value);
            }
            _advShellTabContent.Clear();
            // Re-build + show the current tab in the new language. The
            // overlay's visibility / selected-tab id are preserved.
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
            catch { /* swallow logging failures */ }
        }
    }

    /// <summary>
    /// Mirror connection state into the Advanced shell footer: dot color,
    /// status text, button label + colors. Called from
    /// <see cref="UpdateConnectionState"/> (Simple page) so both surfaces
    /// stay in lock-step with the engine-level intent. Safe to call before
    /// the overlay has been built — null-checks each field.
    /// </summary>
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
}
