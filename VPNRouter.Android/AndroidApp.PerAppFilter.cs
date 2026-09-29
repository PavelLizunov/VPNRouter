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
    private void OnTunnelModeRadioChanged(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var splitOn = _splitRadio?.IsChecked == true;

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

        if (_splitRadio?.Tag is StackPanel perAppStack)
        {
            perAppStack.IsVisible = splitOn;
        }

        UpdatePerAppFormCountLabel();
    }

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
        OpenAdvancedShell(AdvancedTab.Applications);
    }

    private async void ReseedAppPickerTabState()
    {
        _appPickerSelected = new HashSet<string>(AndroidStorage.GetPerAppPackages(),
                                                 System.StringComparer.OrdinalIgnoreCase);

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
        AndroidStorage.SetPerAppMode(_appPickerMode);
        AndroidStorage.SetPerAppLastMode(_appPickerMode);
        UpdatePerAppFormCountLabel();
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
        if (_appPickerSearch == null)
        {
            ApplyAppPickerFilter();
            return;
        }

        if (_appPickerSearch.Tag is not DispatcherTimer timer)
        {
            timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                ApplyAppPickerFilter();
            };
            _appPickerSearch.Tag = timer;
        }

        timer.Stop();
        timer.Start();
    }

    private void ApplyAppPickerFilter()
    {
        if (_appPickerList is null) return;
        var search = _appPickerSearch?.Text?.Trim() ?? string.Empty;

        IEnumerable<AppListLoader.AppEntry> scoped = ScopeAppsToActiveCategory(_appPickerCache);

        var filtered = string.IsNullOrEmpty(search)
            ? scoped
            : scoped.Where(a =>
                a.Label.Contains(search, System.StringComparison.OrdinalIgnoreCase)
                || a.PackageName.Contains(search, System.StringComparison.OrdinalIgnoreCase));

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
        if (_appPickerShowingCount is not null)
        {
            _appPickerShowingCount.Text = string.Format(
                Localization.PerAppShowingCount,
                selectedRows.Count + availableRows.Count);
        }
    }

    private IEnumerable<AppListLoader.AppEntry> ScopeAppsToActiveCategory(IEnumerable<AppListLoader.AppEntry> source)
    {
        if (string.IsNullOrEmpty(_advAppsActiveCategoryId))
            return System.Linq.Enumerable.Empty<AppListLoader.AppEntry>();

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

            AndroidStorage.SetPerAppPackages(_appPickerSelected);

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
        return rowBorder;
    }

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

    private Control BuildAppPickerTabContent()
    {
        var chipWrapHost = new WrapPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Margin = new Thickness(8, 4, 8, 4),
        };
        _advAppsCategoryWrapHost = chipWrapHost;

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

        var scopeBody = BuildAppPickerScopeBody();
        _advAppsRightPaneScopeContainer = new Border
        {
            Child = scopeBody,
            IsVisible = true,
        };

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

    private void RebuildAppCategorySidebar()
    {
        var host = _advAppsCategoryWrapHost;
        if (host is null) return;
        host.Children.Clear();
        _advAppsCategoryRowMap.Clear();
        _advAppsCategoryCountMap.Clear();
        _advAppsCategoryNameMap.Clear();

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

        foreach (var def in AndroidCategoryDefaults.All)
        {
            if (AndroidCategoryDefaults.IsCustomCatchAll(def.Id)) continue;
            var displayName = Localization.GroupDisplayName(def.Id);
            var row = MakeAppsCategoryRow(def.Id, displayName, isCustom: false);
            host.Children.Add(row);
        }

        foreach (var cat in _advAppsCustomCategories)
        {
            if (string.IsNullOrWhiteSpace(cat.Name)) continue;
            var row = MakeAppsCategoryRow(cat.Name, cat.Name, isCustom: true);
            host.Children.Add(row);
        }

        UpdateAllCategoryCounts();
        StyleActiveCategoryRow();
    }

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
        border.PointerPressed += (_, _) => SetActiveAppCategory(id);

        if (isCustom)
        {
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

    private int ComputeCustomCategoryCount(VPNRouter.Core.Models.CustomCategory cat)
    {
        if (cat.Apps is null || cat.Apps.Count == 0) return 0;
        int hits = 0;
        foreach (var pkg in cat.Apps)
            if (_appPickerSelected.Contains(pkg)) hits++;
        return hits;
    }

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

    private void SetActiveAppCategory(string? id)
    {
        if (ConsumePendingDeleteIfMatches(id)) return;
        _advAppsActiveCategoryId = id;
        AndroidStorage.SetApplicationsActiveCategory(id);
        StyleActiveCategoryRow();
        ApplyAppPickerFilter();
    }

    private string? _pendingDeleteCategoryId;

    private void PromptDeleteCustomCategory(string id)
    {
        if (string.IsNullOrEmpty(id)) return;
        if (!IsUserDefinedCategory(id)) return;
        _pendingDeleteCategoryId = id;
        if (_advAppsCategoryNameMap.TryGetValue(id, out var tb) && tb is not null)
        {
            tb.Text = "✗ " + Localization.AndroidDeleteCategoryConfirm;
            tb.Foreground = GetBrush("DangerFgBrush");
        }
    }

    private bool ConsumePendingDeleteIfMatches(string? tappedId)
    {
        var pending = _pendingDeleteCategoryId;
        if (string.IsNullOrEmpty(pending)) return false;
        _pendingDeleteCategoryId = null;
        if (!string.Equals(pending, tappedId, System.StringComparison.OrdinalIgnoreCase))
        {
            RebuildAppCategorySidebar();
            return false;
        }
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

    private void OnAdvAppsAddCategoryClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var raw = _advAppsNewCategoryInput?.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(raw)) return;

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


    private void OnMenuUpdateCheckClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_kebabPopup is not null) _kebabPopup.IsOpen = false;
        _ = RunUpdateCheckAsync(manual: true);
    }

    private void OnMenuResetSettingsClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_resetConfirmPending)
        {
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
                ShowMenuFeedback(Localization.MenuItemResetDone);
            }
            catch (Exception ex)
            {
                ShowMenuFeedback($"Error: {ex.GetType().Name}");
            }
            return;
        }

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
