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
    // ── Header kebab menu ──────────────────────────────────────────────

    /// <summary>
    /// v3.0 Phase 7.2 — generic factory for a kebab-menu row. Stretches
    /// horizontally, left-aligns content, transparent background. The
    /// click handler is optional (e.g. version row is non-interactive).
    /// </summary>
    private Avalonia.Controls.Button MakeMenuItem(
        string label,
        string foregroundKey,
        EventHandler<Avalonia.Interactivity.RoutedEventArgs>? onClick)
    {
        // F-12 kebab visual parity (2026-05-09): mirrors desktop
        // Style Selector="Button.menu-item" — FontSize=11, Padding=10,7,
        // CornerRadius=RadiusXs (3). Pre-fix Android used 12px / 14,8 /
        // 0px which made rows visibly taller and wider than desktop.
        var btn = new Avalonia.Controls.Button
        {
            Content = label,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(10, 7),
            FontSize = 11,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(GetRadius("RadiusXs")),
            IsHitTestVisible = onClick is not null,
        };
        btn.BindToken(Avalonia.Controls.Button.ForegroundProperty, foregroundKey);
        if (onClick is not null) btn.Click += onClick;
        return btn;
    }

    /// <summary>
    /// v3.0 Phase 7.3 (2026-05-04) — segment button factory. Mirrors
    /// desktop's <c>Classes="segment" Classes.active="..."</c> CSS:
    /// active segment uses the accent surface + accent foreground;
    /// inactive uses the base surface + secondary foreground.
    /// </summary>
    private Avalonia.Controls.Button MakeSegmentButton(
        string label,
        bool active,
        EventHandler<Avalonia.Interactivity.RoutedEventArgs> onClick)
    {
        var btn = new Avalonia.Controls.Button
        {
            Content = label,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(0, 6),
            FontSize = 12,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(GetRadius("RadiusXs")),
        };
        // v3.0 Phase 8.2 — initial bindings; StyleSegmentButton replaces
        // them on selection change so the active+inactive split moves
        // (token keys differ between the two states).
        StyleSegmentButton(btn, active);
        btn.Click += onClick;
        return btn;
    }

    /// <summary>
    /// v3.0 Phase 7.3 — wrap two segment buttons in a 2-column grid with
    /// equal width and small gap, mirroring desktop's
    /// <c>Grid ColumnDefinitions="*,*" ColumnSpacing="2"</c>.
    /// </summary>
    private Grid MakeSegmentRow(Avalonia.Controls.Button left, Avalonia.Controls.Button right)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*"),
            ColumnSpacing = 4,
            Margin = new Thickness(14, 4, 14, 4),
        };
        Grid.SetColumn(left, 0);
        Grid.SetColumn(right, 1);
        grid.Children.Add(left);
        grid.Children.Add(right);
        return grid;
    }

    /// <summary>
    /// v3.0 Phase 7.3 — overload of <see cref="AppendMenuSection"/> that
    /// accepts arbitrary <see cref="Control"/> items (not just Buttons),
    /// so segment-control rows fit the same flow.
    /// </summary>
    private void AppendMenuSectionWithControls(
        StackPanel stack,
        string headerText,
        Control[] items)
    {
        // F-12 kebab visual parity (2026-05-09): section label spec mirrors
        // desktop Style Selector="TextBlock.section-label" — FontSize=9,
        // SemiBold, TextMutedBrush, Margin="8,6,8,4". Divider moved from
        // immediately under the header (was acting as a header underline)
        // to AFTER the section's items (acts as inter-section separator,
        // matches desktop Border Classes="menu-divider"/ pattern).
        var header = new TextBlock
        {
            Text = headerText,
            FontSize = 9,
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(8, 6, 8, 4),
        };
        header.BindToken(TextBlock.ForegroundProperty, "TextMutedBrush");
        if (headerText == Localization.MenuSectionView) _menuSectionView = header;
        else if (headerText == Localization.MenuSectionDiagnostics) _menuSectionDiagnostics = header;
        else if (headerText == Localization.MenuSectionTroubleshooting) _menuSectionTroubleshooting = header;
        else if (headerText == Localization.MenuSectionAbout) _menuSectionAbout = header;

        stack.Children.Add(header);

        foreach (var item in items)
        {
            stack.Children.Add(item);
        }

        AppendMenuDivider(stack);
    }

    /// <summary>
    /// v3.0 Phase 7.2 — append a section to the kebab menu stack:
    /// header TextBlock + thin divider + the supplied items + bottom
    /// spacer. Section header TextBlocks are stored on the field
    /// (_menuSectionView etc.) so language toggle can refresh them.
    /// </summary>
    private void AppendMenuSection(
        StackPanel stack,
        string headerText,
        Avalonia.Controls.Button[] items)
    {
        // F-12 kebab visual parity (2026-05-09): mirrors desktop section-label
        // (FontSize=9 SemiBold TextMutedBrush, Margin=8,6,8,4) and moves the
        // divider to AFTER the section so it separates this section from the
        // next, instead of underlining the header. See AppendMenuSectionWithControls
        // above for the same structure for the View segments section.
        var header = new TextBlock
        {
            Text = headerText,
            FontSize = 9,
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(8, 6, 8, 4),
        };
        header.BindToken(TextBlock.ForegroundProperty, "TextMutedBrush");
        // Cache by header text so ToggleLanguageAndRefresh can find it.
        if (headerText == Localization.MenuSectionView) _menuSectionView = header;
        else if (headerText == Localization.MenuSectionDiagnostics) _menuSectionDiagnostics = header;
        else if (headerText == Localization.MenuSectionTroubleshooting) _menuSectionTroubleshooting = header;
        else if (headerText == Localization.MenuSectionAbout) _menuSectionAbout = header;

        stack.Children.Add(header);

        foreach (var item in items)
        {
            stack.Children.Add(item);
        }

        AppendMenuDivider(stack);
    }

    /// <summary>
    /// F-12 kebab visual parity (2026-05-09) — 1px <see cref="BorderSubtleBrush"/>
    /// separator between sections. Mirrors desktop's
    /// <c>Style Selector="Border.menu-divider"</c> (Height=1,
    /// Background=BorderSubtleBrush, Margin=4,4).
    /// </summary>
    private void AppendMenuDivider(StackPanel stack)
    {
        var divider = new Border
        {
            Height = 1,
            Margin = new Thickness(4, 4, 4, 4),
        };
        divider.BindToken(Border.BackgroundProperty, "BorderSubtleBrush");
        stack.Children.Add(divider);
    }

    // DEFCT-001 (2026-05-10) — recursive AccessibilityView=Raw walk over
    // the popup subtree. See call site in the kebab construction block
    // (around the _kebabPopup = new Popup{} statement) for the rationale.
    // Implementation note: we walk the LOGICAL tree via ILogical so this
    // works on the freshly-constructed subtree before it's attached to a
    // visual root (Border.Child / Panel.Children / ContentControl.Content
    // are all logical children at construction time). Setting the property
    // on each StyledElement makes its eventual AutomationPeer surface as
    // Raw, which Avalonia's IsControlElement / IsContentElement honour.
    private static void HideSubtreeFromAccessibility(StyledElement element)
    {
        AutomationProperties.SetAccessibilityView(element, AccessibilityView.Raw);
        if (element is ILogical logical)
        {
            foreach (var child in logical.LogicalChildren)
            {
                if (child is StyledElement childElement)
                    HideSubtreeFromAccessibility(childElement);
            }
        }
    }

    private void OnKebabMenuClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_kebabPopup is null) return;
        _kebabPopup.IsOpen = !_kebabPopup.IsOpen;
        // Reset the Reset-confirm flow when the menu is reopened so a
        // stale "All settings will be cleared. Continue?" prompt doesn't
        // accidentally trigger on next tap.
        if (_kebabPopup.IsOpen)
        {
            _resetConfirmPending = false;
            if (_menuResetSettingsItem is not null)
                _menuResetSettingsItem.Content = Localization.MenuItemResetSettings;
        }
    }

    // v3.0 Phase 7.3 — segmented control click handlers. Each one SETS
    // a specific value (no-op if already active) instead of toggling.
    // Matches desktop's SetThemeLight / SetThemeDark / SetLanguageRussian
    // / SetLanguageEnglish commands. Popup stays open so the user can
    // see the segment switch visually.

    private void OnMenuLangRuClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (Localization.Ru) return; // already active — no-op
        ApplyLanguage(true);
    }

    private void OnMenuLangEnClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (!Localization.Ru) return;
        ApplyLanguage(false);
    }

    private void OnMenuThemeLightClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ApplyTheme("light");
    }

    private void OnMenuThemeDarkClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ApplyTheme("dark");
    }

    /// <summary>
    /// v3.0 Phase 7.3 — set RU or EN explicitly + refresh all the
    /// labels through ToggleLanguageAndRefresh + repaint segment
    /// active state. Idempotent.
    /// DEFCT-004 (2026-05-10): pre-fix this called Localization.ToggleAndPersist()
    /// AND ToggleLanguageAndRefresh() — but ToggleLanguageAndRefresh internally
    /// also toggles, so the two calls cancelled out and tapping RU/EN never
    /// flipped state visibly. The early-return guard above already ensures
    /// we only proceed when state needs to change, so a single internal
    /// toggle (via ToggleLanguageAndRefresh) is sufficient.
    /// </summary>
    private void ApplyLanguage(bool ru)
    {
        if (Localization.Ru == ru) return;
        ToggleLanguageAndRefresh();
        RepaintLanguageSegment();
    }

    private void ApplyTheme(string mode)
    {
        var current = AndroidStorage.GetTheme();
        if (current == mode) return;
        // F4 full fix (2026-06-15, device-confirmed A101BM) — theme changes no
        // longer swap ISingleViewApplicationLifetime.MainView. The pre-fix path
        // rebuilt the whole MainView because ~257 GetBrush() snapshot sites don't
        // repaint via DynamicResource — but the root swap orphaned every
        // overlay-hosted control in the new tree: the kebab Popup AND every
        // ComboBox dropdown (Config·Mode / DPI / DNS, which host their flyouts in
        // internal Popups) wouldn't reopen, and the IntentChanged→_statusCard
        // wiring went stale — all until the app restarted. (Activity.Recreate,
        // tried even earlier, crashes the Mono runtime on Avalonia.Mobile.)
        //
        // The always-visible Simple page (BuildSimplePageView's tree) is already
        // 100% BindToken/DynamicResource, so flipping RequestedThemeVariant below
        // repaints it in place with NO rebuild — which is exactly why the kebab
        // Popup, the ComboBoxes and the status-card wiring now survive the toggle.
        // The only snapshot-heavy surfaces are the on-demand overlays (Advanced
        // shell tabs + the app-picker that lives inside them); those are refreshed
        // by RebuildSimplePageView() below, which now rebuilds ONLY the Advanced-
        // shell overlay in place (a sibling swap inside the live root Grid),
        // never the root MainView.
        //
        // The kebab popup deliberately stays OPEN across a Simple-page toggle so
        // the user sees the Light/Dark segment highlight move (RepaintThemeSegment
        // below) — the open menu repaints in place via its BindToken bindings.
        AndroidStorage.SetTheme(mode);
        RequestedThemeVariant = mode == "dark" ? ThemeVariant.Dark : ThemeVariant.Light;

        // Refresh the on-demand Advanced-shell overlay for the new theme. Posted
        // at Background priority so the chrome rebuild stays off the tap handler;
        // the live Simple page has already repainted via DynamicResource.
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            try { RebuildSimplePageView(); }
            catch (Exception ex)
            {
                try
                {
                    global::Android.Util.Log.Warn("VpnRouter.Theme",
                        $"Advanced-shell theme refresh failed: {ex.GetType().Name}: {ex.Message}");
                }
                catch { /* swallow logging failures */ }
            }
        }, Avalonia.Threading.DispatcherPriority.Background);

        // v3.0 Phase 8.2 (2026-05-07) — every property bound via
        // BindToken auto-resolves to the new theme's value through
        // Avalonia's DynamicResource pipeline. The two surfaces that
        // can't ride DynamicResource still need manual refresh:
        //   1) Mascot Bitmap — Bgra8888 byte buffer, must re-load to
        //      get the inverted dark variant (mirrors desktop's
        //      MainWindowViewModel.LogoSource pattern).
        //   2) Active-segment chrome — StyleSegmentButton/SetVpnChipState
        //      pick a different brush KEY for active vs inactive, so
        //      they need to re-bind to the right key (the theme
        //      variant change alone wouldn't move the active segment).
        if (_mascotImage is not null)
        {
            _mascotImage.Source = LoadMascot();
        }
        RepaintThemeSegment();
        RepaintLanguageSegment();
        SetVpnChipState(_vpnChipState, force: true);
        // v2.32.0 (AND-ZAPRET) — re-bind Zapret chip on theme flip too.
        // UpdateConnectionState below recomputes from current state, but
        // we force it explicitly so the BindToken call happens even when
        // state hasn't changed (mirrors the SetVpnChipState force path).
        SetZapretChipState(_zapretChipState, force: true);
        UpdateConnectionState(MainActivity.IntendedConnected);
    }

    /// <summary>
    /// F4 full fix (2026-06-15) — despite the legacy name, this no longer
    /// rebuilds the Simple page. The Simple-page tree
    /// (<see cref="BuildSimplePageView"/>) is fully DynamicResource-bound and
    /// repaints in place when <c>RequestedThemeVariant</c> flips, so it must NOT
    /// be torn down — doing so (the pre-fix MainView swap) orphaned the kebab
    /// Popup, every ComboBox dropdown and the IntentChanged→<c>_statusCard</c>
    /// wiring until restart.
    ///
    /// <para>Instead this rebuilds ONLY the on-demand Advanced-shell overlay so
    /// its ~257 <c>GetBrush()</c> snapshot sites (chrome + lazily-built tab
    /// bodies) re-pick the new theme. The rebuild is an in-place SIBLING swap
    /// inside the live root Grid (<see cref="_advShellOverlay"/> is a direct
    /// child of the Grid returned by <see cref="BuildSimplePageView"/>); the
    /// root MainView is never touched, so all the live Simple-page reactivity
    /// survives. Safe to call repeatedly. Called by <see cref="ApplyTheme(string)"/>
    /// after the theme variant changes.</para>
    /// </summary>
    private void RebuildSimplePageView()
    {
        var old = _advShellOverlay;
        if (old is null) return;
        // _advShellOverlay is a direct child of the root Grid (see the return of
        // BuildSimplePageView). Locate it so we can swap it in place without
        // touching MainView. If the tree shape ever changes and it isn't a
        // panel child, bail rather than risk a bad mutation.
        if (old.Parent is not Panel rootPanel) return;
        var idx = rootPanel.Children.IndexOf(old);
        if (idx < 0) return;

        var advancedWasOpen = old.IsVisible;
        var advancedTab = _advShellSelectedTab;

        // The kebab popup may be open and (via OnAdvancedKebabClicked) anchored
        // to the Advanced shell's kebab button, which the rebuild below replaces.
        // Dismiss it in that case so it isn't left anchored to a detached
        // PlacementTarget. When the toggle came from the Simple-page kebab
        // (overlay hidden) the popup stays open so the user sees the Light/Dark
        // segment highlight move.
        if (advancedWasOpen && _kebabPopup is not null)
            _kebabPopup.IsOpen = false;

        // Drop the lazy tab caches — their Controls belong to the OLD overlay
        // tree about to be discarded. BuildAdvancedShellOverlay repopulates
        // _advShellTabButtons; the tab bodies rebuild on next activation (same
        // contract the language-toggle rebuild in RefreshAdvancedShellStrings
        // relies on). Not clearing them would re-add stale Controls to the new
        // content host (Bug-AND-009 empty-body class).
        _advShellTabContent.Clear();
        _advShellTabButtons.Clear();

        // Build the fresh overlay BEFORE swapping so a construction exception
        // leaves the old one intact and visible.
        var fresh = BuildAdvancedShellOverlay();
        rootPanel.Children[idx] = fresh;
        _advShellOverlay = fresh;

        // Re-open the previously-active tab so the user stays where they were,
        // now rendered with the new theme. The footer connection state is seeded
        // inside BuildAdvancedShellOverlay; the Simple page (untouched) keeps its
        // own _statusCard / server-list state, so no Simple-side re-seed is needed.
        if (advancedWasOpen)
        {
            try { OpenAdvancedShell(advancedTab); }
            catch (Exception ex)
            {
                try
                {
                    global::Android.Util.Log.Warn("VpnRouter.Theme",
                        $"Restore Advanced shell after theme rebuild failed: {ex.GetType().Name}: {ex.Message}");
                }
                catch { /* swallow logging failures */ }
            }
        }
    }

    /// <summary>
    /// v3.0 Phase 7.3 — refresh segment colors after a theme change so
    /// the active segment moves to the new selection.
    /// </summary>
    private void RepaintThemeSegment()
    {
        var isDark = AndroidStorage.GetTheme() == "dark";
        StyleSegmentButton(_menuThemeLight, !isDark);
        StyleSegmentButton(_menuThemeDark, isDark);
    }

    private void RepaintLanguageSegment()
    {
        StyleSegmentButton(_menuLangRu, Localization.Ru);
        StyleSegmentButton(_menuLangEn, !Localization.Ru);
    }

    private void StyleSegmentButton(Avalonia.Controls.Button? btn, bool active)
    {
        if (btn is null) return;
        btn.FontWeight = active ? FontWeight.SemiBold : FontWeight.Normal;
        // v3.0 Phase 8.2 — re-bind via DynamicResource so the button
        // tracks ThemeVariant changes between calls. New bindings
        // replace any prior binding at LocalValue priority on the same
        // property.
        btn.BindToken(Avalonia.Controls.Button.BackgroundProperty,
            active ? "AccentBgSubtleBrush" : "SurfaceSunkenBrush");
        btn.BindToken(Avalonia.Controls.Button.ForegroundProperty,
            active ? "AccentFgBrush" : "TextSecondaryBrush");
        btn.BindToken(Avalonia.Controls.Button.BorderBrushProperty,
            active ? "BorderAccentBrush" : "BorderSubtleBrush");
    }

    /// <summary>
    /// v3.0 Phase 7.4 (2026-05-04) — Diagnostics > Open log. Reads the
    /// last 50 KB of <c>getExternalFilesDir()/singbox.log</c> into the
    /// in-app overlay viewer. Pre-7.4 this only copied the path to the
    /// clipboard, which closed handbook §5.6 only formally — users on
    /// device couldn't actually read the log without `adb`.
    /// </summary>
    // OnMenuOpenLogClicked / ShowLogViewer / OnMenuViewCrashLogClicked /
    // LoadCrashLogContent / OnLogViewer* / LoadLogContent / ShowLogEmptyState
    // moved to AndroidApp.Notifications.cs (Phase 2C Wave 9, 2026-05-18).

}
