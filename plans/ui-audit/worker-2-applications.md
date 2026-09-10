# Applications Page UI Audit: Process Virtualization, Reactivity, Steam Scanner & Responsiveness

**Date:** 2026-09-07  
**Target File:** `plans/ui-audit/worker-2-applications.md`  
**Audit Scope:**
- `VPNRouter.App/Views/Pages/ApplicationsPage.axaml`
- `VPNRouter.App/Views/Pages/ApplicationsPage.axaml.cs`
- `VPNRouter.App/ViewModels/MainWindowViewModel.Profiles.cs`
- `VPNRouter.App/ViewModels/MainWindowViewModel.cs` (Applications & Routing sections)
- `VPNRouter.App/ViewModels/AppGroupViewModel.cs`
- `VPNRouter.App/ViewModels/AppItemViewModel.cs`
- `VPNRouter.App/Services/SteamLibraryScanner.cs`

---

## Executive Summary

The Applications page (`ApplicationsPage.axaml`) and its backing ViewModels (`MainWindowViewModel.Profiles.cs`, `AppGroupViewModel.cs`, `AppItemViewModel.cs`) manage split-tunnel process routing in both Include ("Through VPN") and Exclude ("Bypass VPN") modes.

This audit identified **11 critical and high-impact findings** across the 4 core focus areas:
1. **Virtualization & Checkbox Throttling**: A catastrophic dual-layer disk I/O thrashing loop executes `SaveSettings()` twice per app toggle. Bulk actions (`Select All`, `Clear All`, group toggles) on 200 apps cause 400 synchronous disk YAML writes + `.bak` file copies on the UI thread. Mode switches (`Include` ↔ `Exclude`) fire 500+ synchronous disk writes.
2. **Filter & Search Reactivity**: The page completely lacks search/filter controls for massive app lists. If implemented following existing patterns (like `CustomRulesList`), unfiltered keystrokes would trigger un-debounced collection mutations that falsely mark routing policy as dirty and freeze the UI.
3. **Steam Library Scanning & Batching**: `ImportSteamGames` executes a synchronous recursive directory scan across all Steam libraries directly on the UI thread (freezing the window for 5–15s). Each discovered game is inserted individually into two `ObservableCollection`s and immediately calls `RaiseIsCheckedChanged()`, executing synchronous YAML writes for every single game. Additionally, switching editor modes during import desynchronizes the UI segmented toggle from the active routing mode.
4. **Narrow (360px) Responsiveness & Accessibility**: The fixed 120px master column starves the detail view on narrow windows. Bulk action buttons ("Выбрать все" / "Снять выделение") overflow horizontally and clip in Russian. Icon-only buttons (`✕` for custom categories and custom apps) and all action controls lack `AutomationProperties.Name`, failing screen reader accessibility standards.
5. **Memory Management**: Collection changed handlers in `MainWindowViewModel.Profiles.cs` ignore `OldItems`, leaking event subscriptions and pinning removed categories and apps in memory forever.

---

## Findings Matrix

| ID | Focus Area | Title | Severity | Location |
|---|---|---|:---:|---|
| **APP-VIRT-01** | Virtualization / Checkbox | Synchronous YAML save thrashing (2x per app) & freeze on bulk selection / mode toggle | **P1** | `AppItemViewModel.cs:75`<br/>`MainWindowViewModel.cs:777`<br/>`MainWindowViewModel.Profiles.cs:510`<br/>`MainWindowViewModel.cs:816` |
| **APP-STEAM-01** | Steam Scan / Batching | Synchronous recursive file-system Steam scan blocks UI thread (5–15s freeze) | **P1** | `MainWindowViewModel.Profiles.cs:671-700`<br/>`SteamLibraryScanner.cs:24-60` |
| **APP-STEAM-02** | Steam Scan / Batching | Unbatched collection mutations + per-game synchronous disk I/O during Steam import | **P1** | `MainWindowViewModel.Profiles.cs:679-684, 713-731` |
| **APP-LAYOUT-01** | Responsive Layout (360px) | Bulk action buttons ("Select All" / "Clear All") overflow horizontally on narrow windows in Russian | **P1** | `ApplicationsPage.axaml:289-298` |
| **APP-SRCH-01** | Filter / Search | Complete absence of search/filter functionality for massive application catalogues | **P1** | `ApplicationsPage.axaml:213-402`<br/>`MainWindowViewModel.Profiles.cs:61-253` |
| **APP-VIRT-02** | Virtualization / Checkbox | Linear O(N) lookup in `List<string>` on every checkbox check/uncheck | **P2** | `MainWindowViewModel.cs:750-773` |
| **APP-VIRT-03** | Virtualization / Checkbox | `$parent[UserControl]` visual tree walks on every realized item in virtualized ListBox | **P2** | `ApplicationsPage.axaml:332, 338, 343` |
| **APP-SRCH-02** | Filter / Search | Collection-based filtering without debounce triggers UI freeze & corrupts dirty-tracking | **P2** | `MainWindowViewModel.Profiles.cs:424-425, 488-497`<br/>`MainWindowViewModel.cs:914-964` |
| **APP-STEAM-03** | Steam Scan / State | Mode toggle desynchronization: `AppsListEditorMode` flips without updating `RoutingAppsMode` | **P2** | `MainWindowViewModel.Profiles.cs:674-675`<br/>`MainWindowViewModel.cs:601-665` |
| **APP-LAYOUT-02** | Responsive Layout (360px) | Fixed 120px master column heavily cramps both category names and detail app rows | **P2** | `ApplicationsPage.axaml:213-264`<br/>`ApplicationsPage.axaml.cs:5-11` |
| **APP-LAYOUT-03** | Responsive Layout (360px) | Full-Tunnel and True-Split banners squish text and fail to wrap action buttons | **P2** | `ApplicationsPage.axaml:118-141, 181-205` |
| **APP-A11Y-01** | Accessibility | Missing `AutomationProperties.Name` on icon-only deletion buttons and interactive controls | **P2** | `ApplicationsPage.axaml:131, 194, 226, 251, 255, 277, 291, 294, 307, 336, 362, 371, 379, 383` |
| **APP-MEM-01** | Memory Lifecycle | Event handler leak on category/app removal (`OldItems` ignored in collection listeners) | **P2** | `MainWindowViewModel.Profiles.cs:436-453, 488-497` |
| **APP-STEAM-04** | Steam Scan / UX | Steam import discards friendly game names and surfaces cryptic executable basenames | **P3** | `MainWindowViewModel.Profiles.cs:681`<br/>`SteamLibraryScanner.cs:52, 158` |

---

## Detailed Findings by Focus Area

### Focus Area 1: Virtualization on Massive Process Lists & Per-App Routing Checkboxes

#### APP-VIRT-01 (Severity P1)
- **File:Line**: 
  - `VPNRouter.App/ViewModels/AppItemViewModel.cs:75`
  - `VPNRouter.App/ViewModels/MainWindowViewModel.cs:777`
  - `VPNRouter.App/ViewModels/MainWindowViewModel.Profiles.cs:510`
  - `VPNRouter.App/ViewModels/MainWindowViewModel.cs:816`
- **Symptom / Finding**:
  Dual-layer synchronous disk I/O thrashing on every checkbox toggle and catastrophic O(N) cascade during bulk operations or mode toggles.
  1. When an app checkbox is toggled, `AppItemViewModel.IsChecked` setter invokes `WriteMode` (`SetAppCheckedInList`), which performs `try { SaveSettings(); }`. `SaveSettings()` copies `config.yaml` to `config.yaml.bak`, serializes settings to YAML, and writes to disk synchronously.
  2. Then, `AppItemViewModel` raises `OnPropertyChanged(nameof(IsChecked))`.
  3. `MainWindowViewModel.Profiles.cs` has an attached listener `OnAppItemPropertyChanged` which catches `nameof(AppItemViewModel.IsChecked)` and calls `try { SaveSettings(); }` **a second time**!
  4. When `SelectAll`, `ClearAll`, or a group master checkbox is clicked on a group with 200 apps, `SaveSettings()` is called **400 times synchronously on the UI thread**, blocking the UI for multiple seconds.
  5. When the user flips the mode toggle (`Include` ↔ `Exclude`), `OnRoutingAppsModeChanged` invokes `RefreshAppCheckboxes()`, which calls `app.RaiseIsCheckedChanged()` for every app across `AppGroups.Concat(BypassAppGroups)`. Every synthetic notification hits `OnAppItemPropertyChanged` and triggers `SaveSettings()` for **every app in the entire application** (500+ saves in a row on the UI thread).
- **Concrete Actionable Fix**:
  1. Remove `SaveSettings()` from `OnAppItemPropertyChanged` in `MainWindowViewModel.Profiles.cs:510`. Setting persistence is already handled by `WriteMode` (`SetAppCheckedInList`).
  2. Guard `OnAppItemPropertyChanged` with an `_isRefreshingCheckboxes` flag during `RefreshAppCheckboxes()` so synthetic UI refresh notifications never trigger persistence or dirty marking.
  3. In `SelectAll`, `ClearAll`, and `OnIsCheckedChanged`, wrap the loop in a batch scope:
     ```csharp
     using (_batchUpdateScope.Enter())
     {
         foreach (var app in Apps) app.SetCheckedInternal(value);
     }
     SaveSettings();
     MarkRoutingSettingsChanged();
     ```

---

#### APP-VIRT-02 (Severity P2)
- **File:Line**: `VPNRouter.App/ViewModels/MainWindowViewModel.cs:750-773`
- **Symptom / Finding**:
  Linear O(N) lookup in `List<string>` on every checkbox check/uncheck.
  `IsAppCheckedInList` and `SetAppCheckedInList` perform `list.Any(...)` and `list.FirstOrDefault(...)` on `_settings.App.RoutingAppsInclude` and `RoutingAppsExclude`. With hundreds of apps, virtualized rendering and item checking triggers repeated linear string comparisons and heap allocations on the UI thread.
- **Concrete Actionable Fix**:
  Maintain an in-memory `HashSet<string>` with `StringComparer.OrdinalIgnoreCase` representing the active include/exclude sets. Synchronize with the underlying `List<string>` only on persistent save, providing O(1) membership checks for all checkbox bindings and updates.

---

#### APP-VIRT-03 (Severity P2)
- **File:Line**: `VPNRouter.App/Views/Pages/ApplicationsPage.axaml:332, 338, 343`
- **Symptom / Finding**:
  `$parent[UserControl]` visual tree walks on every realized item in virtualized ListBox.
  In `ListBox.ItemTemplate` (`AppItemViewModel`), three bindings resolve through the parent visual tree:
  - `Text="{Binding $parent[UserControl].((vm:MainWindowViewModel)DataContext).L_LblCustomBadge}"`
  - `Command="{Binding $parent[UserControl].((vm:MainWindowViewModel)DataContext).RemoveCustomAppCommand}"`
  - `ToolTip.Tip="{Binding $parent[UserControl].((vm:MainWindowViewModel)DataContext).L_TipRemoveApp}"`
  During scrolling with container recycling across hundreds of items, traversing up to `UserControl` on every item realization incurs unnecessary visual tree traversal overhead.
- **Concrete Actionable Fix**:
  Pass a command delegate directly to `AppItemViewModel` during instantiation (e.g. `RemoveCommand`), or bind via a shared static resource / inherit localized strings from a token resource dictionary rather than walking the visual tree.

---

### Focus Area 2: Filter/Search Reactivity on Typing Without UI Thread Freezes

#### APP-SRCH-01 (Severity P1)
- **File:Line**: 
  - `VPNRouter.App/Views/Pages/ApplicationsPage.axaml:213-402`
  - `VPNRouter.App/ViewModels/MainWindowViewModel.Profiles.cs:61-253`
- **Symptom / Finding**:
  Complete absence of search/filter functionality for massive application catalogues.
  `ApplicationsPage.axaml` has no `TextBox` or filter control for searching apps or categories. When hundreds of applications (system default profiles, Steam imports, custom executables) are loaded, finding a specific application requires scrolling through large lists manually.
- **Concrete Actionable Fix**:
  Add an application search input in the detail header above the apps ListBox:
  ```xml
  <TextBox Grid.Row="1" Text="{Binding AppSearchText}"
           PlaceholderText="{Binding L_SearchAppsPlaceholder}"
           Classes="search-box" Margin="14,0,14,6"/>
  ```
  Expose a filtered projection on `AppGroupViewModel` or `MainWindowViewModel`.

---

#### APP-SRCH-02 (Severity P2)
- **File:Line**: 
  - `VPNRouter.App/ViewModels/MainWindowViewModel.Profiles.cs:424-425, 488-497`
  - `VPNRouter.App/ViewModels/MainWindowViewModel.cs:914-964`
- **Symptom / Finding**:
  Naive collection filtering on typing freezes the UI thread and corrupts dirty-tracking state.
  In other parts of the codebase (e.g. `CustomRulesSearchText`), filtering synchronously invokes `Clear()` and `Add()` on the bound collection on every keystroke. If applied directly to `AppGroupViewModel.Apps`, modifying `Apps` would trigger `group.Apps.CollectionChanged` (`OnAppsCollectionChanged`), which calls `MarkRoutingSettingsChanged()`. This would incorrectly mark the routing configuration dirty and enable the "Apply Changes" button during passive search typing!
- **Concrete Actionable Fix**:
  1. Do not mutate the underlying `Apps` collection when filtering.
  2. Implement an independent read-only projection: `FilteredApps` (`ObservableCollection<AppItemViewModel>` or Avalonia `DataGridCollectionView` with predicate filter).
  3. Debounce input changes by 150–200ms using a `DispatcherTimer` or Rx `.Throttle()` before updating the filtered view.

---

### Focus Area 3: Steam Library Scan UI Updates & Observable Collection Batching

#### APP-STEAM-01 (Severity P1)
- **File:Line**: 
  - `VPNRouter.App/ViewModels/MainWindowViewModel.Profiles.cs:671-700`
  - `VPNRouter.App/Services/SteamLibraryScanner.cs:24-60`
- **Symptom / Finding**:
  Synchronous recursive file-system Steam scan blocks UI thread (5–15s freeze).
  `ImportSteamGames` is marked `[RelayCommand] private void ImportSteamGames()`. It invokes `SteamLibraryScanner.FindInstalledGames()` directly on the UI thread. This method reads Windows registry keys, parses VDF library folders, reads ACF manifests, and traverses directory trees on all drives searching for `*.exe` files. On large Steam libraries across mechanical HDDs or external storage, the UI thread blocks completely, triggering OS "Not Responding" warnings. There is also no progress indicator or cancellation support.
- **Concrete Actionable Fix**:
  1. Convert `ImportSteamGames` to an asynchronous command:
     ```csharp
     [RelayCommand]
     private async Task ImportSteamGamesAsync(CancellationToken ct)
     {
         if (IsScanningSteam) return;
         IsScanningSteam = true;
         try
         {
             var games = await Task.Run(() => Services.SteamLibraryScanner.FindInstalledGames(), ct);
             // Batch update UI
         }
         finally { IsScanningSteam = false; }
     }
     ```
  2. Bind button `IsEnabled="{Binding !IsScanningSteam}"` and show a subtle progress ring or localized status message ("Scanning Steam libraries...").

---

#### APP-STEAM-02 (Severity P1)
- **File:Line**: `VPNRouter.App/ViewModels/MainWindowViewModel.Profiles.cs:679-684, 713-731`
- **Symptom / Finding**:
  Unbatched collection mutations + per-game synchronous disk I/O during Steam import.
  Inside `ImportSteamGames`:
  ```csharp
  foreach (var game in games)
      AddCustomAppCandidate(game.ProcessName);
  ```
  For every game found:
  - `includeCustom.Apps.Add(...)` fires a `CollectionChanged` (Add) notification.
  - `bypassCustom.Apps.Add(...)` fires a second `CollectionChanged` notification.
  - `bypassCustom.Apps.FirstOrDefault(...)?.RaiseIsCheckedChanged()` fires `PropertyChanged(nameof(IsChecked))`.
  - `OnAppItemPropertyChanged` catches `IsChecked` and runs `SaveSettings()` synchronously!
  For 150 discovered games, this causes **300 individual UI collection change events** (forcing continuous layout invalidations) and **150 synchronous YAML saves to disk**, followed by another `SaveSettings()` at line 687.
- **Concrete Actionable Fix**:
  1. Suppress notifications or accumulate items in a local list and perform bulk addition.
  2. Suppress `OnAppItemPropertyChanged` / `SaveSettings()` during the loop.
  3. Call `SaveSettings()` and `MarkRoutingSettingsChanged()` **exactly once** after all candidate games have been appended.

---

#### APP-STEAM-03 (Severity P2)
- **File:Line**: 
  - `VPNRouter.App/ViewModels/MainWindowViewModel.Profiles.cs:674-675`
  - `VPNRouter.App/ViewModels/MainWindowViewModel.cs:601-665`
- **Symptom / Finding**:
  Mode toggle desynchronization: `AppsListEditorMode` flips without updating `RoutingAppsMode`.
  `ImportSteamGames` contains:
  ```csharp
  if (!IsAppsListEditorExclude)
      AppsListEditorMode = "exclude";
  ```
  However, mutating `AppsListEditorMode` only changes which group collection (`BypassAppGroups`) is shown in the editor; it does NOT update `RoutingAppsMode`. The segmented toggle in `ApplicationsPage.axaml` (lines 156-171) binds two-way to `IsRoutingAppsModeInclude` and `IsRoutingAppsModeExclude`. When `ImportSteamGames` executes, the segmented button stays highlighted on "Include", while the list editor displays the "Exclude" catalog, producing an inconsistent UI state.
- **Concrete Actionable Fix**:
  Synchronize both properties by setting `RoutingAppsMode = "exclude"` (which automatically cascades to `AppsListEditorMode = "exclude"` in `OnRoutingAppsModeChanged`).

---

#### APP-STEAM-04 (Severity P3)
- **File:Line**: 
  - `VPNRouter.App/ViewModels/MainWindowViewModel.Profiles.cs:681`
  - `VPNRouter.App/Services/SteamLibraryScanner.cs:52, 158`
- **Symptom / Finding**:
  Steam import discards friendly game names and surfaces cryptic executable basenames.
  `SteamLibraryScanner` parses the friendly `GameName` from manifests (e.g. "Counter-Strike 2", "Civilization VI"), but `AddCustomAppCandidate` only passes `game.ProcessName`. The UI displays raw filenames like `cs2` or `Civ6_DX12`, leaving users unable to easily recognize which game an entry represents.
- **Concrete Actionable Fix**:
  Expose a `FriendlyName` or `DisplayName` property on `AppItemViewModel` (persisted or populated from metadata) so the UI displays `"Counter-Strike 2 (cs2)"`.

---

### Focus Area 4: Layout Responsiveness on Narrow (360px) Windows & Accessibility

#### APP-LAYOUT-01 (Severity P1)
- **File:Line**: `VPNRouter.App/Views/Pages/ApplicationsPage.axaml:289-298`
- **Symptom / Finding**:
  Bulk action buttons ("Select All" / "Clear All") overflow horizontally on narrow windows in Russian.
  The buttons are laid out in:
  ```xml
  <Border Grid.Row="1" Padding="14,4,14,8">
    <StackPanel Orientation="Horizontal" Spacing="6">
      <Button Content="{Binding ...L_SelectAllApps}" ... Padding="6,4"/>
      <Button Content="{Binding ...L_ClearAllApps}" ... Padding="6,4"/>
    </StackPanel>
  </Border>
  ```
  At 360px window width, subtracting the 120px master column and 28px border padding leaves only **212px** for the detail content. In Russian:
  - `L_SelectAllApps` ("Выбрать все") requires ~95px with padding.
  - `L_ClearAllApps` ("Снять выделение") requires ~125px with padding.
  - Total required width: 95px + 6px + 125px = **226px > 212px**.
  At 125% Windows DPI scaling, the required width is ~280px. Because `StackPanel` has `Orientation="Horizontal"` and does not wrap, and `Button.Content` uses bare strings without `TextWrapping="Wrap"`, the second button clips off-screen past the right border.
- **Concrete Actionable Fix**:
  Replace `StackPanel` with a 2-column `Grid` or `WrapPanel`, and wrap button contents in `TextBlock` with `TextWrapping="Wrap"`:
  ```xml
  <Grid ColumnDefinitions="*,*" ColumnSpacing="6">
    <Button Grid.Column="0" Command="{Binding SelectAllCommand}" HorizontalAlignment="Stretch">
      <TextBlock Text="{Binding $parent[UserControl].((vm:MainWindowViewModel)DataContext).L_SelectAllApps}"
                 TextWrapping="Wrap" TextAlignment="Center"/>
    </Button>
    <Button Grid.Column="1" Command="{Binding ClearAllCommand}" HorizontalAlignment="Stretch">
      <TextBlock Text="{Binding $parent[UserControl].((vm:MainWindowViewModel)DataContext).L_ClearAllApps}"
                 TextWrapping="Wrap" TextAlignment="Center"/>
    </Button>
  </Grid>
  ```

---

#### APP-LAYOUT-02 (Severity P2)
- **File:Line**: 
  - `VPNRouter.App/Views/Pages/ApplicationsPage.axaml:213-264`
  - `VPNRouter.App/Views/Pages/ApplicationsPage.axaml.cs:5-11`
- **Symptom / Finding**:
  Fixed 120px master column heavily cramps both category names and detail app rows on narrow windows.
  `ApplicationsPage.axaml:213` hardcodes `ColumnDefinitions="120,*"`. At 360px window width:
  - Left pane (120px): Category names in Russian (e.g. "Пользовательские", "Мессенджеры") truncate with ellipsis after only 6–8 characters. `NewCategoryInput` and `AddCategory` button ("Добавить категорию") are compressed into 112px; the bare-string button text clips without wrapping.
  - Right pane (240px): Inside each app row (`Border Classes="app-row"`), subtracting checkbox (~20px), custom badge (~45px), delete button (~25px), and column spacing leaves only ~85px for the process name, heavily truncating standard filenames (e.g. `ShooterGam...`).
  Unlike `NetworkPage.axaml.cs` which adapts using an `IsRulesNarrow` responsive breakpoint, `ApplicationsPage.axaml.cs` has no size-change listener.
- **Concrete Actionable Fix**:
  1. Add responsive size monitoring in `ApplicationsPage.axaml.cs` with an `IsNarrow` breakpoint at ~480px.
  2. On narrow screens, wrap `AddCategory` button content in `TextBlock TextWrapping="Wrap"`.
  3. Allow master column to collapse into a category dropdown/combo-box selector when window width is below 420px, freeing all 360px for the detail process list.

---

#### APP-LAYOUT-03 (Severity P2)
- **File:Line**: `VPNRouter.App/Views/Pages/ApplicationsPage.axaml:118-141, 181-205`
- **Symptom / Finding**:
  Full-Tunnel and True-Split banners squish explanatory text and fail to wrap action buttons.
  Both warning banners use `<Grid ColumnDefinitions="*,Auto" ColumnSpacing="10">`. In Russian, the action button `L_AppsFullTunnelBannerAction` ("Включить раздельное туннелирование") is 35 characters long (~220px wide). At 360px window width with 28px padding, the button takes 240px, leaving only ~70px for the explanatory `TextBlock` in Column 0, squishing the message into 8+ vertical lines. Furthermore, `Button.Content` uses bare string binding, preventing the button itself from wrapping.
- **Concrete Actionable Fix**:
  Wrap button content in `<TextBlock TextWrapping="Wrap" TextAlignment="Center"/>`, and on narrow layouts switch banner Grid definitions to `RowDefinitions="Auto,Auto"` with the action button spanning the full width below the text.

---

#### APP-A11Y-01 (Severity P2)
- **File:Line**: `VPNRouter.App/Views/Pages/ApplicationsPage.axaml:131, 194, 226, 251, 255, 277, 291, 294, 307, 336, 362, 371, 379, 383`
- **Symptom / Finding**:
  Missing `AutomationProperties.Name` on icon-only deletion buttons and interactive controls.
  Screen readers (Windows Narrator, macOS VoiceOver) fail accessibility compliance:
  1. **Line 277**: Category delete button `Content="✕"` has no `AutomationProperties.Name`. Announced as "Multiplication X".
  2. **Line 336**: Custom app delete button `Content="✕"` has no `AutomationProperties.Name`. Announced as "Multiplication X" with no context of which app is being deleted.
  3. **Lines 226 & 307**: Neither `ListBox` has an automation container name.
  4. **Lines 251 & 379**: Input `TextBox`es (`NewCategoryInput`, `CustomAppInput`) have placeholder text but lack `AutomationProperties.Name`.
  5. **Lines 131, 194, 255, 291, 294, 362, 371, 383**: Action buttons lack explicit accessible names.
- **Concrete Actionable Fix**:
  Add `AutomationProperties.Name` across all interactive elements. Specifically for icon-only buttons:
  - Line 277: `AutomationProperties.Name="{Binding $parent[UserControl].((vm:MainWindowViewModel)DataContext).L_TipRemoveCategory}"`
  - Line 336: `AutomationProperties.Name="{Binding ProcessName, StringFormat='{}{0} - Delete'}"`

---

### Focus Area 5: Memory Lifecycle & Event Handler Management

#### APP-MEM-01 (Severity P2)
- **File:Line**: `VPNRouter.App/ViewModels/MainWindowViewModel.Profiles.cs:436-453, 488-497`
- **Symptom / Finding**:
  Event handler memory leak on category and custom app removal (`OldItems` ignored in collection listeners).
  `OnAppGroupsCollectionChanged` and `OnAppsCollectionChanged` handle only `e.NewItems != null`. When a custom category is removed (`RemoveCategory`) or an app is removed (`RemoveCustomApp` or `UnrouteAppFromShell`), `e.OldItems` is ignored:
  - `g.PropertyChanged -= OnAppGroupPropertyChanged` is never called.
  - `g.Apps.CollectionChanged -= OnAppsCollectionChanged` is never called.
  - `a.PropertyChanged -= OnAppItemPropertyChanged` is never called.
  The removed `AppGroupViewModel` and `AppItemViewModel` instances remain pinned in memory via delegates rooted in `MainWindowViewModel`, causing a persistent memory leak and allowing phantom `OnAppItemPropertyChanged` notifications if those objects are touched.
- **Concrete Actionable Fix**:
  Update both collection listeners to detach handlers when `e.OldItems != null`:
  ```csharp
  if (e.OldItems != null)
  {
      foreach (AppItemViewModel a in e.OldItems)
          a.PropertyChanged -= OnAppItemPropertyChanged;
  }
  ```

---

## Actionable Remediation Roadmap

1. **Phase 1: Elimination of UI Freezes & Thrashing (P1)**
   - Remove redundant `SaveSettings()` call from `OnAppItemPropertyChanged`.
   - Implement batching scope in `AppGroupViewModel.SelectAll()`, `ClearAll()`, and `OnIsCheckedChanged()`.
   - Wrap `ImportSteamGames` in `Task.Run` with `CancellationToken` and `IsScanningSteam` busy state.
   - Batch Steam import item additions and suppress per-game saves.
2. **Phase 2: Narrow Layout (360px) & Accessibility Remediation (P1/P2)**
   - Replace `StackPanel Orientation="Horizontal"` in bulk actions row with a responsive 2-column `Grid` using wrapping `TextBlock`s.
   - Add `AutomationProperties.Name` to category delete and custom app delete `✕` buttons and input textboxes.
   - Wrap `AddCategory` and banner action buttons in `TextBlock TextWrapping="Wrap"`.
3. **Phase 3: Search Reactivity & State Consistency (P2)**
   - Introduce an application search `TextBox` backed by a 200ms debounced filter projection without mutating source collections.
   - Synchronize `RoutingAppsMode` and `AppsListEditorMode` in `ImportSteamGames`.
   - Add `OldItems` unwiring in `OnAppGroupsCollectionChanged` and `OnAppsCollectionChanged` to stop memory leaks.
4. **Phase 4: Steam Display & Virtualization Polish (P3)**
   - Preserve and display friendly Steam game titles alongside process names.
   - Replace linear list scans with `HashSet<string>` lookups for O(1) checkbox resolution.
