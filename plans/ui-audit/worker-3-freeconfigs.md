# UI Performance & Lifecycle Audit: FreeConfigs Public Server Pool & Deep Verifier UI

**Date:** 2026-09-07  
**Worker:** Worker-3 (FreeConfigs Audit)  
**Target Subsystem:** FreeConfigs Page Rendering, Server Pool Virtualization, and Deep Verifier UI  
**Target Files:**
- `VPNRouter.App/Views/Pages/FreeConfigsPage.axaml`
- `VPNRouter.App/Views/Pages/FreeConfigsPage.axaml.cs`
- `VPNRouter.App/ViewModels/FreeConfigs/FreeConfigsPageViewModel.cs`
- `VPNRouter.App/ViewModels/FreeConfigs/FreeConfigItemViewModel.cs`

---

## 1. Executive Summary

This audit evaluates the FreeConfigs subsystem under high-volume public server pool operations (handling hundreds to thousands of server configurations, concurrent TCP/TLS probing, and deep sing-box proxy verifications).

The audit identified **4 Critical/High-Severity (P1)** defects, **5 Medium-Severity (P2)** defects, and **3 Low-Severity (P3)** defects across four core focus areas:
1. **Virtualization Breakdown & Selection Reset:** The UI repeatedly recreates `ObservableCollection` instances instead of mutating them, forcing Avalonia to destroy and recreate visual item containers on every verified config. Due to missing `Equals`/`GetHashCode` in `FreeConfigItemViewModel`, the user's selected row is forcibly reset to index 0 on every verified finding during live searches.
2. **Allocation Churn:** Flag emoji generation (`FlagFor`) dynamically allocates multiple strings and arrays on every property read. Hex color codes are returned as raw strings rather than cached brushes or theme tokens, forcing Avalonia's brush parser to allocate new `SolidColorBrush` instances on every realized row.
3. **UI Dispatcher Saturation:** Full O(N log N) LINQ sorting, grouping, and filtering pipelines run synchronously on `Dispatcher.UIThread` during streaming verification. Background verification workers block awaiting the UI thread while holding concurrency semaphore permits, starving network I/O.
4. **Lifecycle & Token Leaks:** `CancellationTokenSource` instances allocated across multiple commands (`RefreshAsync`, `RecheckOneAsync`, `RecheckAllStaleAsync`, etc.) are never disposed. Tab switching leaves background sing-box verifiers running unchecked while pumping updates to hidden UI controls.

---

## 2. Findings Matrix

| ID | Severity | Focus Area | File:Line | Finding Summary |
|---|---|---|---|---|
| **FC-01** | **P1** | Virtualization | `FreeConfigsPageViewModel.cs:883-900, 955, 1914` | Whole `ObservableCollection` replaced per verified server, destroying virtualized containers |
| **FC-02** | **P1** | Virtualization | `FreeConfigsPageViewModel.cs:1917-1918`, `FreeConfigItemViewModel.cs:11-181` | Reference-equality failure in `Contains(SelectedItem)` resets user selection to row 0 on every probe |
| **FC-03** | **P1** | UI Stalls | `FreeConfigsPageViewModel.cs:883-900, 1826-1931` | Synchronous heavy LINQ queries and collection rebuilds executed on `Dispatcher.UIThread` |
| **FC-04** | **P1** | UI Stalls / Concurrency | `FreeConfigsPageViewModel.cs:815-912` | Background verification task awaits UI thread while holding `SemaphoreSlim` permit |
| **FC-05** | **P2** | Virtualization | `FreeConfigsPage.axaml:200-246, 337-399` | Missing `ListBoxItem` styling; default Fluent padding/MinHeight breaks 22px row layout and header alignment |
| **FC-06** | **P2** | Allocation Overhead | `FreeConfigItemViewModel.cs:47-49, 170-180` | `FlagFor` allocates multiple strings and int arrays on every property getter access without caching |
| **FC-07** | **P2** | Allocation / Tokens | `FreeConfigItemViewModel.cs:150-161`, `FreeConfigsPage.axaml:226, 364` | Raw hex strings bound to `Background` cause continuous brush parser allocations and violate theme tokens |
| **FC-08** | **P2** | Lifecycle / Leaks | `FreeConfigsPageViewModel.cs:383, 1011, 1108, 1349` | `CancellationTokenSource` instances overwritten without disposal across multiple commands |
| **FC-09** | **P2** | UI Stalls | `FreeConfigsPageViewModel.cs:1338-1341` | `SKGraphics.PurgeAllCaches()` on search completion evicts app-wide GPU font/texture caches |
| **FC-10** | **P2** | Virtualization | `FreeConfigsPageViewModel.cs:1910` | Arbitrary `Take(300)` cap masks underlying collection churn while Saved tab remains uncapped |
| **FC-11** | **P3** | Allocation Overhead | `FreeConfigItemViewModel.cs:26, 56-58, 67-80` | Uncached string interpolations on `Endpoint`, `BandwidthDisplay`, and `LatencyDisplay` |
| **FC-12** | **P3** | Visual Tree Overhead | `FreeConfigsPage.axaml:237, 369, 379-390` | Repeated `$parent` visual tree relative bindings and duplicate ToolTips on every row element |

---

## 3. Detailed Findings by Focus Area

### Focus Area 1: Virtualization of Server Cards ListBox/ItemsControl

#### FC-01 (P1): Whole ObservableCollection Replaced on Every Verified Server Finding
- **File:Line:**
  - `VPNRouter.App/ViewModels/FreeConfigs/FreeConfigsPageViewModel.cs:883-900`
  - `VPNRouter.App/ViewModels/FreeConfigs/FreeConfigsPageViewModel.cs:955`
  - `VPNRouter.App/ViewModels/FreeConfigs/FreeConfigsPageViewModel.cs:1914`
- **Symptom/Finding:**
  In `ApplyFiltersAndStats()`, `DisplayedConfigs` is updated via:
  ```csharp
  DisplayedConfigs = new ObservableCollection<FreeConfigItemViewModel>(items);
  ```
  Similarly, in `RebuildSavedDisplayList()`:
  ```csharp
  DisplayedSavedConfigs = new ObservableCollection<FreeConfigItemViewModel>(items);
  ```
  During batched deep verification (`VerifyOneAndAppendAsync`, lines 883-900), as each server passes deep verification, both methods are called in rapid succession.
  Assigning a brand new `ObservableCollection` instance forces Avalonia's `ItemsControl` to detach from the old collection, wipe its internal container generator (`ItemContainerGenerator`), destroy all realized `ListBoxItem` visual controls, and recreate them from scratch.
  This completely defeats the purpose of `VirtualizingStackPanel`. It produces visible UI flickering, scroll-bar position stuttering, high GC generation-0/1 pressure, and CPU spikes on the UI thread.
- **Concrete Actionable Fix:**
  1. Maintain single, readonly instances of `ObservableCollection<FreeConfigItemViewModel>` for both `DisplayedConfigs` and `DisplayedSavedConfigs`.
  2. Implement an in-place collection synchronization method (or batch add/remove) to mutate the existing collection rather than replacing the collection instance.
  3. During active background scans, buffer incoming verified configs and update the collection in batches (e.g. debounced at 300–500 ms intervals) instead of triggering a full collection reset for each server.

---

#### FC-02 (P1): Selection Reset to Row 0 Due to Reference Equality Failure
- **File:Line:**
  - `VPNRouter.App/ViewModels/FreeConfigs/FreeConfigsPageViewModel.cs:1917-1918`
  - `VPNRouter.App/ViewModels/FreeConfigs/FreeConfigItemViewModel.cs:11-181`
- **Symptom/Finding:**
  At line 1917 of `ApplyFiltersAndStats()`:
  ```csharp
  if (SelectedItem == null || !DisplayedConfigs.Contains(SelectedItem))
      SelectedItem = DisplayedConfigs.FirstOrDefault();
  ```
  Line 1911 constructs brand new `FreeConfigItemViewModel` instances:
  ```csharp
  .Select(c => new FreeConfigItemViewModel(c))
  ```
  Because `FreeConfigItemViewModel` does not override `Equals` or `GetHashCode`, `DisplayedConfigs.Contains(SelectedItem)` relies on default object reference equality (`object.ReferenceEquals`).
  Consequently, `!DisplayedConfigs.Contains(SelectedItem)` is **always true** whenever `ApplyFiltersAndStats()` runs. If the user clicks any row in the list while search or verification is underway, their selection is instantly wiped and yanked back to index 0 on the very next verified server.
- **Concrete Actionable Fix:**
  1. Implement `IEquatable<FreeConfigItemViewModel>` and override `Equals(object?)` and `GetHashCode()` in `FreeConfigItemViewModel.cs` using `Entry.Id` (case-insensitive) as the equality key:
     ```csharp
     public bool Equals(FreeConfigItemViewModel? other) =>
         other != null && string.Equals(Entry.Id, other.Entry.Id, StringComparison.OrdinalIgnoreCase);
     public override bool Equals(object? obj) => obj is FreeConfigItemViewModel other && Equals(other);
     public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(Entry.Id ?? string.Empty);
     ```
  2. In `ApplyFiltersAndStats()`, retain selection by matching the identifier:
     ```csharp
     var currentSelectedId = SelectedItem?.Entry.Id;
     // Update collection...
     if (currentSelectedId != null)
         SelectedItem = DisplayedConfigs.FirstOrDefault(x => string.Equals(x.Entry.Id, currentSelectedId, StringComparison.OrdinalIgnoreCase))
                        ?? DisplayedConfigs.FirstOrDefault();
     ```

---

#### FC-05 (P2): Missing ListBoxItem Theme Overrides Causing Row Height Distortion
- **File:Line:**
  - `VPNRouter.App/Views/Pages/FreeConfigsPage.axaml:200-246`
  - `VPNRouter.App/Views/Pages/FreeConfigsPage.axaml:337-399`
- **Symptom/Finding:**
  The column header strip defines a compact `Height="22"`:
  ```xml
  <Grid Grid.Row="0" Background="{DynamicResource SurfaceSunkenBrush}" ColumnDefinitions="44,*,80,80,72" Height="22">
  ```
  The row DataTemplate also sets `Height="22"`. However, unlike `ServersPage.axaml` (which explicitly overrides `ListBoxItem` padding and min-height to `0`), `FreeConfigsPage.axaml` defines no style overrides for `ListBoxItem`.
  Avalonia's FluentTheme imposes default `MinHeight="32"` (or `40`) and `Padding="12, 9"` on every `ListBoxItem`. As a result:
  - Each item container is stretched vertically, adding ~10-18px of unintended padding per row.
  - Fewer rows fit into the viewport, increasing scroll frequency.
  - Cells misalign with the static 22px header bar.
- **Concrete Actionable Fix:**
  Add a scoped style in `FreeConfigsPage.axaml`:
  ```xml
  <UserControl.Styles>
      <Style Selector="ListBox#ConfigsList ListBoxItem, ListBox#SavedConfigsList ListBoxItem">
          <Setter Property="Padding" Value="0"/>
          <Setter Property="MinHeight" Value="0"/>
          <Setter Property="VerticalAlignment" Value="Center"/>
      </Style>
  </UserControl.Styles>
  ```

---

#### FC-10 (P2): Asymmetric Arbitrary Take(300) Cap
- **File:Line:**
  - `VPNRouter.App/ViewModels/FreeConfigs/FreeConfigsPageViewModel.cs:1910`
  - `VPNRouter.App/ViewModels/FreeConfigs/FreeConfigsPageViewModel.cs:951-956`
- **Symptom/Finding:**
  In `ApplyFiltersAndStats()`:
  ```csharp
  .Take(300) // cap at 300 visible to keep ListBox responsive with emoji flags
  ```
  This hard limit of 300 was introduced as a workaround because full list rebuilds and emoji flag generation froze the UI. However:
  1. The Saved tab (`RebuildSavedDisplayList()`) does not have this cap. If a user accumulates >300 saved entries, the Saved tab pays the full unmitigated rendering penalty.
  2. With proper virtualization and cached string/brush resources, `VirtualizingStackPanel` can easily handle 5,000+ items without dropping frames.
- **Concrete Actionable Fix:**
  Resolve the underlying VM re-allocation and emoji generation bottlenecks. Once fixed, remove the artificial 300 item cap or make it a user-configurable pagination/virtualization threshold.

---

### Focus Area 2: Allocation Overhead in FreeConfigItemViewModel and Card DataTemplates

#### FC-06 (P2): Excessive String and Array Allocations in Flag Emoji Generation
- **File:Line:**
  - `VPNRouter.App/ViewModels/FreeConfigs/FreeConfigItemViewModel.cs:47-49`
  - `VPNRouter.App/ViewModels/FreeConfigs/FreeConfigItemViewModel.cs:170-180`
- **Symptom/Finding:**
  `CountryDisplay` is a dynamic getter property calling `FlagFor(Entry.CountryCode)`.
  In `FlagFor`:
  ```csharp
  public static string FlagFor(string? cc)
  {
      if (string.IsNullOrEmpty(cc) || cc.Length != 2) return "🌐";

      var upper = cc.ToUpperInvariant(); // Allocates string
      var chars = new int[2];            // Allocates heap array
      chars[0] = 0x1F1E6 + (upper[0] - 'A');
      chars[1] = 0x1F1E6 + (upper[1] - 'A');
      return char.ConvertFromUtf32(chars[0]) + char.ConvertFromUtf32(chars[1]); // Allocates 3 strings
  }
  ```
  `CountryDisplay` then concatenates:
  ```csharp
  $"{FlagFor(Entry.CountryCode)} {Entry.CountryCode}" // Allocates 5th string
  ```
  Every time a `ListBoxItem` is realized, re-measured, or scrolled into view, Avalonia evaluates this binding. For 300 items, one pass allocates 1,500 strings and arrays. Given that country codes are fixed (only ~250 worldwide), this is pure heap waste.
- **Concrete Actionable Fix:**
  Introduce a static cached lookup dictionary for flags and country labels:
  ```csharp
  private static readonly ConcurrentDictionary<string, string> FlagCache = new(StringComparer.OrdinalIgnoreCase);
  private static readonly ConcurrentDictionary<string, string> CountryDisplayCache = new(StringComparer.OrdinalIgnoreCase);

  public string CountryDisplay => GetCountryDisplay(Entry.CountryCode);

  public static string GetCountryDisplay(string? cc)
  {
      if (string.IsNullOrEmpty(cc)) return "—";
      return CountryDisplayCache.GetOrAdd(cc, code => $"{FlagFor(code)} {code.ToUpperInvariant()}");
  }

  public static string FlagFor(string? cc)
  {
      if (string.IsNullOrEmpty(cc) || cc.Length != 2) return "🌐";
      return FlagCache.GetOrAdd(cc, code =>
      {
          var upper = code.ToUpperInvariant();
          var c0 = char.ConvertFromUtf32(0x1F1E6 + (upper[0] - 'A'));
          var c1 = char.ConvertFromUtf32(0x1F1E6 + (upper[1] - 'A'));
          return string.Concat(c0, c1);
      });
  }
  ```

---

#### FC-07 (P2): Raw Hex String LatencyColor Forcing Continuous Brush Allocations and Violating Design Tokens
- **File:Line:**
  - `VPNRouter.App/ViewModels/FreeConfigs/FreeConfigItemViewModel.cs:150-161`
  - `VPNRouter.App/Views/Pages/FreeConfigsPage.axaml:226`
  - `VPNRouter.App/Views/Pages/FreeConfigsPage.axaml:364`
- **Symptom/Finding:**
  `LatencyColor` returns raw hex string literals:
  ```csharp
  public string LatencyColor => Entry.Status switch
  {
      FreeConfigStatus.Verified => "#059669",
      FreeConfigStatus.Ok when Entry.LatencyMs < 100 => "#22C55E",
      ...
  };
  ```
  In XAML, this is bound to `Border.Background="{Binding LatencyColor}"`.
  1. Avalonia's binding system converts `string` to `IBrush` via `Brush.Parse()`, instantiating a new `SolidColorBrush` object on every binding evaluation or item container realization.
  2. This violates the project design system rule (`VPNRouter.App/AGENTS.md`): *"Never hardcode hex colors in XAML; always use dynamic resources"*.
  3. These hardcoded hex colors do not react to Light/Dark theme switching, creating inconsistent visual contrast.
- **Concrete Actionable Fix:**
  Expose semantic latency classes or static immutable brushes:
  - **Option A (Static Immutable Brushes):**
    ```csharp
    private static readonly IBrush BrushVerified = new ImmutableSolidColorBrush(Color.Parse("#059669"));
    private static readonly IBrush BrushFast = new ImmutableSolidColorBrush(Color.Parse("#22C55E"));
    // Return IBrush directly — zero allocation on binding
    public IBrush LatencyBrush => ...;
    ```
  - **Option B (Recommended Design Tokens via Classes):**
    Expose `LatencyClass` (e.g. `"lat-verified"`, `"lat-fast"`, `"lat-slow"`, `"lat-failed"`) and define corresponding styles in XAML referencing `{DynamicResource SuccessSolidBrush}`, `{DynamicResource WarningSolidBrush}`, etc.

---

#### FC-11 (P3): Uncached String Interpolations in FreeConfigItemViewModel Getters
- **File:Line:**
  - `VPNRouter.App/ViewModels/FreeConfigs/FreeConfigItemViewModel.cs:26`
  - `VPNRouter.App/ViewModels/FreeConfigs/FreeConfigItemViewModel.cs:56-58`
  - `VPNRouter.App/ViewModels/FreeConfigs/FreeConfigItemViewModel.cs:67-80`
- **Symptom/Finding:**
  `Endpoint` (`$"{Entry.Host}:{Entry.Port}"`), `BandwidthDisplay` (`$"{Entry.MeasuredBandwidthMbps} Mbps"`), and `LatencyDisplay` (`$"{Entry.LatencyMs} ms ✓✓"`) are computed getters re-formatting strings on every read.
- **Concrete Actionable Fix:**
  Compute these values once and store them in readonly backing fields initialized in the constructor (or lazily initialized), since `Entry` is immutable for the lifecycle of a displayed item.

---

#### FC-12 (P3): Redundant ToolTips and Visual Tree Traversals on Every Row
- **File:Line:**
  - `VPNRouter.App/Views/Pages/FreeConfigsPage.axaml:237`
  - `VPNRouter.App/Views/Pages/FreeConfigsPage.axaml:369`
  - `VPNRouter.App/Views/Pages/FreeConfigsPage.axaml:379-396`
- **Symptom/Finding:**
  In both tabs, the bandwidth column cell contains:
  ```xml
  ToolTip.Tip="{Binding $parent[UserControl].((vm:MainWindowViewModel)DataContext).L_FcSpeedColumnTooltip}"
  ```
  In the Saved tab, the Recheck and Delete buttons contain:
  ```xml
  Command="{Binding $parent[ListBox].((vm:MainWindowViewModel)DataContext).FreeConfigsVm.RecheckOneCommand}"
  ToolTip.Tip="{Binding $parent[ListBox].((vm:MainWindowViewModel)DataContext).L_FcSavedRecheckOneTooltip}"
  ```
  Every materialized row creates ToolTip instances and performs dynamic `$parent` tree walks. For a static tooltip explaining what the column means, placing the tooltip on the column header is cleaner and avoids inflating tooltip instances for every cell.
- **Concrete Actionable Fix:**
  Move the tooltip to the header `TextBlock` in `Grid.Row="0"`/`Row="1"`.
  In `FreeConfigItemViewModel`, provide direct delegates or pass commands without deep `$parent` cast paths.

---

### Focus Area 3: UI Dispatcher Stalls During Background Verification, Sorting, and Filtering

#### FC-03 (P1): Synchronous Heavy LINQ Computations and Grouping on UI Thread
- **File:Line:**
  - `VPNRouter.App/ViewModels/FreeConfigs/FreeConfigsPageViewModel.cs:883-900`
  - `VPNRouter.App/ViewModels/FreeConfigs/FreeConfigsPageViewModel.cs:1826-1931`
- **Symptom/Finding:**
  Inside `ApplyFiltersAndStats()`:
  - 7 sequential `.Count(predicate)` LINQ scans across `_allConfigs`.
  - `.Select(c => c.CountryCode).Distinct().OrderBy()` query.
  - Full `.OrderBy(FreeConfigItemViewModel.SortKeyFor).GroupBy(c => c.Host)...` pipeline.
  - Constructing 300 ViewModels and building a new `ObservableCollection`.
  All of this runs inside `Dispatcher.UIThread.InvokeAsync` on every single verified config. During deep verification, multiple servers can be verified per second. Queuing multiple heavy LINQ runs on the UI thread causes severe UI stutter, input lag, and frame drops.
- **Concrete Actionable Fix:**
  1. Compute counts, filtering, sorting, and grouping on a background thread (`Task.Run`).
  2. Single-pass count: calculate all status counts in a single `foreach` loop instead of 7 separate LINQ passes.
  3. Dispatch only the final list of ready items to the UI thread.
  4. Throttle updates so `ApplyFiltersAndStats()` runs at most every 300 ms during live scanning.

---

#### FC-04 (P1): Background Verifier Awaiting UI Thread While Holding Concurrency Semaphore
- **File:Line:**
  - `VPNRouter.App/ViewModels/FreeConfigs/FreeConfigsPageViewModel.cs:815-912`
- **Symptom/Finding:**
  In `VerifyOneAndAppendAsync`:
  ```csharp
  await sem.WaitAsync(ct); // Line 815: acquires deepSem slot
  try
  {
      ...
      await Dispatcher.UIThread.InvokeAsync(() => // Line 883: blocks waiting for UI thread!
      {
          _allConfigs = snapshot;
          UpsertSavedConfig(cfg);
          ApplyFiltersAndStats();
          RebuildSavedDisplayList();
          ...
      });
  }
  finally
  {
      sem.Release(); // Line 911
  }
  ```
  Because `await Dispatcher.UIThread.InvokeAsync(...)` is called inside the semaphore-guarded block, if the UI thread is busy rendering or handling user input, the background task remains paused and holds its semaphore permit.
  This stalls all other worker tasks waiting on `sem.WaitAsync(ct)`, reducing deep verification throughput.
- **Concrete Actionable Fix:**
  Release the semaphore before dispatching to the UI thread, or decouple the UI update using `Dispatcher.UIThread.Post` / a channel or background queue:
  ```csharp
  // Release semaphore immediately after deep verify completes
  sem.Release();
  // Post UI update without blocking the verifier
  Dispatcher.UIThread.Post(() => ...);
  ```

---

#### FC-09 (P2): SKGraphics.PurgeAllCaches Causes App-Wide GPU Thrashing on Search Exit
- **File:Line:**
  - `VPNRouter.App/ViewModels/FreeConfigs/FreeConfigsPageViewModel.cs:1338-1341`
- **Symptom/Finding:**
  In `ReclaimPostSearchMemory()`:
  ```csharp
  SkiaSharp.SKGraphics.PurgeAllCaches();
  ```
  This is executed at the end of every search, on cancellation, and on error.
  `SKGraphics.PurgeAllCaches()` purges all cached native font glyphs, path rasters, and GPU texture atlases across the entire Avalonia application (including the main navigation bar, status icons, and other pages).
  On the subsequent frame, Avalonia must synchronously regenerate and re-upload every visible glyph and texture to the GPU, causing an immediate, noticeable visual hitch.
- **Concrete Actionable Fix:**
  Remove `SkiaSharp.SKGraphics.PurgeAllCaches()` from the per-search routine. Standard .NET GC is sufficient to reclaim memory from discarded JSON/pool objects.

---

### Focus Area 4: Tab Switching Memory Leaks, DataContextChanged Unsubscription, and Cancellation Token Cleanup

#### FC-08 (P2): Undisposed CancellationTokenSource Leaks on Repeated Runs
- **File:Line:**
  - `VPNRouter.App/ViewModels/FreeConfigs/FreeConfigsPageViewModel.cs:383`
  - `VPNRouter.App/ViewModels/FreeConfigs/FreeConfigsPageViewModel.cs:1011`
  - `VPNRouter.App/ViewModels/FreeConfigs/FreeConfigsPageViewModel.cs:1108`
  - `VPNRouter.App/ViewModels/FreeConfigs/FreeConfigsPageViewModel.cs:1349`
  - `VPNRouter.App/ViewModels/FreeConfigs/FreeConfigsPageViewModel.cs:1590`
- **Symptom/Finding:**
  In `RefreshAsync`, `RecheckOneAsync`, `RecheckAllStaleAsync`, `RetestAsync`, and `DeepVerifyTopAsync`:
  ```csharp
  _refreshCts = new CancellationTokenSource();
  ```
  The prior `_refreshCts` instance is never cancelled or disposed before being overwritten.
  Furthermore, the `finally` blocks in these methods do not dispose `_refreshCts`.
  Undisposed `CancellationTokenSource` instances retain internal timer allocations and callback registrations, contributing to memory fragmentation.
- **Concrete Actionable Fix:**
  Introduce a safe CTS disposal helper:
  ```csharp
  private void ResetCts()
  {
      var oldCts = Interlocked.Exchange(ref _refreshCts, new CancellationTokenSource());
      if (oldCts != null)
      {
          try { oldCts.Cancel(); } catch { }
          oldCts.Dispose();
      }
  }
  ```
  Dispose and null out `_refreshCts` in `finally` blocks when the operation completes.

---

#### FC-13 (P2): Background Verification Continues Pumping UI Updates When Page Hidden
- **File:Line:**
  - `VPNRouter.App/ViewModels/MainWindowViewModel.cs:496-543`
  - `VPNRouter.App/ViewModels/FreeConfigs/FreeConfigsPageViewModel.cs:883-900`
- **Symptom/Finding:**
  When a user starts a search in FreeConfigs and navigates away to another tab (e.g. Servers or Network), `MainWindowViewModel.OnSelectedTabIndexChanged` performs no check on `FreeConfigsVm.IsBusy`.
  The deep verifier continues spawning sing-box processes and executing network traffic in the background. Worse, on every verified server, it continues dispatching full collection rebuilds (`ApplyFiltersAndStats()` and `RebuildSavedDisplayList()`) to the UI thread for a hidden UserControl (`IsVisible=false`).
- **Concrete Actionable Fix:**
  When `!IsFreeConfigsTabSelected`, suspend UI collection rebuilding. Buffer newly verified servers in `_allConfigs`/`_savedConfigs` and perform a single `ApplyFiltersAndStats()` refresh when the user navigates back to Tab 5.

---

#### FC-14 (P2): Missing DataContextChanged Lifecycle Guard in FreeConfigsPage Code-Behind
- **File:Line:**
  - `VPNRouter.App/Views/Pages/FreeConfigsPage.axaml.cs:9-36`
- **Symptom/Finding:**
  `ServersPage.axaml.cs` and `SubscribePage.axaml.cs` follow a defensive pattern:
  ```csharp
  DataContextChanged += OnDataContextChanged;
  ```
  Where any prior ViewModel references or subscriptions are explicitly unwired when the DataContext is swapped (e.g. during `ReloadMainWindowForLocalization` or window recreation).
  `FreeConfigsPage.axaml.cs` lacks `DataContextChanged` handling or visual tree cleanup.
- **Concrete Actionable Fix:**
  Add standard lifecycle guarding to `FreeConfigsPage.axaml.cs` matching `ServersPage.axaml.cs`.

---

## 4. Remediation Architecture & Implementation Plan

### Phase 1: Virtualization & Selection Fixes (P1)
1. **Model Equality:** Implement `IEquatable<FreeConfigItemViewModel>` based on `Entry.Id` in `FreeConfigItemViewModel.cs`.
2. **Stable Collections:** Stop instantiating `new ObservableCollection<...>()` in `ApplyFiltersAndStats()` and `RebuildSavedDisplayList()`. Mutate existing collections in-place or use a bulk-observable collection.
3. **Selection Preservation:** Update selection lookup in `ApplyFiltersAndStats()` to search by `Entry.Id`.
4. **Item Container Sizing:** Add `ListBoxItem` style in `FreeConfigsPage.axaml` setting `Padding="0"` and `MinHeight="0"`.

### Phase 2: Dispatcher & Background Threading Offload (P1 & P2)
1. **Background Query Execution:** Move sorting, grouping, and filtering out of `ApplyFiltersAndStats()` into a background method (`Task.Run`), returning a snapshot of items to the UI thread.
2. **Batching & Throttling:** Add a 300ms debounce/throttle timer to `VerifyOneAndAppendAsync` so streaming findings don't spam the UI dispatcher.
3. **Semaphore Decoupling:** Release the `SemaphoreSlim` permit before awaiting the UI dispatcher in `VerifyOneAndAppendAsync`.

### Phase 3: Memory & Allocation Optimization (P2 & P3)
1. **Flag Caching:** Implement static `ConcurrentDictionary` caching in `FreeConfigItemViewModel.FlagFor` and `CountryDisplay`.
2. **Design Tokens for Latency:** Replace string-based `LatencyColor` with semantic XAML classes or static pre-allocated `ImmutableSolidColorBrush` instances.
3. **CTS Cleanup:** Introduce safe CTS replacement and dispose tokens in all command `finally` blocks.
4. **Remove SKGraphics Purge:** Remove `SkiaSharp.SKGraphics.PurgeAllCaches()` from `ReclaimPostSearchMemory()`.

---

## 5. Verification Checklist

- [ ] Run unit tests:
  ```powershell
  dotnet test VPNRouter.Tests/VPNRouter.Tests.csproj -c Release --filter "FullyQualifiedName~MainWindowViewModelCharacterizationTests"
  ```
- [ ] Profile memory allocations under 500+ free config test load using `dotnet-trace` or Visual Studio Diagnostic Tools.
- [ ] Confirm selection stays stable while configs trickle in during live search.
- [ ] Confirm row height is exactly 22px matching the column header.
- [ ] Verify Light and Dark theme switching renders latency badges correctly.
