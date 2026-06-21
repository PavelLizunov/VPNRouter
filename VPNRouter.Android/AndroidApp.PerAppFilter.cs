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
    // ── Phase 7.5 — Per-app filter UI (handbook §5.5) ───────────────────

    private void OnTunnelModeRadioChanged(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        // IsChecked changes fire on BOTH the previously-selected and the
        // newly-selected radio when group toggles, so dedupe by checking
        // the actual state.
        var splitOn = _splitRadio?.IsChecked == true;

        // v3.0 v2.32.0 — when the user toggles split ON, restore the last
        // active per-app mode ("include" or "exclude"); first-time users
        // get "include" via the GetPerAppLastMode default. Toggling split
        // OFF writes "off" to the active mode but preserves last-mode so
        // the next ON toggle is sticky.
        if (splitOn)
        {
            var current = AndroidStorage.GetPerAppMode();
            if (current == "off")
            {
                var restored = AndroidStorage.GetPerAppLastMode();
                AndroidStorage.SetPerAppMode(restored);
            }
        }
        else
        {
            if (AndroidStorage.GetPerAppMode() != "off")
            {
                AndroidStorage.SetPerAppMode("off");
            }
        }

        // Show/hide the "Choose apps…" sub-stack we tagged on the split
        // radio in BuildSimplePageView.
        if (_splitRadio?.Tag is StackPanel perAppStack)
        {
            perAppStack.IsVisible = splitOn;
        }

        UpdatePerAppFormCountLabel();
    }

    /// <summary>
    /// v3.0 v2.32.0 (2026-05-07) — keeps the form-side "Selected: N" label
    /// in sync with the saved package count + the active mode. The label
    /// suffix differs by mode so a user glancing at the form can tell
    /// whether "Selected: 3" means "3 apps go via VPN" (include) or
    /// "3 apps bypass VPN" (exclude). Called from
    /// <see cref="OnTunnelModeRadioChanged"/> + <see cref="OnAppPickerSaveClicked"/>.
    /// </summary>
    private void UpdatePerAppFormCountLabel()
    {
        if (_perAppCountLabel is null) return;
        var count = AndroidStorage.GetPerAppPackages().Count;
        var mode = AndroidStorage.GetPerAppMode();
        var fmt = mode == "exclude"
            ? Localization.PerAppCountExclude
            : Localization.PerAppCountInclude;
        _perAppCountLabel.Text = string.Format(fmt, count);
    }

    private void OnPerAppPickButtonClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ShowAppPicker();
    }

    private void ShowAppPicker()
    {
        // AND-MIGRATE-OVERLAYS (2026-05-09): "Choose apps" button now
        // deeplinks to the Advanced shell on the Applications tab. Re-seed
        // happens inside ReseedAppPickerTabState (called by the shell on
        // tab activation). AND-ADV-CHROME (2026-05-10): tab renamed
        // Apps → Applications to match desktop v2.32.0.
        OpenAdvancedShell(AdvancedTab.Applications);
    }

    /// <summary>
    /// Re-seed Apps tab state from persisted storage. Called by the
    /// Advanced shell on tab activation. Replaces the body of the old
    /// ShowAppPicker.
    /// <para>Phase D: also rebuilds the category sidebar (10 built-ins +
    /// any user-defined custom categories from
    /// <see cref="AndroidStorage.GetCustomCategories"/>) and restores the
    /// last-active category id from
    /// <see cref="AndroidStorage.GetApplicationsActiveCategory"/>. Empty / no
    /// active id keeps the right pane on the placeholder.</para>
    /// </summary>
    private async void ReseedAppPickerTabState()
    {
        // Seed the selection set from storage so check states match what
        // the user previously saved.
        _appPickerSelected = new HashSet<string>(AndroidStorage.GetPerAppPackages(),
                                                 System.StringComparer.OrdinalIgnoreCase);

        // v3.0 v2.32.0 — seed the picker mode. If storage is currently
        // "off" (user opened the picker after toggling split on but before
        // mode persisted), restore the last active mode; default to
        // "include" via GetPerAppLastMode for first-run.
        var storedMode = AndroidStorage.GetPerAppMode();
        _appPickerMode = storedMode switch
        {
            "include" => "include",
            "exclude" => "exclude",
            _ => AndroidStorage.GetPerAppLastMode(),
        };
        ApplyPickerModeVisuals();

        if (_appPickerSearch is not null) _appPickerSearch.Text = string.Empty;
        if (_appPickerSystemToggle is not null)
            _appPickerSystemToggle.IsChecked = _appPickerSystemAppsVisible;

        UpdateAppPickerCount();
        if (_appPickerList is not null)
        {
            _appPickerList.ItemsSource = new[] { Localization.PerAppLoading };
        }

        // Phase D — build the sidebar before the cache load so the row
        // styling (active highlight) is in place when the user lands on
        // the tab. Counts paint as zeros first; UpdateAllCategoryCounts
        // refreshes them after the cache returns.
        //
        // Bug #2 (2026-05-11) — mobile redesign: default to the
        // CustomCatchAll category on first open (no saved active id) so
        // the apps list is immediately populated. Pre-fix the right pane
        // showed a "← Select a category" placeholder, which on phone read
        // as "the tab is empty" — users had to discover the chip row to
        // get any apps to show. Defaulting to CustomCatchAll = "all apps"
        // matches user intent ("выбор приложений неудобен на телефоне").
        _advAppsCustomCategories = AndroidStorage.GetCustomCategories();
        var savedActiveId = AndroidStorage.GetApplicationsActiveCategory();
        _advAppsActiveCategoryId = ResolveActiveCategoryId(savedActiveId)
            ?? AndroidCategoryDefaults.CustomCatchAllId;
        RebuildAppCategorySidebar();

        try
        {
            _appPickerCache = await System.Threading.Tasks.Task.Run(() =>
                _appPickerSystemAppsVisible
                    ? AppListLoader.ListAllApps()
                    : AppListLoader.ListUserApps());
        }
        catch
        {
            _appPickerCache = new List<AppListLoader.AppEntry>();
        }
        UpdateAllCategoryCounts();
        ApplyAppPickerFilter();
    }

    /// <summary>Validate the persisted active-category id against the current
    /// built-in list + user-defined categories. Returns null if the id no
    /// longer maps (e.g. user removed a custom category between sessions),
    /// so the placeholder shows on next open instead of orphan styling.</summary>
    private string? ResolveActiveCategoryId(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        if (AndroidCategoryDefaults.Find(id) is not null) return id;
        if (IsUserDefinedCategory(id)) return id;
        return null;
    }

    private void OnAppPickerSaveClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        AndroidStorage.SetPerAppPackages(_appPickerSelected);
        // v3.0 v2.32.0 — persist mode + sticky-restore key in one step so
        // the next split-radio toggle restores the same mode.
        AndroidStorage.SetPerAppMode(_appPickerMode);
        AndroidStorage.SetPerAppLastMode(_appPickerMode);
        UpdatePerAppFormCountLabel();
        // AND-MIGRATE-OVERLAYS (2026-05-09): Save no longer dismisses the
        // surface — Apps lives as a tab inside the Advanced shell. The
        // count label refresh + storage flush is enough; the user closes
        // the shell when they're done.
    }

    private void OnAppPickerModeIncludeClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_appPickerMode == "include") return;
        _appPickerMode = "include";
        ApplyPickerModeVisuals();
    }

    private void OnAppPickerModeExcludeClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_appPickerMode == "exclude") return;
        _appPickerMode = "exclude";
        ApplyPickerModeVisuals();
    }

    /// <summary>
    /// v3.0 v2.32.0 (2026-05-07) — repaints the include/exclude segment
    /// buttons + the hint TextBlock based on <see cref="_appPickerMode"/>.
    /// Mirrors how the kebab menu's theme/language segment row paints
    /// active/inactive (see <see cref="MakeSegmentButton"/>).
    /// </summary>
    private void ApplyPickerModeVisuals()
    {
        var includeActive = _appPickerMode == "include";
        var excludeActive = _appPickerMode == "exclude";
        StyleSegment(_appPickerModeIncludeBtn, includeActive);
        StyleSegment(_appPickerModeExcludeBtn, excludeActive);
        if (_appPickerModeHint is not null)
        {
            _appPickerModeHint.Text = excludeActive
                ? Localization.PerAppHintExclude
                : Localization.PerAppHintInclude;
        }
    }

    private void StyleSegment(Avalonia.Controls.Button? btn, bool active)
    {
        if (btn is null) return;
        btn.Background = active ? GetBrush("AccentBgSubtleBrush") : GetBrush("SurfaceSunkenBrush");
        btn.Foreground = active ? GetBrush("AccentFgBrush") : GetBrush("TextSecondaryBrush");
        btn.BorderBrush = active ? GetBrush("BorderAccentBrush") : GetBrush("BorderSubtleBrush");
        btn.FontWeight = active ? FontWeight.SemiBold : FontWeight.Normal;
    }

    private void OnAppPickerSystemToggleChanged(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var newValue = _appPickerSystemToggle?.IsChecked == true;
        if (newValue == _appPickerSystemAppsVisible) return;
        _appPickerSystemAppsVisible = newValue;
        // Reload list with the new include-system flag. This might take a
        // beat on slow devices; reuse the show flow for the loading state.
        _ = ReloadAppPickerCacheAsync();
    }

    private async System.Threading.Tasks.Task ReloadAppPickerCacheAsync()
    {
        if (_appPickerList is null) return;
        _appPickerList.ItemsSource = new[] { Localization.PerAppLoading };
        try
        {
            _appPickerCache = await System.Threading.Tasks.Task.Run(() =>
                _appPickerSystemAppsVisible
                    ? AppListLoader.ListAllApps()
                    : AppListLoader.ListUserApps());
        }
        catch
        {
            _appPickerCache = new List<AppListLoader.AppEntry>();
        }
        ApplyAppPickerFilter();
    }

    private void OnAppPickerSearchChanged(object? sender, Avalonia.Controls.TextChangedEventArgs e)
    {
        ApplyAppPickerFilter();
    }

    /// <summary>
    /// Apply the current search term to <see cref="_appPickerCache"/> and
    /// refresh the ListBox with a row factory that builds CheckBox + label
    /// per visible app. Each row's CheckedChanged updates
    /// <see cref="_appPickerSelected"/> immediately so Save just persists
    /// the in-memory set.
    /// <para>Phase D: rows are first scoped to the active category. Built-in
    /// categories filter to their hint-package list; the catch-all "Custom"
    /// shows all apps; user-created categories show all apps too (the
    /// CustomCategory.Apps[] tag list grows as the user checks them while
    /// the category is active).</para>
    /// </summary>
    private void ApplyAppPickerFilter()
    {
        if (_appPickerList is null) return;
        var search = _appPickerSearch?.Text?.Trim() ?? string.Empty;

        // Phase D — category scope is the first filter. No active category =
        // empty pane (placeholder is shown by SetActiveAppCategory anyway,
        // but ItemsSource still needs to be empty so the ListBox doesn't
        // flash the previous category's rows).
        IEnumerable<AppListLoader.AppEntry> scoped = ScopeAppsToActiveCategory(_appPickerCache);

        var filtered = string.IsNullOrEmpty(search)
            ? scoped
            : scoped.Where(a =>
                a.Label.Contains(search, System.StringComparison.OrdinalIgnoreCase)
                || a.PackageName.Contains(search, System.StringComparison.OrdinalIgnoreCase));

        // v3.0 — Selected / Available split mirrors desktop ApplicationsPage
        // category structure. Sections are computed only at filter time
        // (search/system-toggle change); per-row checkbox toggles update
        // the selected count but leave rows in their current section so
        // the user doesn't lose scroll position mid-tap.
        var selectedRows = new List<AppListLoader.AppEntry>();
        var availableRows = new List<AppListLoader.AppEntry>();
        foreach (var app in filtered)
        {
            if (_appPickerSelected.Contains(app.PackageName))
                selectedRows.Add(app);
            else
                availableRows.Add(app);
        }

        var rows = new List<Control>(selectedRows.Count + availableRows.Count + 2);
        if (selectedRows.Count > 0)
        {
            rows.Add(BuildPickerSectionHeader(Localization.PerAppGroupSelected, selectedRows.Count));
            foreach (var app in selectedRows) rows.Add(BuildAppRow(app));
        }
        if (availableRows.Count > 0)
        {
            rows.Add(BuildPickerSectionHeader(Localization.PerAppGroupAvailable, availableRows.Count));
            foreach (var app in availableRows) rows.Add(BuildAppRow(app));
        }

        _appPickerList.ItemsSource = rows;
        UpdateAppPickerCount();
        // Bug #2 (2026-05-11) — surface the visible-app count so users
        // can verify the launcher-activities fallback in AppListLoader is
        // doing its job. Sum of selectedRows + availableRows == filtered
        // apps within active category scope (post-search). When the user
        // toggles "System apps" the reload reseeds _appPickerCache before
        // this runs.
        if (_appPickerShowingCount is not null)
        {
            _appPickerShowingCount.Text = string.Format(
                Localization.PerAppShowingCount,
                selectedRows.Count + availableRows.Count);
        }
    }

    /// <summary>Filter the installed-app cache down to the apps that belong
    /// to the active category. Built-ins use a static hint package set; the
    /// catch-all + user-defined custom categories surface all installed
    /// apps. Empty / unknown active id returns an empty sequence so the
    /// right pane shows nothing while the placeholder is visible.</summary>
    private IEnumerable<AppListLoader.AppEntry> ScopeAppsToActiveCategory(IEnumerable<AppListLoader.AppEntry> source)
    {
        if (string.IsNullOrEmpty(_advAppsActiveCategoryId))
            return System.Linq.Enumerable.Empty<AppListLoader.AppEntry>();

        // Custom catch-all + user-created custom categories: scope = all
        // installed apps. Same code path so the user can pick freely from
        // the full list either way.
        if (AndroidCategoryDefaults.IsCustomCatchAll(_advAppsActiveCategoryId)
            || IsUserDefinedCategory(_advAppsActiveCategoryId))
        {
            return source;
        }

        var def = AndroidCategoryDefaults.Find(_advAppsActiveCategoryId);
        if (def is null || def.PackageHints.Count == 0)
            return System.Linq.Enumerable.Empty<AppListLoader.AppEntry>();

        var hintSet = new HashSet<string>(def.PackageHints, System.StringComparer.OrdinalIgnoreCase);
        return source.Where(a => hintSet.Contains(a.PackageName));
    }

    private bool IsUserDefinedCategory(string id)
    {
        foreach (var cat in _advAppsCustomCategories)
            if (string.Equals(cat.Name, id, System.StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private VPNRouter.Core.Models.CustomCategory? FindUserDefinedCategory(string id)
    {
        foreach (var cat in _advAppsCustomCategories)
            if (string.Equals(cat.Name, id, System.StringComparison.OrdinalIgnoreCase))
                return cat;
        return null;
    }

    private Control BuildAppRow(AppListLoader.AppEntry app)
    {
        // v3.0 — visual parity with desktop ApplicationsPage Border.app-row:
        // sunken-bg rounded block, padding 10/7, 4-pt margin between rows.
        // Desktop has no per-app icon (Windows doesn't expose a uniform
        // per-process icon API) so the icon slot is Android-only polish;
        // typography (TextPrimary name + TextMuted secondary) and the
        // rounded-block surround mirror desktop one-to-one. CheckBox sits
        // trailing per Material list convention — desktop puts it leading,
        // but the touch ergonomics differ (large finger tapping a leading
        // checkbox occludes the icon/label readability mid-tap).
        var label = new TextBlock
        {
            Text = app.Label,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        label.BindToken(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        var pkgLine = new TextBlock
        {
            Text = app.PackageName,
            FontSize = 9,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        pkgLine.BindToken(TextBlock.ForegroundProperty, "TextMutedBrush");
        var rowText = new StackPanel
        {
            Spacing = 1,
            Children = { label, pkgLine },
            VerticalAlignment = VerticalAlignment.Center,
        };

        var checkbox = new Avalonia.Controls.CheckBox
        {
            IsChecked = _appPickerSelected.Contains(app.PackageName),
            VerticalAlignment = VerticalAlignment.Center,
            MinHeight = 0,
            Padding = new Thickness(0),
        };
        checkbox.IsCheckedChanged += (_, __) =>
        {
            if (checkbox.IsChecked == true)
                _appPickerSelected.Add(app.PackageName);
            else
                _appPickerSelected.Remove(app.PackageName);

            // Bug-AND-013 (2026-05-16) — persist on every toggle so the
            // selection survives a tab rebuild (theme/lang switch goes
            // through ReseedAppPickerTabState which reads
            // AndroidStorage.GetPerAppPackages back into the in-memory
            // set). Pre-fix the Save button was the only persist path,
            // and a theme flip mid-edit silently dropped every unsaved
            // tap. Now the Done button is purely a visual "close"
            // affordance (storage is already up to date).
            AndroidStorage.SetPerAppPackages(_appPickerSelected);

            // Phase D — when active category is a user-defined custom one,
            // mirror the toggle into its Apps[] tag list so the sidebar
            // count + persisted membership reflect what the user just did.
            // Built-in hint lists are static; toggling there only affects
            // _appPickerSelected.
            if (!string.IsNullOrEmpty(_advAppsActiveCategoryId))
            {
                var custom = FindUserDefinedCategory(_advAppsActiveCategoryId);
                if (custom is not null)
                {
                    custom.Apps ??= new List<string>();
                    if (checkbox.IsChecked == true)
                    {
                        if (!custom.Apps.Any(p => string.Equals(p, app.PackageName, System.StringComparison.OrdinalIgnoreCase)))
                            custom.Apps.Add(app.PackageName);
                    }
                    else
                    {
                        custom.Apps.RemoveAll(p => string.Equals(p, app.PackageName, System.StringComparison.OrdinalIgnoreCase));
                    }
                    AndroidStorage.SetCustomCategories(_advAppsCustomCategories);
                }
            }

            UpdateAppPickerCount();
            UpdateAllCategoryCounts();
        };

        // 32dp icon — Material medium list-icon size, matches the touch
        // density of the rounded-block row. Cached Bitmap from
        // AppIconCache; null slot stays blank rather than placeholder.
        var iconImage = new Image
        {
            Width = 32,
            Height = 32,
            VerticalAlignment = VerticalAlignment.Center,
            Stretch = Stretch.Uniform,
            Source = app.IconBitmap,
        };
        RenderOptions.SetBitmapInterpolationMode(iconImage, BitmapInterpolationMode.HighQuality);

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            ColumnSpacing = 10,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(iconImage, 0);
        Grid.SetColumn(rowText, 1);
        Grid.SetColumn(checkbox, 2);
        grid.Children.Add(iconImage);
        grid.Children.Add(rowText);
        grid.Children.Add(checkbox);

        var rowBorder = new Border
        {
            CornerRadius = new CornerRadius(GetRadius("RadiusSm")),
            Padding = new Thickness(10, 7),
            Margin = new Thickness(0, 0, 0, 4),
            MinHeight = 44,
            Child = grid,
        };
        rowBorder.BindToken(Border.BackgroundProperty, "SurfaceSunkenBrush");
        // Bug-AND-008 / Bug-AND-013 (2026-05-16) — synthetic "tap row
        // to toggle" was the source of the scroll-toggle accidents in
        // the original brat report. Every implementation candidate had
        // an issue:
        //   - Bare PointerPressed: fired mid-scroll → mass toggles.
        //   - Manual time+distance: ListBoxItem captures the pointer
        //     for selection, swallowing PointerReleased on the inner
        //     Border so even genuine taps were ignored.
        //   - Tapped event: ScrollViewer's recognizer marks it Handled
        //     on scrolls before it bubbles past the inner Border.
        // Resolution: drop the row-Border tap handler. Users toggle via
        // the explicit CheckBox at the row's trailing edge — the
        // checkbox handler (above) is the single source of selection
        // truth and already auto-persists to AndroidStorage (Bug-AND-013).
        // CheckBox is large enough (32 dp visual + 44 dp implicit
        // Material touch target) to remain ergonomic with one hand.
        return rowBorder;
    }

    /// <summary>
    /// Section header for the per-app picker — mirrors desktop
    /// ApplicationsPage cat-name + cat-count style: SemiBold secondary
    /// label on the left, mono muted count on the right. Used to split
    /// the picker into "Selected" / "Available" subsections so users
    /// see at a glance what's currently routed via VPN vs the rest.
    /// </summary>
    private Control BuildPickerSectionHeader(string label, int count)
    {
        var nameTb = new TextBlock
        {
            Text = label,
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        nameTb.BindToken(TextBlock.ForegroundProperty, "TextSecondaryBrush");

        var countTb = new TextBlock
        {
            Text = count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            FontSize = 9,
            FontFamily = new FontFamily("Consolas, SF Mono, Cascadia Code, Ubuntu Mono, monospace"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 0, 0),
        };
        countTb.BindToken(TextBlock.ForegroundProperty, "TextMutedBrush");

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
        };
        Grid.SetColumn(nameTb, 0);
        Grid.SetColumn(countTb, 1);
        grid.Children.Add(nameTb);
        grid.Children.Add(countTb);

        return new Border
        {
            Padding = new Thickness(2, 8, 2, 4),
            Child = grid,
        };
    }

    private void UpdateAppPickerCount()
    {
        if (_appPickerCount is not null)
            _appPickerCount.Text = string.Format(Localization.PerAppCount, _appPickerSelected.Count);
    }

    /// <summary>
    /// Phase D (AND-ADV-APPS-CATEGORIES, 2026-05-10) — Applications tab body.
    /// Mirrors desktop <c>ApplicationsPage.axaml</c> two-column master/detail
    /// layout: ~140dp category sidebar on the left (with an inline
    /// "+ New category" form at the bottom) and the per-category app list on
    /// the right. The right pane shows the include/exclude mode picker, a
    /// search box, the system-apps toggle, and a scoped checkbox list of
    /// installed apps. The shell provides the title bar / close button.
    /// <para>The 10 built-in categories come from <see cref="AndroidCategoryDefaults"/>;
    /// user-created categories live in <see cref="AndroidStorage.GetCustomCategories"/>
    /// and are appended below the catch-all "Custom" row.</para>
    /// </summary>
    private Control BuildAppPickerTabContent()
    {
        // Bug #2 (2026-05-11) — single-column mobile-first layout. The
        // pre-fix design cloned desktop's 2-pane Grid (140dp sidebar +
        // right pane) which left ~330dp for the right pane on a 1080-px
        // phone — too cramped for icon + label + package + checkbox.
        // Replaced with a vertical DockPanel: search → horizontal
        // category chip row → +New row → mode picker → mode hint →
        // count/system-toggle row → app list (fills) → sticky Save.
        //
        // Reference points: Material Design app picker pattern
        // (filter chips on top), desktop divergence intentional per user
        // feedback ("Выбор приложений идентичный desktop неудобен на
        // телефоне"). The fields _advAppsRightPanePlaceholder /
        // _advAppsRightPaneScopeContainer stay declared because other
        // call sites null-check them, but they no longer participate in
        // the visual tree.

        // ── Category chip grid (categories) ──────────────────────────
        // Bug-AND-008 (2026-05-16) — replaced horizontal-scrollable strip
        // with a WrapPanel. The previous design (Horizontal StackPanel
        // inside a ScrollViewer) had three issues on Android:
        //   1. ScrollViewer's gesture recogniser preemptively captured
        //      every chip-press as a possible scroll-start, so chip
        //      activation needed unreliable tap-detection heuristics.
        //   2. "Custom" was offscreen-right and required a long swipe
        //      to reach (the catch-all is the most-used scope).
        //   3. Horizontal scrolling itself is awkward on a phone — users
        //      had to swipe with one hand while reading labels.
        // WrapPanel makes every chip simultaneously visible and tappable
        // — at typical font sizes 10 built-in categories fit in 2 rows
        // on a 1080dp screen. Custom now leads the first row
        // (RebuildAppCategorySidebar ordering).
        _advAppsCategoryListPanel = new StackPanel
        {
            // Keep the field type as StackPanel so the rest of the
            // codebase (.Children.Clear, .Children.Add) keeps working
            // without churn. We swap the panel into a WrapPanel host
            // via a wrapper Panel that lets us re-layout to wrap-flow
            // semantics without changing the field type.
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 6,
        };
        var chipWrapHost = new WrapPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            // Use an outer Border per row by laying chips directly in
            // a WrapPanel — children flow left-to-right and wrap to
            // the next line as needed.
            Margin = new Thickness(8, 4, 8, 4),
        };
        // Swap: WrapPanel hosts the rows directly. We replace the
        // StackPanel role by pointing _advAppsCategoryListPanel at a
        // panel that *is* the WrapPanel surface. Easiest pattern: keep
        // the StackPanel field but reassign children every rebuild via
        // a tiny adapter.
        // Concretely: the WrapPanel holds chips directly; rebuild adds
        // them straight to chipWrapHost. _advAppsCategoryListPanel is
        // kept as an alias bound to chipWrapHost.Children via
        // _advAppsCategoryWrapHost (private field set below).
        _advAppsCategoryWrapHost = chipWrapHost;

        // ── "+ New category" inline row (always visible, compact) ────
        _advAppsNewCategoryInput = new TextBox
        {
            Watermark = Localization.AdvAppsCategoryNamePlaceholder,
            FontSize = 11,
            Padding = new Thickness(8, 6),
            CornerRadius = new CornerRadius(GetRadius("RadiusXs")),
            BorderThickness = new Thickness(1),
        };
        _advAppsNewCategoryInput.BindToken(TextBox.BackgroundProperty, "SurfaceSunkenBrush");
        _advAppsNewCategoryInput.BindToken(TextBox.BorderBrushProperty, "BorderSubtleBrush");

        _advAppsAddCategoryBtn = new Avalonia.Controls.Button
        {
            Content = Localization.AdvAppsAddCategoryButton,
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(12, 6),
            CornerRadius = new CornerRadius(GetRadius("RadiusXs")),
            BorderThickness = new Thickness(0),
        };
        _advAppsAddCategoryBtn.BindToken(Avalonia.Controls.Button.BackgroundProperty, "AccentSolidBrush");
        _advAppsAddCategoryBtn.BindToken(Avalonia.Controls.Button.ForegroundProperty, "AccentOnSolidBrush");
        _advAppsAddCategoryBtn.Click += OnAdvAppsAddCategoryClicked;

        var addCategoryRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            ColumnSpacing = 6,
            Margin = new Thickness(8, 0, 8, 6),
        };
        Grid.SetColumn(_advAppsNewCategoryInput, 0);
        Grid.SetColumn(_advAppsAddCategoryBtn, 1);
        addCategoryRow.Children.Add(_advAppsNewCategoryInput);
        addCategoryRow.Children.Add(_advAppsAddCategoryBtn);

        // ── Scope body (mode picker + filters + apps list + Save) ────
        var scopeBody = BuildAppPickerScopeBody();
        _advAppsRightPaneScopeContainer = new Border
        {
            Child = scopeBody,
            IsVisible = true,
        };

        // Field still declared but unused in mobile layout. Pre-fix the
        // right pane could swap to a "← Select a category" placeholder;
        // mobile design defaults the active category to CustomCatchAll
        // (all apps), so the apps list is always populated.
        _advAppsRightPanePlaceholder = null;

        var dock = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(chipWrapHost, Dock.Top);
        DockPanel.SetDock(addCategoryRow, Dock.Top);
        dock.Children.Add(chipWrapHost);
        dock.Children.Add(addCategoryRow);
        dock.Children.Add(_advAppsRightPaneScopeContainer);

        var body = new Border { Child = dock };
        body.BindToken(Border.BackgroundProperty, "SurfaceAppBrush");
        return body;
    }

    /// <summary>
    /// Right-pane content used when a category is active: include/exclude
    /// segmented control + hint + search + system-apps toggle + apps ListBox
    /// + Save bar. Factored out of <see cref="BuildAppPickerTabContent"/> so
    /// the placeholder ("← Select a category") and the scoped body can swap
    /// via <see cref="_advAppsRightPaneScopeContainer"/> visibility without
    /// rebuilding the widget tree.
    /// </summary>
    private Control BuildAppPickerScopeBody()
    {
        _appPickerModeLabel = new TextBlock
        {
            Text = Localization.PerAppPickerModeLabel,
            FontSize = 10,
            FontWeight = FontWeight.SemiBold,
            Foreground = GetBrush("TextMutedBrush"),
            Margin = new Thickness(8, 6, 8, 2),
        };
        _appPickerModeIncludeBtn = MakeSegmentButton(
            Localization.PerAppModeInclude,
            _appPickerMode == "include",
            OnAppPickerModeIncludeClicked);
        _appPickerModeExcludeBtn = MakeSegmentButton(
            Localization.PerAppModeExclude,
            _appPickerMode == "exclude",
            OnAppPickerModeExcludeClicked);
        var modeRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*"),
            ColumnSpacing = 4,
            Margin = new Thickness(8, 0, 8, 4),
        };
        Grid.SetColumn(_appPickerModeIncludeBtn, 0);
        Grid.SetColumn(_appPickerModeExcludeBtn, 1);
        modeRow.Children.Add(_appPickerModeIncludeBtn);
        modeRow.Children.Add(_appPickerModeExcludeBtn);
        _appPickerModeHint = new TextBlock
        {
            Text = _appPickerMode == "exclude"
                ? Localization.PerAppHintExclude
                : Localization.PerAppHintInclude,
            FontSize = 9,
            Foreground = GetBrush("TextMutedBrush"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(8, 0, 8, 6),
        };

        _appPickerSearch = new TextBox
        {
            Watermark = Localization.PerAppSearchHint,
            FontSize = 12,
            Padding = new Thickness(10, 6),
            CornerRadius = new CornerRadius(GetRadius("RadiusXs")),
            BorderThickness = new Thickness(1),
        };
        _appPickerSearch.BindToken(TextBox.BackgroundProperty, "SurfaceSunkenBrush");
        _appPickerSearch.BindToken(TextBox.BorderBrushProperty, "BorderSubtleBrush");
        _appPickerSearch.TextChanged += OnAppPickerSearchChanged;

        var systemToggleLabel = new TextBlock
        {
            Text = Localization.PerAppSystemAppsToggle,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
        };
        systemToggleLabel.BindToken(TextBlock.ForegroundProperty, "TextSecondaryBrush");

        _appPickerSystemToggle = new Avalonia.Controls.CheckBox
        {
            Content = systemToggleLabel,
            IsChecked = _appPickerSystemAppsVisible,
            MinHeight = 0,
            Padding = new Thickness(4, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        _appPickerSystemToggle.IsCheckedChanged += OnAppPickerSystemToggleChanged;

        _appPickerCount = new TextBlock
        {
            Text = string.Format(Localization.PerAppCount, 0),
            FontSize = 10,
            FontFamily = new FontFamily("Consolas, SF Mono, Cascadia Code, Ubuntu Mono, monospace"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        _appPickerCount.BindToken(TextBlock.ForegroundProperty, "TextMutedBrush");

        // Bug #2 (2026-05-11) — "Showing N apps" hint sits next to the
        // system-toggle so users can verify the enumeration is producing
        // a sane count. Pre-fix the user reported apps missing on Xiaomi
        // MIUI; the launcher-activities fallback in AppListLoader plus
        // this visible count makes the regression detectable at a glance.
        _appPickerShowingCount = new TextBlock
        {
            Text = string.Format(Localization.PerAppShowingCount, 0),
            FontSize = 10,
            FontFamily = new FontFamily("Consolas, SF Mono, Cascadia Code, Ubuntu Mono, monospace"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        _appPickerShowingCount.BindToken(TextBlock.ForegroundProperty, "TextMutedBrush");

        var filterRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            ColumnSpacing = 8,
            Margin = new Thickness(8, 6, 8, 0),
        };
        Grid.SetColumn(_appPickerSearch, 0);
        Grid.SetColumn(_appPickerCount, 1);
        filterRow.Children.Add(_appPickerSearch);
        filterRow.Children.Add(_appPickerCount);

        // Compact row: [☐ System apps]   spacer   Showing: N
        var togglesRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            ColumnSpacing = 8,
            Margin = new Thickness(8, 4, 8, 4),
        };
        Grid.SetColumn(_appPickerSystemToggle, 0);
        Grid.SetColumn(_appPickerShowingCount, 2);
        togglesRow.Children.Add(_appPickerSystemToggle);
        togglesRow.Children.Add(_appPickerShowingCount);

        _appPickerList = new ListBox
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
        };

        _appPickerSaveBtn = new Avalonia.Controls.Button
        {
            Content = Localization.PerAppSaveButton,
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(0, 12),
            Margin = new Thickness(8, 6, 8, 8),
            CornerRadius = new CornerRadius(GetRadius("RadiusSm")),
            BorderThickness = new Thickness(0),
        };
        _appPickerSaveBtn.BindToken(Avalonia.Controls.Button.BackgroundProperty, "AccentSolidBrush");
        _appPickerSaveBtn.BindToken(Avalonia.Controls.Button.ForegroundProperty, "AccentOnSolidBrush");
        _appPickerSaveBtn.Click += OnAppPickerSaveClicked;

        // Bug #2 (2026-05-11) — mobile-first dock order: search-first at
        // the top (most-used on phone, thumb-reach), then include/exclude
        // mode + hint, then the system-toggle / showing-count row, then
        // the apps list (fills), and a sticky Save button at the bottom.
        // Pre-fix put mode label + buttons above search; on phone that
        // wasted prime thumb-reach real estate on a setting users rarely
        // change after first run.
        var dock = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(filterRow, Dock.Top);
        DockPanel.SetDock(_appPickerModeLabel!, Dock.Top);
        DockPanel.SetDock(modeRow, Dock.Top);
        DockPanel.SetDock(_appPickerModeHint!, Dock.Top);
        DockPanel.SetDock(togglesRow, Dock.Top);
        DockPanel.SetDock(_appPickerSaveBtn!, Dock.Bottom);
        dock.Children.Add(filterRow);
        dock.Children.Add(_appPickerModeLabel!);
        dock.Children.Add(modeRow);
        dock.Children.Add(_appPickerModeHint!);
        dock.Children.Add(togglesRow);
        dock.Children.Add(_appPickerSaveBtn!);
        dock.Children.Add(_appPickerList!);
        return dock;
    }

    /// <summary>
    /// Rebuild the left category sidebar: 10 built-ins + any user-created
    /// custom categories. Each row is a clickable Border so the whole pill
    /// reacts to taps; the active row gets <c>AccentBgSubtleBrush</c> +
    /// <c>AccentFgBrush</c> styling. Counts come from
    /// <see cref="ComputeCategoryCount"/> against the cached app list.
    /// </summary>
    private void RebuildAppCategorySidebar()
    {
        // Bug-AND-008 (2026-05-16) — WrapPanel host replaces the
        // scroll-strip StackPanel. Write chips into the WrapPanel so
        // they wrap to multiple rows instead of overflowing into a
        // horizontal ScrollViewer.
        var host = (Avalonia.Controls.Panel?)_advAppsCategoryWrapHost
                   ?? _advAppsCategoryListPanel;
        if (host is null) return;
        host.Children.Clear();
        _advAppsCategoryRowMap.Clear();
        _advAppsCategoryCountMap.Clear();
        _advAppsCategoryNameMap.Clear();

        // Bug-AND-008c (2026-05-16) — render Custom (the catch-all
        // "all apps" scope) FIRST. brat reported having to scroll
        // a long way right to reach Custom; with the WrapPanel layout
        // the chip is now on the first row at the top-left.
        var customDef = AndroidCategoryDefaults.All
            .FirstOrDefault(d => AndroidCategoryDefaults.IsCustomCatchAll(d.Id));
        if (customDef is not null)
        {
            var customRow = MakeAppsCategoryRow(
                customDef.Id,
                Localization.AdvAppsCategoryCustom,
                isCustom: false);
            host.Children.Add(customRow);
        }

        // Built-ins next (skip Custom — already added).
        foreach (var def in AndroidCategoryDefaults.All)
        {
            if (AndroidCategoryDefaults.IsCustomCatchAll(def.Id)) continue;
            var displayName = Localization.GroupDisplayName(def.Id);
            var row = MakeAppsCategoryRow(def.Id, displayName, isCustom: false);
            host.Children.Add(row);
        }

        // User-created custom categories below the built-ins.
        foreach (var cat in _advAppsCustomCategories)
        {
            if (string.IsNullOrWhiteSpace(cat.Name)) continue;
            var row = MakeAppsCategoryRow(cat.Name, cat.Name, isCustom: true);
            host.Children.Add(row);
        }

        UpdateAllCategoryCounts();
        StyleActiveCategoryRow();
    }

    /// <summary>One chip in the horizontal category strip — name + optional
    /// count rendered inline as a compact pill. Whole chip is tappable; active
    /// chip repaints via <see cref="StyleActiveCategoryRow"/> with an accent
    /// border and tinted background.
    /// <para>Bug #2 (2026-05-11): replaced the vertical sidebar row layout
    /// with a compact horizontal pill (Material filter-chip pattern). The
    /// pre-fix row spanned the full sidebar width (~120dp); chip width is now
    /// driven by content + 10/6 padding so 6-8 chips fit in a single 1080px
    /// row with horizontal scroll for the rest.</para></summary>
    private Border MakeAppsCategoryRow(string id, string displayName, bool isCustom)
    {
        var nameTb = new TextBlock
        {
            Text = displayName,
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        nameTb.BindToken(TextBlock.ForegroundProperty, "TextSecondaryBrush");

        var countTb = new TextBlock
        {
            Text = string.Empty,
            FontSize = 9,
            FontFamily = new FontFamily("Consolas, SF Mono, Cascadia Code, Ubuntu Mono, monospace"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 0, 0),
        };
        countTb.BindToken(TextBlock.ForegroundProperty, "TextMutedBrush");

        var inner = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { nameTb, countTb },
        };

        var border = new Border
        {
            Padding = new Thickness(10, 6),
            CornerRadius = new CornerRadius(GetRadius("RadiusSm")),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(1),
            Child = inner,
        };
        border.BindToken(Border.BorderBrushProperty, "BorderSubtleBrush");
        // Bug-AND-008 (2026-05-16) — chip strip is now a WrapPanel (no
        // surrounding ScrollViewer), so plain PointerPressed activation
        // is safe: no horizontal scroll to mistakenly trigger. Every
        // category is simultaneously visible and tappable.
        border.PointerPressed += (_, _) => SetActiveAppCategory(id);

        // Bug-AND-019 (2026-05-16) — long-press on a user-defined custom
        // category brings up a delete confirmation. Built-in categories
        // (Discord, Browsers, Custom catch-all, etc.) are immutable and
        // ignore the gesture.
        if (isCustom)
        {
            // Wave 23 (2026-05-18) — Avalonia 12 made Gestures internal;
            // hoist the same routed event off InputElement (it's the same
            // RoutedEvent instance underneath, just publicly re-exposed).
            border.AddHandler(InputElement.HoldingEvent, (_, e) =>
            {
                if (e.HoldingState == HoldingState.Started)
                    PromptDeleteCustomCategory(id);
            });
        }

        _advAppsCategoryRowMap[id] = border;
        _advAppsCategoryCountMap[id] = countTb;
        _advAppsCategoryNameMap[id] = nameTb;
        return border;
    }

    /// <summary>Recompute "selected ∩ scope" count for every sidebar row.</summary>
    private void UpdateAllCategoryCounts()
    {
        foreach (var def in AndroidCategoryDefaults.All)
        {
            if (!_advAppsCategoryCountMap.TryGetValue(def.Id, out var tb)) continue;
            var n = ComputeCategoryCount(def.Id);
            tb.Text = n > 0 ? n.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty;
        }
        foreach (var cat in _advAppsCustomCategories)
        {
            if (string.IsNullOrWhiteSpace(cat.Name)) continue;
            if (!_advAppsCategoryCountMap.TryGetValue(cat.Name, out var tb)) continue;
            var n = ComputeCustomCategoryCount(cat);
            tb.Text = n > 0 ? n.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty;
        }
    }

    /// <summary>Selected packages within this built-in category's hint scope.
    /// For the catch-all, that's "selected NOT in any built-in hint" so the
    /// counts across all sidebar rows partition the selected set.</summary>
    private int ComputeCategoryCount(string id)
    {
        if (AndroidCategoryDefaults.IsCustomCatchAll(id))
        {
            var allBuiltIn = AndroidCategoryDefaults.AllBuiltInPackages();
            int n = 0;
            foreach (var pkg in _appPickerSelected)
                if (!allBuiltIn.Contains(pkg)) n++;
            return n;
        }

        var def = AndroidCategoryDefaults.Find(id);
        if (def is null) return 0;
        int hits = 0;
        foreach (var hint in def.PackageHints)
            if (_appPickerSelected.Contains(hint)) hits++;
        return hits;
    }

    /// <summary>Selected packages within a user-defined custom category's
    /// tagged Apps[] list (mirrors desktop's Apps.Count display semantics for
    /// custom categories: shows the user's own membership view).</summary>
    private int ComputeCustomCategoryCount(VPNRouter.Core.Models.CustomCategory cat)
    {
        if (cat.Apps is null || cat.Apps.Count == 0) return 0;
        int hits = 0;
        foreach (var pkg in cat.Apps)
            if (_appPickerSelected.Contains(pkg)) hits++;
        return hits;
    }

    /// <summary>Repaint the active chip with an accent border + tinted
    /// background. Bug #2 (2026-05-11): pre-fix this used the desktop's
    /// "lifted-card" affordance (SurfaceBaseBrush) which was illegible on a
    /// horizontal chip strip — chips need a visible border state, not a
    /// background lift. Now uses BorderAccentBrush + AccentBgSubtleBrush.</summary>
    private void StyleActiveCategoryRow()
    {
        var activeBg = GetBrush("AccentBgSubtleBrush");
        var activeBorder = GetBrush("BorderAccentBrush");
        var inactiveBorder = GetBrush("BorderSubtleBrush");
        var accentFg = GetBrush("AccentFgBrush");
        var defaultName = GetBrush("TextSecondaryBrush");
        var defaultCount = GetBrush("TextMutedBrush");

        foreach (var kv in _advAppsCategoryRowMap)
        {
            var isActive = string.Equals(kv.Key, _advAppsActiveCategoryId, System.StringComparison.OrdinalIgnoreCase);
            kv.Value.Background = isActive ? activeBg : Brushes.Transparent;
            kv.Value.BorderBrush = isActive ? activeBorder : inactiveBorder;
            if (_advAppsCategoryNameMap.TryGetValue(kv.Key, out var nameTb))
            {
                nameTb.Foreground = isActive ? accentFg : defaultName;
                nameTb.FontWeight = isActive ? FontWeight.Bold : FontWeight.SemiBold;
            }
            if (_advAppsCategoryCountMap.TryGetValue(kv.Key, out var countTb))
                countTb.Foreground = isActive ? accentFg : defaultCount;
        }
    }

    /// <summary>Switch active category. Persists via
    /// <see cref="AndroidStorage.SetApplicationsActiveCategory"/> so the next
    /// open lands on the same category.
    /// <para>Bug #2 (2026-05-11) — mobile redesign dropped the placeholder
    /// surface (the pre-fix 2-pane layout swapped placeholder ↔ scope body
    /// when id was empty). The scope body is now the only content surface,
    /// always visible; chip strip drives the filter.</para></summary>
    private void SetActiveAppCategory(string? id)
    {
        // Bug-AND-019 — intercept the tap for pending-delete confirm.
        // If we committed a delete the sidebar was rebuilt; activation
        // for the deleted id should be skipped (Custom catch-all
        // already became active inside the consume helper).
        if (ConsumePendingDeleteIfMatches(id)) return;
        _advAppsActiveCategoryId = id;
        AndroidStorage.SetApplicationsActiveCategory(id);
        StyleActiveCategoryRow();
        ApplyAppPickerFilter();
    }

    /// <summary>
    /// Bug-AND-019 (2026-05-16) — track which user-defined custom
    /// category was just long-pressed. The chip enters an inline
    /// "tap-to-confirm-delete" state; a second tap on the same chip
    /// drops it from <see cref="_advAppsCustomCategories"/>, a tap on
    /// anything else (different chip, app row, etc.) cancels.
    /// </summary>
    private string? _pendingDeleteCategoryId;

    private void PromptDeleteCustomCategory(string id)
    {
        if (string.IsNullOrEmpty(id)) return;
        if (!IsUserDefinedCategory(id)) return;
        _pendingDeleteCategoryId = id;
        // Repaint the chip's text to surface the inline confirmation
        // ("✗ Tap again to delete"). Active state still applies so the
        // chip stays visually anchored.
        if (_advAppsCategoryNameMap.TryGetValue(id, out var tb) && tb is not null)
        {
            tb.Text = "✗ " + Localization.AndroidDeleteCategoryConfirm;
            tb.Foreground = GetBrush("DangerFgBrush");
        }
    }

    /// <summary>Called by SetActiveAppCategory when the user taps a chip.
    /// If a delete is pending and the user tapped the SAME chip, commit
    /// the delete. Otherwise (different chip or different surface),
    /// cancel and revert the inline state.</summary>
    private bool ConsumePendingDeleteIfMatches(string? tappedId)
    {
        var pending = _pendingDeleteCategoryId;
        if (string.IsNullOrEmpty(pending)) return false;
        _pendingDeleteCategoryId = null;
        if (!string.Equals(pending, tappedId, System.StringComparison.OrdinalIgnoreCase))
        {
            // Cancel — repaint the original label on the previously
            // pending chip via a sidebar rebuild (cheaper than tracking
            // original label).
            RebuildAppCategorySidebar();
            return false;
        }
        // Commit delete.
        try
        {
            _advAppsCustomCategories.RemoveAll(c =>
                string.Equals(c.Name, pending, System.StringComparison.OrdinalIgnoreCase));
            AndroidStorage.SetCustomCategories(_advAppsCustomCategories);
            if (string.Equals(_advAppsActiveCategoryId, pending, System.StringComparison.OrdinalIgnoreCase))
                _advAppsActiveCategoryId = AndroidCategoryDefaults.CustomCatchAllId;
            RebuildAppCategorySidebar();
            ApplyAppPickerFilter();
        }
        catch (System.Exception ex)
        {
            global::Android.Util.Log.Warn("VpnRouter.Categories",
                $"Bug-AND-019 delete failed: {ex.GetType().Name}: {ex.Message}");
        }
        return true;
    }

    /// <summary>Add a user-created custom category from the sidebar's
    /// "+ New category" form. Trims input, ignores duplicates, persists, and
    /// auto-activates the new row.</summary>
    private void OnAdvAppsAddCategoryClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var raw = _advAppsNewCategoryInput?.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(raw)) return;

        // Skip dupes: built-in id collision OR existing custom name.
        if (AndroidCategoryDefaults.Find(raw) is not null) return;
        foreach (var existing in _advAppsCustomCategories)
            if (string.Equals(existing.Name, raw, System.StringComparison.OrdinalIgnoreCase))
                return;

        _advAppsCustomCategories.Add(new VPNRouter.Core.Models.CustomCategory
        {
            Name = raw,
            Apps = new List<string>(),
            Enabled = true,
        });
        AndroidStorage.SetCustomCategories(_advAppsCustomCategories);

        if (_advAppsNewCategoryInput is not null) _advAppsNewCategoryInput.Text = string.Empty;
        RebuildAppCategorySidebar();
        SetActiveAppCategory(raw);
    }

    private void OnMenuCopyLogPathClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_kebabPopup is not null) _kebabPopup.IsOpen = false;
        try
        {
            // A6 (2026-06-13) — surface FilesDir/singbox.log (private sandbox,
            // Bug-AND-011), the path the service actually writes. Was the
            // GetExternalFilesDir path, so the value copied to the clipboard
            // pointed at a file that never exists post-Bug-AND-011.
            var logPath = AndroidDiagnosticsExporter.ResolveSingboxLogPath();
            if (logPath is null)
            {
                ShowMenuFeedback(Localization.SaveStatusUnknown);
                return;
            }
            CopyToClipboard("singbox-log-path", logPath);
            ShowMenuFeedback(logPath);
        }
        catch (Exception ex)
        {
            ShowMenuFeedback($"Error: {ex.GetType().Name}");
        }
    }

    private void OnMenuUpdateCheckClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_kebabPopup is not null) _kebabPopup.IsOpen = false;
        // v2.32.0 (2026-05-07) — wires the kebab item to the real
        // Android auto-update flow (AndroidUpdater + REQUEST_INSTALL_PACKAGES).
        // Pre-2.32.0 this just showed "coming in next release" toast.
        _ = RunUpdateCheckAsync(manual: true);
    }

    private void OnMenuResetSettingsClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_resetConfirmPending)
        {
            // Second tap — actually wipe.
            if (_kebabPopup is not null) _kebabPopup.IsOpen = false;
            _resetConfirmPending = false;
            if (_menuResetSettingsItem is not null)
                _menuResetSettingsItem.Content = Localization.MenuItemResetSettings;

            try
            {
                AndroidStorage.SetVlessUri(null);
                AndroidStorage.SetSubscriptionUrl(null);
                AndroidStorage.SetServers(null);
                AndroidStorage.SetSelectedServerName(null);
                // Theme + language preserved (those are UI prefs, not
                // routing config) — same behaviour as desktop "Reset
                // routing settings" not nuking theme.
                ShowMenuFeedback(Localization.MenuItemResetDone);
            }
            catch (Exception ex)
            {
                ShowMenuFeedback($"Error: {ex.GetType().Name}");
            }
            return;
        }

        // First tap — show confirm prompt inline. Don't dismiss the
        // popup so the user can read the warning + tap the row again.
        _resetConfirmPending = true;
        if (_menuResetSettingsItem is not null)
            _menuResetSettingsItem.Content = Localization.MenuItemResetConfirm;
    }

    private void OnMenuRepoClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_kebabPopup is not null) _kebabPopup.IsOpen = false;
        try
        {
            var intent = new global::Android.Content.Intent(global::Android.Content.Intent.ActionView,
                global::Android.Net.Uri.Parse("https://github.com/PavelLizunov/VPNRouter"));
            intent.SetFlags(global::Android.Content.ActivityFlags.NewTask);
            global::Android.App.Application.Context.StartActivity(intent);
        }
        catch (Exception ex)
        {
            ShowMenuFeedback($"Error: {ex.GetType().Name}");
        }
    }
}
