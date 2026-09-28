#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VPNRouter.App.Localization;
using VPNRouter.Core;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.App.ViewModels;

public partial class MainWindowViewModel
{
    private void LoadApps()
    {
        UnwireAllAppGroups();
        AppGroups.Clear();
        BypassAppGroups.Clear();

        var activeProfileStr = _settings.ActiveProfile ?? "";
        var isFirstLaunch = string.IsNullOrWhiteSpace(activeProfileStr);

        var activeProfiles = activeProfileStr
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        var excludedSet = new HashSet<string>(
            (_settings.ExcludedApps ?? new()).Select(s => StripExe(s ?? string.Empty)),
            StringComparer.OrdinalIgnoreCase);

        _settings.App.RoutingAppsInclude ??= new List<string>();
        _settings.App.RoutingAppsExclude ??= new List<string>();
        AppsListEditorMode = string.Equals(
            _settings.App.RoutingAppsMode, "exclude", StringComparison.OrdinalIgnoreCase)
            ? "exclude"
            : "include";

        var legacyIncludeNames = ComputeLegacyEffectiveIncludeNames(
            activeProfiles, excludedSet, isFirstLaunch);

        if (!_settings.App.RoutingAppsIncludeInitialized
            && _settings.App.RoutingAppsInclude.Count == 0
            && legacyIncludeNames.Count > 0)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var n in legacyIncludeNames)
            {
                if (string.IsNullOrWhiteSpace(n)) continue;
                if (seen.Add(n))
                    _settings.App.RoutingAppsInclude.Add(n);
            }
            _logger?.Information(
                "[VM] AM-3: seeded RoutingAppsInclude with {Count} entries " +
                "from legacy profile/custom state on first load",
                _settings.App.RoutingAppsInclude.Count);
        }
        if (!_settings.App.RoutingAppsIncludeInitialized)
            _settings.App.RoutingAppsIncludeInitialized = true;

        var profileFile = OperatingSystem.IsMacOS() ? "default-macos.json"
                        : OperatingSystem.IsLinux() ? "default-linux.json"
                        : "default.json";
        var profilePath = Path.Combine(AppContext.BaseDirectory, "profiles", profileFile);
        if (!File.Exists(profilePath))
            profilePath = Path.Combine(AppPaths.ProfilesDir, profileFile);
        if (!File.Exists(profilePath))
            profilePath = Path.Combine(AppPaths.ProfilesDir, "default.json");

        if (File.Exists(profilePath))
        {
            try
            {
                var json = File.ReadAllText(profilePath);
                var collection = JsonSerializer.Deserialize<ProfileCollection>(json, ProfileManager.SafeJsonOptions);
                if (collection?.Profiles != null)
                {
                    foreach (var profile in collection.Profiles)
                    {
                        var isActive = isFirstLaunch || activeProfiles.Any(p =>
                            p.Equals(profile.Name, StringComparison.OrdinalIgnoreCase));

                        var includeGroup = new AppGroupViewModel(profile.Name, profile.Description, isActive);

                        foreach (var proc in profile.Processes)
                        {
                            var name = StripExe(proc.Name);
                            var appChecked = isActive && !excludedSet.Contains(name);
                            includeGroup.Apps.Add(CreateIncludeAppItem(name, appChecked));
                        }

                        if (_settings.CustomGroupApps != null
                            && _settings.CustomGroupApps.TryGetValue(profile.Name, out var extras))
                        {
                            foreach (var extra in extras)
                            {
                                if (string.IsNullOrWhiteSpace(extra)) continue;
                                var extraName = StripExe(extra);
                                if (includeGroup.Apps.Any(a => a.ProcessName.Equals(extraName, StringComparison.OrdinalIgnoreCase)))
                                    continue;
                                var appChecked = isActive && !excludedSet.Contains(extraName);
                                includeGroup.Apps.Add(CreateIncludeAppItem(extraName, appChecked, isCustom: true));
                            }
                        }

                        AppGroups.Add(includeGroup);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.Warning(ex, "Failed to load profiles");
            }
        }

        var bypassProfilePath = OperatingSystem.IsWindows()
            ? ResolveBundledProfilePath("bypass-windows.json", fallbackToDefault: false)
            : null;
        foreach (var profile in LoadProfileCollection(bypassProfilePath))
        {
            var excludeGroup = new AppGroupViewModel(profile.Name, profile.Description, false);
            foreach (var proc in profile.Processes)
                excludeGroup.Apps.Add(CreateExcludeAppItem(StripExe(proc.Name), false));
            BypassAppGroups.Add(excludeGroup);
        }

        var customApps = _settings.CustomApps ?? new();
        var customGroup = new AppGroupViewModel("Custom Apps", "Your custom applications", true) { IsCustomGroup = true, IsExpanded = true };
        var bypassCustomGroup = new AppGroupViewModel("Custom Apps", "Your custom applications", false) { IsCustomGroup = true, IsExpanded = true };
        foreach (var app in customApps)
        {
            if (!string.IsNullOrEmpty(app))
            {
                var name = StripExe(app);
                customGroup.Apps.Add(CreateIncludeAppItem(name, true, isCustom: true));
                bypassCustomGroup.Apps.Add(CreateExcludeAppItem(name, false, isCustom: true));
            }
        }
        AppGroups.Add(customGroup);
        BypassAppGroups.Add(bypassCustomGroup);

        foreach (var cat in _settings.CustomCategories ?? new())
        {
            if (string.IsNullOrWhiteSpace(cat.Name)) continue;
            var group = new AppGroupViewModel(cat.Name, "", cat.Enabled) { IsCustomCategory = true };
            var bypassGroup = new AppGroupViewModel(cat.Name, "", false) { IsCustomCategory = true };
            foreach (var app in cat.Apps ?? new())
            {
                if (string.IsNullOrWhiteSpace(app)) continue;
                var name = StripExe(app);
                group.Apps.Add(CreateIncludeAppItem(name, cat.Enabled, isCustom: true));
                bypassGroup.Apps.Add(CreateExcludeAppItem(name, false, isCustom: true));
            }
            AppGroups.Add(group);
            BypassAppGroups.Add(bypassGroup);
        }

        SelectedAppGroup ??= AppGroups.FirstOrDefault();
        SelectedBypassAppGroup ??= BypassAppGroups.FirstOrDefault();
        _appsLoaded = true;
        WireAppChangeTracking();
    }

    internal static string? ResolveBundledProfilePath(string profileFile, bool fallbackToDefault)
    {
        var appPath = Path.Combine(AppContext.BaseDirectory, "profiles", profileFile);
        if (File.Exists(appPath)) return appPath;

        var userPath = Path.Combine(AppPaths.ProfilesDir, profileFile);
        if (File.Exists(userPath)) return userPath;

        if (!fallbackToDefault) return null;
        var fallback = Path.Combine(AppPaths.ProfilesDir, "default.json");
        return File.Exists(fallback) ? fallback : null;
    }

    private IEnumerable<Profile> LoadProfileCollection(string? profilePath)
    {
        if (string.IsNullOrWhiteSpace(profilePath) || !File.Exists(profilePath))
            yield break;

        ProfileCollection? collection;
        try
        {
            var json = File.ReadAllText(profilePath);
            collection = JsonSerializer.Deserialize<ProfileCollection>(json, ProfileManager.SafeJsonOptions);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to load profiles from {Path}", profilePath);
            yield break;
        }

        foreach (var profile in collection?.Profiles ?? new())
            yield return profile;
    }

    private AppItemViewModel CreateBridgedAppItem(
        string processName, bool legacyChecked, bool isCustom = false)
    {
        var item = new AppItemViewModel(processName, legacyChecked, isCustom);
        item.ReadMode = IsAppCheckedInCurrentMode;
        item.WriteMode = SetAppCheckedInCurrentMode;
        return item;
    }

    private AppItemViewModel CreateIncludeAppItem(
        string processName, bool legacyChecked, bool isCustom = false)
    {
        var item = new AppItemViewModel(processName, legacyChecked, isCustom);
        item.ReadMode = IsAppCheckedInIncludeList;
        item.WriteMode = SetAppCheckedInIncludeList;
        return item;
    }

    private AppItemViewModel CreateExcludeAppItem(
        string processName, bool legacyChecked, bool isCustom = false)
    {
        var item = new AppItemViewModel(processName, legacyChecked, isCustom);
        item.ReadMode = IsAppCheckedInExcludeList;
        item.WriteMode = SetAppCheckedInExcludeList;
        return item;
    }

    private List<string> ComputeLegacyEffectiveIncludeNames(
        string[] activeProfiles, HashSet<string> excludedSet, bool isFirstLaunch)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void TryAdd(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            if (excludedSet.Contains(name)) return;
            if (seen.Add(name))
                result.Add(name);
        }

        var profileFile = OperatingSystem.IsMacOS() ? "default-macos.json"
                        : OperatingSystem.IsLinux() ? "default-linux.json"
                        : "default.json";
        var profilePath = Path.Combine(AppContext.BaseDirectory, "profiles", profileFile);
        if (!File.Exists(profilePath))
            profilePath = Path.Combine(AppPaths.ProfilesDir, profileFile);
        if (!File.Exists(profilePath))
            profilePath = Path.Combine(AppPaths.ProfilesDir, "default.json");

        if (File.Exists(profilePath))
        {
            try
            {
                var json = File.ReadAllText(profilePath);
                var collection = JsonSerializer.Deserialize<ProfileCollection>(json, ProfileManager.SafeJsonOptions);
                if (collection?.Profiles != null)
                {
                    foreach (var profile in collection.Profiles)
                    {
                        var isActive = isFirstLaunch || activeProfiles.Any(p =>
                            p.Equals(profile.Name, StringComparison.OrdinalIgnoreCase));
                        if (!isActive) continue;
                        foreach (var proc in profile.Processes)
                            TryAdd(StripExe(proc.Name));

                        if (_settings.CustomGroupApps != null
                            && _settings.CustomGroupApps.TryGetValue(profile.Name, out var extras))
                        {
                            foreach (var extra in extras)
                                TryAdd(StripExe(extra ?? string.Empty));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.Warning(ex, "[VM] AM-3 legacy seed: failed to read profiles");
            }
        }

        foreach (var a in _settings.CustomApps ?? new())
            TryAdd(StripExe(a ?? string.Empty));

        foreach (var cat in _settings.CustomCategories ?? new())
        {
            if (!cat.Enabled) continue;
            foreach (var a in cat.Apps ?? new())
                TryAdd(StripExe(a ?? string.Empty));
        }

        return result;
    }

    private bool _appChangeTrackingWired;

    private void WireAppChangeTracking()
    {
        if (!_appChangeTrackingWired)
        {
            AppGroups.CollectionChanged += OnAppGroupsCollectionChanged;
            BypassAppGroups.CollectionChanged += OnAppGroupsCollectionChanged;
            _appChangeTrackingWired = true;
        }

        foreach (var group in AllAppGroups())
        {
            group.BeginBatchUpdate = BeginBatchUpdate;
            group.PropertyChanged -= OnAppGroupPropertyChanged;
            group.PropertyChanged += OnAppGroupPropertyChanged;
            group.Apps.CollectionChanged -= OnAppsCollectionChanged;
            group.Apps.CollectionChanged += OnAppsCollectionChanged;
            foreach (var app in group.Apps)
            {
                app.PropertyChanged -= OnAppItemPropertyChanged;
                app.PropertyChanged += OnAppItemPropertyChanged;
            }
        }
    }

    private IEnumerable<AppGroupViewModel> AllAppGroups() => AppGroups.Concat(BypassAppGroups);

    private void OnAppGroupsCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (_isLoadingUI) return;
        if (e.NewItems != null)
            foreach (AppGroupViewModel g in e.NewItems)
            {
                g.BeginBatchUpdate = BeginBatchUpdate;
                g.PropertyChanged -= OnAppGroupPropertyChanged;
                g.PropertyChanged += OnAppGroupPropertyChanged;
                g.Apps.CollectionChanged -= OnAppsCollectionChanged;
                g.Apps.CollectionChanged += OnAppsCollectionChanged;
                foreach (var a in g.Apps)
                {
                    a.PropertyChanged -= OnAppItemPropertyChanged;
                    a.PropertyChanged += OnAppItemPropertyChanged;
                }
            }
        MarkRoutingSettingsChanged();
    }

    private void OnAppGroupPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_isLoadingUI || IsBatchUpdating) return;
        if (e.PropertyName == nameof(AppGroupViewModel.IsChecked))
        {
            MarkRoutingSettingsChanged();
            try { SaveSettings(); }
            catch (Exception ex) { _logger?.Warning(ex, "[VM] Auto-save on AppGroup change failed"); }
        }
    }

    private void UnwireAllAppGroups()
    {
        foreach (var group in AllAppGroups())
        {
            group.PropertyChanged -= OnAppGroupPropertyChanged;
            group.Apps.CollectionChanged -= OnAppsCollectionChanged;
            foreach (var app in group.Apps)
                app.PropertyChanged -= OnAppItemPropertyChanged;
        }
    }

    private void OnAppsCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (_isLoadingUI) return;
        if (e.NewItems != null)
            foreach (AppItemViewModel a in e.NewItems)
            {
                a.PropertyChanged -= OnAppItemPropertyChanged;
                a.PropertyChanged += OnAppItemPropertyChanged;
            }
        MarkRoutingSettingsChanged();
    }

    private void OnAppItemPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_isLoadingUI || IsBatchUpdating) return;
        if (e.PropertyName == nameof(AppItemViewModel.IsChecked))
        {
            MarkRoutingSettingsChanged();
            if (sender is AppItemViewModel item && item.WriteMode != null) return;

            try { SaveSettings(); }
            catch (Exception ex) { _logger?.Warning(ex, "[VM] Auto-save on AppItem change failed"); }
        }
    }

    private static string StripExe(string name)
    {
        name = name.Trim();
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
        {
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                name = name[..^4];
        }
        return name;
    }

    [ObservableProperty] private string _newCategoryName = string.Empty;

    [RelayCommand]
    private void AddCategory()
    {
        var name = NewCategoryName?.Trim();
        if (string.IsNullOrWhiteSpace(name)) return;
        name = new string(name.Where(c => c != '%' && c != '\\' && c != '/' && c != '"').ToArray()).Trim();
        if (string.IsNullOrWhiteSpace(name)) return;
        if (AllAppGroups().Any(g => g.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) return;

        var group = new AppGroupViewModel(name!, "", isChecked: true) { IsCustomCategory = true };
        var bypassGroup = new AppGroupViewModel(name!, "", isChecked: false) { IsCustomCategory = true };
        AppGroups.Add(group);
        BypassAppGroups.Add(bypassGroup);
        SelectedActiveAppGroup = IsAppsListEditorExclude ? bypassGroup : group;
        NewCategoryName = string.Empty;
        SaveSettings();
    }

    [RelayCommand]
    private void RemoveCategory(AppGroupViewModel? group)
    {
        if (group == null || !group.IsCustomCategory) return;
        var removedNames = group.Apps.Select(a => a.ProcessName).ToList();
        var peerGroups = AllAppGroups()
            .Where(g => g.IsCustomCategory && g.Name.Equals(group.Name, StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var peer in peerGroups)
            (AppGroups.Contains(peer) ? AppGroups : BypassAppGroups).Remove(peer);
        foreach (var name in removedNames)
            ScrubRoutingForProcess(name);
        if (SelectedAppGroup == group)
            SelectedAppGroup = AppGroups.FirstOrDefault();
        if (SelectedBypassAppGroup == group)
            SelectedBypassAppGroup = BypassAppGroups.FirstOrDefault();
        SaveSettings();
    }

    private void ScrubRoutingForProcess(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        var bare = StripExe(name);
        bool Match(string p) =>
            string.Equals(p, name, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(StripExe(p), bare, StringComparison.OrdinalIgnoreCase);

        var includeSurvives = AppGroups.SelectMany(g => g.Apps)
            .Any(a => a.IsChecked && Match(a.ProcessName));
        var excludeSurvives = BypassAppGroups.SelectMany(g => g.Apps)
            .Any(a => a.IsChecked && Match(a.ProcessName));

        if (!includeSurvives)
            _settings.App.RoutingAppsInclude?.RemoveAll(Match);
        if (!excludeSurvives)
            _settings.App.RoutingAppsExclude?.RemoveAll(Match);
    }

    [RelayCommand]
    private void AddCustomApp(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return;

        var name = VPNRouter.Core.Services.RoutingAppListEditor.NormalizeManualProcessName(
            processName, OperatingSystem.IsWindows());
        if (name == null) return;

        var selected = SelectedActiveAppGroup;
        var target = selected != null &&
                     (selected.IsCustomCategory || selected.Name == "Custom Apps")
            ? selected
            : ActiveAppGroups.FirstOrDefault(g => g.Name == "Custom Apps");
        if (target == null)
        {
            target = new AppGroupViewModel("Custom Apps", "Your custom applications", !IsAppsListEditorExclude) { IsCustomGroup = true };
            ActiveAppGroups.Add(target);
        }

        var existing = target.Apps.FirstOrDefault(a => a.ProcessName.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            existing.IsChecked = true;
            SaveSettings();
            return;
        }

        var newItem = IsAppsListEditorExclude
            ? CreateExcludeAppItem(name, legacyChecked: false, isCustom: true)
            : CreateIncludeAppItem(name, legacyChecked: false, isCustom: true);
        target.Apps.Add(newItem);
        newItem.IsChecked = true;

        var mirrorGroups = IsAppsListEditorExclude ? AppGroups : BypassAppGroups;
        var mirror = mirrorGroups.FirstOrDefault(g => g.Name.Equals(target.Name, StringComparison.OrdinalIgnoreCase));
        if (mirror == null)
        {
            mirror = new AppGroupViewModel(target.Name, target.Description, isChecked: false)
            {
                IsCustomGroup = target.IsCustomGroup,
                IsCustomCategory = target.IsCustomCategory,
                IsExpanded = target.IsExpanded,
            };
            mirrorGroups.Add(mirror);
        }
        if (!mirror.Apps.Any(a => a.ProcessName.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            mirror.Apps.Add(IsAppsListEditorExclude
                ? CreateIncludeAppItem(name, legacyChecked: false, isCustom: true)
                : CreateExcludeAppItem(name, legacyChecked: false, isCustom: true));
        }
        SaveSettings();
    }

    [RelayCommand]
    private async Task ImportSteamGames()
    {
        if (!IsAppsListEditorExclude)
            AppsListEditorMode = "exclude";

        using var _ = BeginBatchUpdate();
        var games = await Task.Run(() => Services.SteamLibraryScanner.FindInstalledGames().ToList());
        var added = 0;
        foreach (var game in games)
        {
            if (AddCustomAppCandidate(game.ProcessName))
                added++;
        }

        if (added > 0)
        {
            ShowRulesToast(IsRussian
                ? $"Steam: найдено {added} .exe"
                : $"Steam: found {added} .exe files");
        }
        else if (games.Count == 0)
        {
            ShowRulesToast(Strings.SteamGamesNotFound);
        }
        else
        {
            ShowRulesToast(Strings.NoNewSteamGamesFound);
        }
    }

    private bool AddCustomAppCandidate(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return false;

        var name = StripExe(processName.Trim());
        var includeCustom = AppGroups.FirstOrDefault(g => g.Name == "Custom Apps");
        var bypassCustom = BypassAppGroups.FirstOrDefault(g => g.Name == "Custom Apps");
        if (includeCustom == null || bypassCustom == null) return false;

        var added = false;
        if (!includeCustom.Apps.Any(a => a.ProcessName.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            includeCustom.Apps.Add(CreateIncludeAppItem(name, false, isCustom: true));
            added = true;
        }

        if (!bypassCustom.Apps.Any(a => a.ProcessName.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            bypassCustom.Apps.Add(CreateExcludeAppItem(name, false, isCustom: true));
            added = true;
        }

        var exclude = _settings.App.RoutingAppsExclude ??= new List<string>();
        if (!exclude.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            exclude.Add(name);
            bypassCustom.Apps
                .FirstOrDefault(a => a.ProcessName.Equals(name, StringComparison.OrdinalIgnoreCase))
                ?.RaiseIsCheckedChanged();
            added = true;
        }

        return added;
    }

#if PLATFORM_WINDOWS
    internal void RouteAppFromShell(string? rawPath, string? category = null)
    {
        var exeName = OperatingSystem.IsWindows()
            ? Services.ShortcutResolver.ResolveToExeName(rawPath, _logger)
            : null;

        if (string.IsNullOrWhiteSpace(exeName))
        {
            _logger.Warning("[ShellAdd] could not resolve a routable .exe from {Path}", rawPath);
            ShowRulesToast(IsRussian
                ? "Это Steam/Store-ярлык — процесс не определить. Запусти приложение и добавь его в разделе «Приложения»."
                : "This is a Steam/Store shortcut — no process to read. Launch the app, then add it in the Apps section.");
            return;
        }

        var routed = _settings.App.RoutingAppsInclude ?? new List<string>();
        if (routed.Any(e => string.Equals(e, exeName, StringComparison.OrdinalIgnoreCase)))
        {
            _logger.Information("[ShellAdd] {Exe} already routed — no-op", exeName);
            ShowRulesToast(IsRussian ? $"{exeName} уже в списке VPN" : $"{exeName} already routed");
            return;
        }

        var target = !string.IsNullOrWhiteSpace(category)
            ? AppGroups.FirstOrDefault(g => g.Name.Equals(category, StringComparison.OrdinalIgnoreCase))
            : null;
        target ??= AppGroups.FirstOrDefault(g => g.Name == "Custom Apps");

        var prevSelected = SelectedAppGroup;
        var prevEditorMode = AppsListEditorMode;
        AppsListEditorMode = "include";
        SelectedAppGroup = target;
        try
        {
            AddCustomApp(exeName);
            var landedItem = target?.Apps.FirstOrDefault(
                a => string.Equals(a.ProcessName, exeName, StringComparison.OrdinalIgnoreCase));
            landedItem ??= AppGroups.SelectMany(g => g.Apps).FirstOrDefault(
                a => string.Equals(a.ProcessName, exeName, StringComparison.OrdinalIgnoreCase));
            if (landedItem != null && !landedItem.IsChecked)
                landedItem.IsChecked = true;
        }
        finally
        {
            SelectedAppGroup = prevSelected;
            AppsListEditorMode = prevEditorMode;
        }

        bool nowRouted = (_settings.App.RoutingAppsInclude ?? new List<string>())
            .Any(e => string.Equals(e, exeName, StringComparison.OrdinalIgnoreCase));
        if (!nowRouted)
        {
            _logger.Warning("[ShellAdd] {Exe} did not end up routed (unexpected) — reporting failure", exeName);
            ShowRulesToast(IsRussian ? $"Не удалось добавить {exeName}" : $"Couldn't add {exeName}");
            return;
        }

        var landed = target?.Name ?? "Custom Apps";
        bool namedCategory = !string.Equals(landed, "Custom Apps", StringComparison.OrdinalIgnoreCase);
        _logger.Information("[ShellAdd] {Exe} added to '{Group}' + routed via VPN", exeName, landed);
        ShowRulesToast(namedCategory
            ? (IsRussian ? $"{exeName} → через VPN ({landed})" : $"{exeName} → routed via VPN ({landed})")
            : (IsRussian ? $"{exeName} → через VPN" : $"{exeName} → routed via VPN"));
    }

    internal void UnrouteAppFromShell(string? rawPath)
    {
        var exeName = OperatingSystem.IsWindows()
            ? Services.ShortcutResolver.ResolveToExeName(rawPath, _logger)
            : null;

        if (string.IsNullOrWhiteSpace(exeName))
        {
            _logger.Warning("[ShellRemove] could not resolve a routable .exe from {Path}", rawPath);
            ShowRulesToast(IsRussian
                ? "Это Steam/Store-ярлык — процесс не определить. Запусти приложение и добавь его в разделе «Приложения»."
                : "This is a Steam/Store shortcut — no process to read. Launch the app, then add it in the Apps section.");
            return;
        }

        var routed = _settings.App.RoutingAppsInclude ?? new List<string>();
        bool wasRouted = routed.Any(e => string.Equals(e, exeName, StringComparison.OrdinalIgnoreCase));

        var matches = new List<(AppGroupViewModel Group, AppItemViewModel Item)>();
        foreach (var group in AppGroups)
            foreach (var item in group.Apps)
                if (string.Equals(item.ProcessName, exeName, StringComparison.OrdinalIgnoreCase))
                    matches.Add((group, item));

        foreach (var (group, item) in matches)
        {
            try { item.IsChecked = false; } catch { }
            group.Apps.Remove(item);
        }
        bool removedItem = matches.Count > 0;

        VPNRouter.Core.Services.RoutingAppListEditor.TryRemoveProcessName(_settings, exeName);

        SaveSettings();

        if (wasRouted || removedItem)
        {
            _logger.Information("[ShellRemove] {Exe} removed from VPN routing ({N} group instance(s))", exeName, matches.Count);
            ShowRulesToast(IsRussian ? $"{exeName} убрано из VPN" : $"{exeName} removed from VPN");
        }
        else
        {
            _logger.Information("[ShellRemove] {Exe} was not routed — no-op", exeName);
            ShowRulesToast(IsRussian ? $"{exeName} не было в списке VPN" : $"{exeName} wasn't routed");
        }
    }
#endif

    [RelayCommand]
    private void RemoveCustomApps()
    {
        var customGroup = ActiveAppGroups.FirstOrDefault(g => g.Name == "Custom Apps");
        if (customGroup == null) return;

        var toRemove = customGroup.Apps.Where(a => a.IsChecked).ToList();
        foreach (var app in toRemove)
            RemoveCustomApp(app);
    }

    [RelayCommand]
    private void RemoveCustomApp(AppItemViewModel? app)
    {
        if (app == null) return;
        var matches = AllAppGroups()
            .SelectMany(g => g.Apps.Select(a => new { Group = g, App = a }))
            .Where(x => x.App.IsCustom &&
                        x.App.ProcessName.Equals(app.ProcessName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matches.Count == 0) return;

        foreach (var match in matches)
        {
            match.Group.Apps.Remove(match.App);
        }
        ScrubRoutingForProcess(app.ProcessName);
        SaveSettings();
    }

    private void DeployBundledProfiles()
    {
        string[] profileFiles = OperatingSystem.IsMacOS() ? new[] { "default-macos.json", "default.json" }
            : OperatingSystem.IsLinux() ? new[] { "default-linux.json", "default.json" }
            : new[] { "default.json" };

        foreach (var file in profileFiles)
        {
            var destPath = Path.Combine(AppPaths.ProfilesDir, file);
            var bundledPath = Path.Combine(AppContext.BaseDirectory, "profiles", file);
            if (!File.Exists(destPath) && File.Exists(bundledPath))
            {
                File.Copy(bundledPath, destPath);
                _logger.Information("Deployed {File}", file);
            }
        }

        if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
        {
            var destSingBox = AppPaths.SingBoxExePath;
            var bundledSingBox = Path.Combine(AppContext.BaseDirectory, "sing-box");
            if (File.Exists(bundledSingBox) && !File.Exists(destSingBox))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destSingBox)!);
                File.Copy(bundledSingBox, destSingBox);
                File.SetUnixFileMode(destSingBox,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                _logger.Information("Deployed sing-box to {Path}", destSingBox);
            }
        }
    }
}
