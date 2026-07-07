#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VPNRouter.Core;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.App.ViewModels;

/// <summary>
/// Phase 2B (Wave 8, 2026-05-18) — Profile / Apps surface split out of the
/// <c>MainWindowViewModel</c> god-class. Hosts the data-load + UI-wire
/// helpers that back the Applications tab (the AppGroups tree of profile-
/// driven + custom-category + custom-apps groups), plus the user-facing
/// add/remove commands:
///
/// <list type="bullet">
///   <item><see cref="LoadApps"/> — main bootstrap. Reads
///   <c>default.json</c> / <c>default-macos.json</c> / <c>default-linux.json</c>,
///   builds the AppGroups tree, hydrates with persisted state
///   (CustomCategories, CustomGroupApps, CustomApps, ExcludedApps), and
///   seeds the AM-3 mode-aware <c>RoutingAppsInclude</c> list when empty
///   on first upgrade.</item>
///   <item><see cref="CreateBridgedAppItem"/> — factory wiring the
///   <see cref="AppItemViewModel"/> bridge so its IsChecked reads from /
///   writes to the active mode-aware list.</item>
///   <item><see cref="ComputeLegacyEffectiveIncludeNames"/> — AM-3 helper
///   that computes the pre-AM-3 "checked = routed via VPN" set from
///   profile + custom data, used to bootstrap
///   <c>RoutingAppsInclude</c> for upgrading users.</item>
///   <item><see cref="WireAppChangeTracking"/> /
///   <see cref="UnwireAllAppGroups"/> +
///   <see cref="OnAppGroupPropertyChanged"/> /
///   <see cref="OnAppsCollectionChanged"/> /
///   <see cref="OnAppItemPropertyChanged"/> — VM-8 leak-safe
///   PropertyChanged + CollectionChanged subscriptions that drive
///   HasPendingAppChanges + auto-persist toggles.</item>
///   <item><see cref="StripExe"/> — cross-platform .exe-suffix normaliser
///   used by the profile loader and AddCustomApp.</item>
///   <item><see cref="AddCategory"/> / <see cref="RemoveCategory"/> /
///   <see cref="AddCustomApp"/> / <see cref="RemoveCustomApps"/> /
///   <see cref="RemoveCustomApp"/> — the Apps-tab add/remove commands.</item>
///   <item><see cref="DeployBundledProfiles"/> — first-run deploy of
///   bundled profile JSON + sing-box binary on Unix.</item>
/// </list>
///
/// <para>The mode-aware bridge (<c>IsAppCheckedInCurrentMode</c> /
/// <c>SetAppCheckedInCurrentMode</c>) stays in the main file because it's
/// used by the AppItem ctor seed and by AM-3 callsites that also touch
/// settings flags; treating it as cross-concern is safer than splitting
/// the bridge across two partials.</para>
/// </summary>
public partial class MainWindowViewModel
{
    private void LoadApps()
    {
        // v2.31.0-r3 (VM-8): explicit unwiring before Clear(). ObservableCollection.Clear()
        // raises CollectionChanged with action=Reset where both NewItems and OldItems are
        // null, so the WireAppChangeTracking handler can't unsubscribe old PropertyChanged
        // delegates. Without this, every RU↔EN toggle (which calls LoadApps) leaks one
        // subscription per existing AppGroupViewModel + AppItemViewModel. Cumulative.
        UnwireAllAppGroups();
        AppGroups.Clear();
        BypassAppGroups.Clear();

        var activeProfileStr = _settings.ActiveProfile ?? "";
        var isFirstLaunch = string.IsNullOrWhiteSpace(activeProfileStr);

        var activeProfiles = activeProfileStr
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        // Bug-r9-I (2026-05-11): load per-app exclusions so unchecked apps
        // inside active groups stay unchecked across reboot. Normalised to
        // the StripExe form because that's what AppItemViewModel.ProcessName
        // holds (no .exe on macOS/Linux, raw on Windows).
        var excludedSet = new HashSet<string>(
            (_settings.ExcludedApps ?? new()).Select(s => StripExe(s ?? string.Empty)),
            StringComparer.OrdinalIgnoreCase);

        // AM-3 (2026-05-12) — guarantee the active mode list is non-null so
        // the AppItem bridge always has something to write into. Migration
        // (Migrate_2_to_3) seeds RoutingAppsInclude from legacy
        // CustomApps but doesn't account for Profile.Processes /
        // CustomGroupApps / ExcludedApps semantics which live in the App
        // layer. We complete the seeding here below.
        _settings.App.RoutingAppsInclude ??= new List<string>();
        _settings.App.RoutingAppsExclude ??= new List<string>();

        // AM-3 — compute the legacy effective include list (Profile-driven
        // process names of active groups minus ExcludedApps plus
        // CustomGroupApps plus top-level CustomApps + CustomCategories
        // apps). Used both as the AppItem ctor seed AND, when
        // RoutingAppsInclude is empty in include mode, as the seed for
        // the new mode-aware list. This makes the upgrade silent for
        // users who never opened the new mode toggle: their previously
        // routed apps stay routed.
        var legacyIncludeNames = ComputeLegacyEffectiveIncludeNames(
            activeProfiles, excludedSet, isFirstLaunch);

        var isIncludeMode = !string.Equals(
            _settings.App.RoutingAppsMode, "exclude",
            StringComparison.OrdinalIgnoreCase);

        // Seed RoutingAppsInclude from legacy state on first load after
        // upgrade — only when the user hasn't explicitly populated it
        // AND we're in include mode (legacy semantics map directly to
        // include). Exclude mode starts empty by design; the user adds
        // bypass apps explicitly.
        if (isIncludeMode
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

        // Load from profiles. Per-platform variants:
        //   macOS → default-macos.json
        //   Linux → default-linux.json (v2.21.6)
        //   Windows + fallback → default.json
        var profileFile = OperatingSystem.IsMacOS() ? "default-macos.json"
                        : OperatingSystem.IsLinux() ? "default-linux.json"
                        : "default.json";
        var profilePath = Path.Combine(AppContext.BaseDirectory, "profiles", profileFile);
        if (!File.Exists(profilePath))
            profilePath = Path.Combine(AppPaths.ProfilesDir, profileFile);
        // Fallback to default.json if the platform-specific variant is missing.
        if (!File.Exists(profilePath))
            profilePath = Path.Combine(AppPaths.ProfilesDir, "default.json");

        if (File.Exists(profilePath))
        {
            try
            {
                var json = File.ReadAllText(profilePath);
                // Phase 3B (2026-05-18): STJ migration — JsonSerializer with
                // ProfileManager.SafeJsonOptions (MaxDepth=32 +
                // PropertyNameCaseInsensitive=true) preserves the v2.31.0-r1
                // DoS guard and reads existing snake_case profiles.json
                // (the on-disk schema, mapped via [JsonPropertyName] on
                // Profile/ProcessRule). See plans/phase3-3B-newtonsoft-to-stj-2026-05-18.md.
                var collection = JsonSerializer.Deserialize<ProfileCollection>(json, ProfileManager.SafeJsonOptions);
                if (collection?.Profiles != null)
                {
                    foreach (var profile in collection.Profiles)
                    {
                        // First launch: select all profiles by default
                        var isActive = isFirstLaunch || activeProfiles.Any(p =>
                            p.Equals(profile.Name, StringComparison.OrdinalIgnoreCase));

                        var includeGroup = new AppGroupViewModel(profile.Name, profile.Description, isActive);

                        foreach (var proc in profile.Processes)
                        {
                            var name = StripExe(proc.Name);
                            // Bug-r9-I: respect persisted per-app exclusions
                            // when the group itself is active.
                            var appChecked = isActive && !excludedSet.Contains(name);
                            includeGroup.Apps.Add(CreateIncludeAppItem(name, appChecked));
                        }

                        // Merge user-added custom apps for this group
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

        // Bypass catalogue is intentionally separate from the include catalogue.
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

        // Custom Apps exists in both editors so checked imported entries can persist.
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

        // User-created categories (persisted separately from default groups)
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

    /// <summary>
    /// AM-3 (2026-05-12) — factory for an <see cref="AppItemViewModel"/>
    /// with the mode-aware bridge wired so its IsChecked reads from /
    /// writes to <see cref="AppConfig.RoutingAppsInclude"/> or
    /// <see cref="AppConfig.RoutingAppsExclude"/> based on the current
    /// mode. The <paramref name="legacyChecked"/> is used only as a
    /// fallback initial value; once the bridge is wired the bridge's
    /// ReadMode is the source of truth for the IsChecked getter.
    /// </summary>
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

    /// <summary>
    /// AM-3 (2026-05-12) — compute the legacy "checked = routed via VPN"
    /// set from profile/custom-group/custom-categories data. Mirrors the
    /// AppItem ctor seed formula used in LoadApps below. Used to bootstrap
    /// <see cref="AppConfig.RoutingAppsInclude"/> for users upgrading
    /// from pre-AM-3 builds where the new field had no profile-driven
    /// seed.
    /// </summary>
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

        // ── 1. Profile-driven processes for active groups ──
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
                // Phase 3B (2026-05-18): STJ migration via
                // ProfileManager.SafeJsonOptions for the AM-3 legacy seed path.
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

        // ── 2. Top-level CustomApps (already checked semantically) ──
        foreach (var a in _settings.CustomApps ?? new())
            TryAdd(StripExe(a ?? string.Empty));

        // ── 3. Enabled CustomCategories (apps within enabled categories) ──
        foreach (var cat in _settings.CustomCategories ?? new())
        {
            if (!cat.Enabled) continue;
            foreach (var a in cat.Apps ?? new())
                TryAdd(StripExe(a ?? string.Empty));
        }

        return result;
    }

    /// <summary>
    /// Hook property-change listeners on all AppGroups + their Apps to set
    /// HasPendingAppChanges when user edits the list while VPN is running.
    /// </summary>
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
        HasPendingAppChanges = IsConnected;
    }

    private void OnAppGroupPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_isLoadingUI) return;
        if (e.PropertyName == nameof(AppGroupViewModel.IsChecked))
        {
            HasPendingAppChanges = IsConnected;
            // Bug-r9-I (2026-05-11): persist immediately so the toggle
            // survives a Windows restart even if the user never clicks
            // Apply (Apply is gated on IsConnected — invisible while VPN
            // is off, which was the entire shape of the user complaint).
            try { SaveSettings(); }
            catch (Exception ex) { _logger?.Warning(ex, "[VM] Auto-save on AppGroup change failed"); }
        }
    }

    /// <summary>
    /// v2.31.0-r3 (VM-8): unsubscribe PropertyChanged + CollectionChanged
    /// from every AppGroup + its Apps before LoadApps() rebuilds the list.
    /// Avalonia's ObservableCollection.Clear() emits a Reset CollectionChanged
    /// without OldItems, so the wire-tracking handler can't unsubscribe.
    /// </summary>
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
        HasPendingAppChanges = IsConnected;
    }

    private void OnAppItemPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_isLoadingUI) return;
        if (e.PropertyName == nameof(AppItemViewModel.IsChecked))
        {
            HasPendingAppChanges = IsConnected;
            // Bug-r9-I (2026-05-11): same rationale as OnAppGroupPropertyChanged
            // — toggle must persist even when disconnected. A group-level
            // toggle cascades to N apps which means N saves in a row, but
            // YAML write is sub-millisecond and the user can't toggle
            // fast enough to make this a bottleneck.
            try { SaveSettings(); }
            catch (Exception ex) { _logger?.Warning(ex, "[VM] Auto-save on AppItem change failed"); }
        }
    }

    /// <summary>
    /// Strip .exe suffix on Unix platforms (macOS, Linux). sing-box matches
    /// by exact process name on Windows (Discord.exe) while on Unix the
    /// process name is bare (Discord, chrome, firefox). The profile JSON
    /// ships with Windows-style .exe names, and MacProcessScanner
    /// normalises at scan time, but the UI would still surface those .exe
    /// names to the user. Stripping in the UI + settings path keeps the
    /// Applications tab readable on Linux.
    /// v2.21.1: Linux added to the strip set (was macOS-only).
    /// </summary>
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

    // ── Custom category / custom app commands ──

    [ObservableProperty] private string _newCategoryName = string.Empty;

    [RelayCommand]
    private void AddCategory()
    {
        var name = NewCategoryName?.Trim();
        if (string.IsNullOrWhiteSpace(name)) return;
        // r6 (audit finding #5): a category name is later embedded verbatim into
        // the Explorer submenu command (--category "<name>"). '%' is token-
        // expanded by Explorer, '\' / trailing-'\' escapes the closing quote
        // (argv rules), and '"' breaks it outright — all corrupt the verb and
        // make the shell "add" silently fall back to the default group. Strip
        // these shell-unsafe chars at the source so the persisted name is always
        // safe (they carry no meaning in a category label).
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
        // audit (apps-page "removed apps remain in routing policy"): scrub the
        // category's apps from RoutingAppsInclude/Exclude before dropping the
        // group, or invisible rules survive (leak-from-intent in Exclude mode,
        // an unwanted route in Include mode).
        foreach (var item in group.Apps.ToList())
            ScrubRoutingForApp(item);
        var peerGroups = AllAppGroups()
            .Where(g => g.IsCustomCategory && g.Name.Equals(group.Name, StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var peer in peerGroups)
            (AppGroups.Contains(peer) ? AppGroups : BypassAppGroups).Remove(peer);
        if (SelectedAppGroup == group)
            SelectedAppGroup = AppGroups.FirstOrDefault();
        if (SelectedBypassAppGroup == group)
            SelectedBypassAppGroup = BypassAppGroups.FirstOrDefault();
        SaveSettings();
    }

    /// <summary>
    /// Remove a process from the active routing lists when its AppItem is
    /// removed from the UI. Mirrors the Explorer shell-verb removal path:
    /// uncheck (fires WriteMode -> drops from RoutingAppsInclude/Exclude) plus a
    /// defensive direct scrub (covers an entry that was routed but never surfaced
    /// as an AppItem). Idempotent + safe on an already-unrouted item.
    /// </summary>
    private void ScrubRoutingForApp(AppItemViewModel item)
    {
        if (item == null) return;
        var name = item.ProcessName;

        // v2.40.0-r8 (#5 bug-scout regression fix): compute the survivor decision
        // BEFORE any uncheck. AppItemViewModel.IsChecked is a READ-THROUGH to the
        // single shared routing-list entry (one entry per process name, shared by
        // every AppItem with that name across groups). So `item.IsChecked = false`
        // empties that entry IMMEDIATELY — and a survivor snapshot taken AFTER it
        // would see the OTHER checked duplicate as unchecked too, collapse it, and
        // un-route the app from EVERY group (split-tunnel leak-from-intent that
        // defeats the r2 survivor-guard). Snapshot the checked OTHER duplicates
        // here, while the shared entry is still intact.
        bool stillRoutedByAnother = false;
        if (!string.IsNullOrWhiteSpace(name))
        {
            var survivors = AppGroups
                .SelectMany(g => g.Apps)
                .Where(a => !ReferenceEquals(a, item) && a.IsChecked)
                .Select(a => a.ProcessName)
                .ToList(); // materialise BEFORE the uncheck mutates the shared list
            stillRoutedByAnother =
                VPNRouter.Core.Services.RoutingAppListEditor.IsStillRoutedByAnother(name, survivors);
        }

        // Another checked AppItem still routes this name → leave the shared routing
        // entry AND the other checkbox's state untouched; the caller just drops this
        // row from its group. Unchecking here would remove the single shared entry
        // and silently un-route the app the user keeps checked elsewhere.
        if (stillRoutedByAnother) return;

        try { item.IsChecked = false; } catch { }
        try { VPNRouter.Core.Services.RoutingAppListEditor.TryRemoveProcessName(_settings, item.ProcessName); }
        catch { }
        // v2.40.0 (review M5): TryRemoveProcessName only scrubs RoutingAppsInclude
        // (and no-ops off-Windows), and the active-list uncheck above can't touch
        // the INACTIVE list. So a row removed while in the other mode left a stale
        // entry in the unscrubbed list that reappeared + bypassed the VPN on a
        // mode flip (leak-from-intent). Removing a UI row must drop the app from
        // EVERY routing list regardless of current mode. Match both the stored
        // name and its .exe-stripped form for cross-platform safety.
        try
        {
            if (!string.IsNullOrWhiteSpace(name))
            {
                var bare = StripExe(name);
                bool Match(string p) =>
                    string.Equals(p, name, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(p, bare, StringComparison.OrdinalIgnoreCase);
                _settings.App.RoutingAppsInclude?.RemoveAll(Match);
                _settings.App.RoutingAppsExclude?.RemoveAll(Match);
            }
        }
        catch { }
    }

    [RelayCommand]
    private void AddCustomApp(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return;

        var name = StripExe(processName.Trim());
        var target = SelectedActiveAppGroup;

        // Fallback: if no group selected, use "Custom Apps"
        if (target == null)
        {
            target = ActiveAppGroups.FirstOrDefault(g => g.Name == "Custom Apps");
            if (target == null)
            {
                target = new AppGroupViewModel("Custom Apps", "Your custom applications", !IsAppsListEditorExclude) { IsCustomGroup = true };
                ActiveAppGroups.Add(target);
            }
        }

        var existing = target.Apps.FirstOrDefault(a => a.ProcessName.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            existing.IsChecked = true;
            SaveSettings();
            return;
        }

        // AM-3 (2026-05-12) — bridge-wired add so adding a custom app
        // writes into the active mode list straight away. Setting
        // IsChecked=true on a fresh AppItem triggers WriteMode which
        // appends to RoutingAppsInclude (or RoutingAppsExclude) per
        // current mode. SaveSettings inside WriteMode persists; the
        // explicit SaveSettings() below is retained for the
        // category-state side-effect (CustomCategories.Apps list).
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
    private void ImportSteamGames()
    {
        if (!IsAppsListEditorExclude)
            AppsListEditorMode = "exclude";

        var added = 0;
        foreach (var game in Services.SteamLibraryScanner.FindInstalledGames())
        {
            if (AddCustomAppCandidate(game.ProcessName))
                added++;
        }

        if (added > 0)
        {
            SaveSettings();
            ShowRulesToast(IsRussian
                ? $"Steam: найдено {added} .exe"
                : $"Steam: found {added} .exe files");
        }
        else
        {
            ShowRulesToast(IsRussian
                ? "Steam-игры не найдены"
                : "No Steam games found");
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
    /// <summary>
    /// v2.38.0 — add an app to the split-tunnel list from the Explorer
    /// "route through VPN" context-menu verb (<c>--route-app "%1"</c>).
    /// Windows-only (matches the shell-verb feature surface); the
    /// <c>#if PLATFORM_WINDOWS</c> guard keeps the Linux/Mac
    /// MainWindowViewModel public-surface hash unchanged.
    /// Resolves the path (.exe or .lnk) to a process-name, then routes it
    /// through the SAME path as the manual Add button so it lands in the
    /// "Custom Apps" group (visible + checked) AND is bridged into
    /// <c>RoutingAppsInclude</c>. No reconnect (locked design — applies on
    /// next connect). Invoked from App.axaml.cs on RouteAppRequested /
    /// PendingRouteAppPath. See plans/feature-shell-context-menu-add-app.md.
    /// </summary>
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

        // RoutingAppsInclude is the authoritative routed list (include mode) — dedup there.
        var routed = _settings.App.RoutingAppsInclude ?? new List<string>();
        if (routed.Any(e => string.Equals(e, exeName, StringComparison.OrdinalIgnoreCase)))
        {
            _logger.Information("[ShellAdd] {Exe} already routed — no-op", exeName);
            ShowRulesToast(IsRussian ? $"{exeName} уже в списке VPN" : $"{exeName} already routed");
            return;
        }

        // r4: pick the target group. With the cascading submenu the user picks a
        // category by name (--category "<name>"); match it. Otherwise (flat verb
        // or a category that was deleted since the verb was registered) fall back
        // to the default "Custom Apps" group. Then add exactly like the manual
        // Add button: lands in the group, checked, bridged into
        // RoutingAppsInclude, saved (AddCustomApp uses SelectedAppGroup).
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
            // r6 (audit finding #2): AddCustomApp early-returns if the target
            // group ALREADY holds the app (even unchecked) — it bails BEFORE
            // setting IsChecked=true, so nothing gets routed and the toast would
            // lie. Force the landed item checked so the verb's promise holds.
            var landedItem = target?.Apps.FirstOrDefault(
                a => string.Equals(a.ProcessName, exeName, StringComparison.OrdinalIgnoreCase));
            landedItem ??= AppGroups.SelectMany(g => g.Apps).FirstOrDefault(
                a => string.Equals(a.ProcessName, exeName, StringComparison.OrdinalIgnoreCase));
            if (landedItem != null && !landedItem.IsChecked)
                landedItem.IsChecked = true; // bridge → RoutingAppsInclude
        }
        finally
        {
            SelectedAppGroup = prevSelected;
            AppsListEditorMode = prevEditorMode;
        }

        // r6 (audit finding #2): base the toast on the ACTUAL post-state — never
        // claim "routed" if the write didn't take.
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

    /// <summary>
    /// v2.38.0-r5 — remove an app from the split-tunnel list via the Explorer
    /// "remove from VPN" context-menu verb (<c>--unroute-app "%1"</c>).
    /// Windows-only (<c>#if PLATFORM_WINDOWS</c> keeps the Linux/Mac
    /// MainWindowViewModel surface hash unchanged). Resolves the path (.exe or
    /// .lnk) to a process-name, then unwinds it from EVERY place it lives:
    /// the bridged AppItem (<c>IsChecked=false</c> fires WriteMode → removes it
    /// from <c>RoutingAppsInclude</c>), its group (UI + custom_apps /
    /// custom_group_apps on save), and a defensive direct
    /// <see cref="RoutingAppListEditor.TryRemoveProcessName"/> scrub. No COM →
    /// the verb is always visible, so this no-ops with a toast if the app
    /// wasn't routed. Invoked from App.axaml.cs on UnrouteAppRequested /
    /// PendingUnrouteAppPath.
    /// </summary>
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

        // Was it routed at all? RoutingAppsInclude is the authoritative list.
        var routed = _settings.App.RoutingAppsInclude ?? new List<string>();
        bool wasRouted = routed.Any(e => string.Equals(e, exeName, StringComparison.OrdinalIgnoreCase));

        // 1) Uncheck + drop the bridged AppItem from EVERY group it lives in.
        //    r6 (audit finding #3): the same process name can exist as separate
        //    AppItem instances across multiple groups (Custom Apps + a named
        //    category — LoadApps dedups only WITHIN a group). The old code
        //    removed only the FIRST match (break) and SaveSettings re-persisted
        //    the leftover from the surviving group, which could re-route when
        //    that group's master checkbox was later toggled. Collect ALL matches
        //    first (avoid mutating mid-iterate), then uncheck + remove each.
        //    IsChecked=false fires WriteMode → removes from RoutingAppsInclude.
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

        // 2) Defensive direct scrub of RoutingAppsInclude — covers an entry that
        //    was routed but never surfaced as an AppItem (e.g. added by a prior
        //    --route-app into a since-deleted category).
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

        ScrubRoutingForApp(app);
        foreach (var match in matches)
        {
            match.Group.Apps.Remove(match.App);
        }
        SaveSettings();
    }

    // ── First-run profile + sing-box deploy ──

    /// <summary>
    /// First-run setup: deploy bundled profiles and sing-box binary.
    /// </summary>
    private void DeployBundledProfiles()
    {
        // Deploy profiles. Ship the platform-specific variant first + the
        // generic default.json as fallback so any code still resolving
        // "default.json" keeps working on first launch.
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

        // Deploy sing-box binary on Unix platforms.
        // macOS: bundled inside the .app (build-mac.sh copies it into
        //        Contents/MacOS/ during packaging).
        // Linux: bundled inside the AppImage / .deb / tar.gz payload by the
        //        build-linux.yml GitHub Actions workflow, which curl-downloads
        //        sing-box-linux-amd64 from SagerNet/sing-box releases and
        //        drops it next to VPNRouter.App. Either way, we copy it
        //        from AppContext.BaseDirectory to ~/.config/vpnrouter/bin/
        //        on first launch so the user doesn't have to do anything.
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
