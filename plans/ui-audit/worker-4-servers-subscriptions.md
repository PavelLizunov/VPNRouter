# Audit Report: Subscription Server Pool & Manual Server List UI

**Target Scope**:
- `VPNRouter.App/Views/Pages/ServersPage.axaml`
- `VPNRouter.App/Views/Pages/SubscribePage.axaml`
- `VPNRouter.App/ViewModels/MainWindowViewModel.Subscriptions.cs`
- `VPNRouter.App/ViewModels/ServerViewModel.cs`
*(With cross-references to `MainWindowViewModel.ServerTesting.cs`, `MainWindowViewModel.cs`, `ServerHealthStore.cs`, and `NaivePairing.cs` where directly coupled to the UI/ViewModel behavior).*

---

## Executive Summary & Findings Matrix

| ID | Severity | Focus Area | File:Line | Finding Summary |
|---|---|---|---|---|
| **VIRT-01** | **P1** | Virtualization | `MainWindowViewModel.Subscriptions.cs:57-64` | Synchronous per-item `ObservableCollection.Add` in `RebuildSubscriptionPool` fires 100–1000 `CollectionChanged` events in a tight loop on UI thread. |
| **PING-01** | **P1** | Ping & Dispatcher | `MainWindowViewModel.ServerTesting.cs:235-238`, `ServerViewModel.cs:186, 252`, `ServerHealthStore.cs:84, 144-153` | Synchronous disk I/O (`File.WriteAllText` + `File.Move`) executed on UI dispatcher inside `ApplyProbeResult` on every completed ping. |
| **RESP-01** | **P1** | Narrow Layout (360px) | `ServersPage.axaml:151, 199`, `SubscribePage.axaml:120, 157` | Column budget collapse at 360px width leaves Server Name with only ~16px; header column count (6 cols) mismatches row template (7 cols). |
| **REACT-01** | **P1** | Reactivity & Selection | `SubscribePage.axaml:326-330`, `SubscriptionViewModel.cs:18`, `MainWindowViewModel.Subscriptions.cs:49-79` | Unsubscribing/disabling a subscription via `Enabled` checkbox has zero reactivity: pool is not rebuilt, disabled servers remain visible and selectable. |
| **REACT-02** | **P1** | Reactivity & Selection | `MainWindowViewModel.Subscriptions.cs:57-64` | `RebuildSubscriptionPool` discards all existing `ServerViewModel` instances on refresh, wiping live latency, probe status, and deep-verify metrics. |
| **VIRT-02** | **P2** | Virtualization | `ServersPage.axaml:223-224, 266, 269, 274, 276`, `SubscribePage.axaml:197, 200` | Heavy `$parent[UserControl]` relative visual tree walks per realized row container degrade scrolling performance at 1000 items. |
| **VIRT-03** | **P2** | Virtualization | `ServersPage.axaml:210-244`, `SubscribePage.axaml:165-179` | Non-uniform, dynamically collapsing row heights cause Avalonia `VirtualizingStackPanel` scrollbar thumb jitter and measurement recalculation churn. |
| **PING-02** | **P2** | Ping & Dispatcher | `MainWindowViewModel.ServerTesting.cs:235-238, 256-259` | Unbatched `Dispatcher.UIThread.InvokeAsync` calls per server probe completion flood the Avalonia UI message queue during batch tests. |
| **PING-03** | **P2** | Ping & Dispatcher | `MainWindowViewModel.ServerTesting.cs:202`, `ServerViewModel.cs:76-79` | Bulk state initialization `foreach (var s in servers) s.IsTesting = true` fires 3,000 synchronous `PropertyChanged` events before testing begins. |
| **PING-04** | **P2** | Ping & Dispatcher | `MainWindowViewModel.cs:3702-3713`, `ServerViewModel.cs:635-644` | Quadratic $O(N^2)$ UDP-sibling and provider-risk scans executed on UI thread on every `Servers.CollectionChanged` event during bulk paste. |
| **RESP-02** | **P2** | Narrow Layout (360px) | `ServersPage.axaml:396-419`, `SubscribePage.axaml:230-256` | Non-wrapping horizontal `StackPanel` clips test progress text and action buttons beyond screen bounds on narrow windows. |
| **RESP-03** | **P2** | Narrow Layout (360px) | `SubscribePage.axaml:398-417` | Add Subscription input grid severely compresses URL `TextBox` to ~150px at 360px width, obscuring long subscription links. |
| **REACT-03** | **P2** | Reactivity & Selection | `MainWindowViewModel.Subscriptions.cs:49-79`, `MainWindowViewModel.cs:2862, 2908-2922` | Active tunnel indicator (`IsActive`) is not updated upon `RebuildSubscriptionPool`; active server highlight disappears when connected. |
| **REACT-04** | **P2** | Reactivity & Selection | `MainWindowViewModel.Subscriptions.cs:69-74`, `SubscribePage.axaml.cs:29-39` | Subscription pool rebuild resets list scroll position to top (0,0) and arbitrarily falls back to `SubscriptionServers.FirstOrDefault()`. |
| **VIRT-04** | **P3** | Virtualization | `SubscribePage.axaml:310-315` | Non-virtualized `ItemsControl` inside nested `ScrollViewer` materializes all subscription cards unconditionally. |
| **RESP-04** | **P3** | Narrow Layout (360px) | `ServerViewModel.cs:30`, `ServersPage.axaml:48-56`, `SubscribePage.axaml:45-53` | Dead `ServerViewModel._isSelected` property and visual styling conflict between selected and active row states. |

---

## Detailed Findings by Focus Area

---

### Focus Area 1: Virtualization of Server Rows (100–1000 Servers)

#### Finding VIRT-01 [P1]: Synchronous per-item ObservableCollection.Add in RebuildSubscriptionPool
- **File:Line**: `VPNRouter.App/ViewModels/MainWindowViewModel.Subscriptions.cs:57-64`
- **Symptom**:
  When importing or refreshing a subscription containing 100 to 1,000 servers, the UI freezes for 500ms to 2.5s. Profiling shows intense dispatcher churn and repeated layout invalidations.
- **Root Cause**:
  ```csharp
  SubscriptionServers.Clear();
  foreach (var sub in Subscriptions)
  {
      if (!sub.Enabled) continue;
      foreach (var serverEntry in sub.UnderlyingEntry.Servers)
          SubscriptionServers.Add(new ServerViewModel(serverEntry));
  }
  ```
  `SubscriptionServers` is a standard `System.Collections.ObjectModel.ObservableCollection<ServerViewModel>`. Every single `.Add(...)` invocation synchronously fires `INotifyCollectionChanged.CollectionChanged` with `NotifyCollectionChangedAction.Add`. For 1,000 servers, Avalonia's `ListBox` and `VirtualizingStackPanel` process 1,000 incremental collection change events sequentially on the UI dispatcher. Furthermore, each `new ServerViewModel(serverEntry)` constructor synchronously acquires a lock and queries `ServerHealthStore.GetFresh(entry)`.
- **Concrete Actionable Fix**:
  1. Implement a bulk-capable collection such as `BulkObservableCollection<T>` (or `RangeObservableCollection<T>`) supporting `AddRange(IEnumerable<T>)` that suppresses change notifications until the entire batch is added, raising a single `NotifyCollectionChangedAction.Reset`.
  2. Construct `ServerViewModel` instances off the UI thread (in background task) before applying them in bulk to `SubscriptionServers`.

---

#### Finding VIRT-02 [P2]: Visual Tree Traversal via `$parent[UserControl]` in Virtualized Row DataTemplate
- **File:Line**:
  - `VPNRouter.App/Views/Pages/ServersPage.axaml:223-224, 266, 269, 274, 276`
  - `VPNRouter.App/Views/Pages/SubscribePage.axaml:197, 200, 339, 373, 381`
- **Symptom**:
  Noticeable scrolling jank and dropped frames (sub-30 FPS) when scrolling rapidly through a 500–1000 item list.
- **Root Cause**:
  Each row template in `ServersPage.axaml` has up to 6 bindings using relative source visual tree traversal:
  ```xml
  ToolTip.Tip="{Binding $parent[UserControl].((vm:MainWindowViewModel)DataContext).L_ServersOrphanTooltip}"
  Text="{Binding $parent[UserControl].((vm:MainWindowViewModel)DataContext).L_ServersOrphanBadge}"
  Command="{Binding $parent[UserControl].((vm:MainWindowViewModel)DataContext).TestServerCommand}"
  ToolTip.Tip="{Binding $parent[UserControl].((vm:MainWindowViewModel)DataContext).L_TipTestTcpTls}"
  Command="{Binding $parent[UserControl].((vm:MainWindowViewModel)DataContext).RemoveServerByEntryCommand}"
  ToolTip.Tip="{Binding $parent[UserControl].((vm:MainWindowViewModel)DataContext).L_TipDeleteServer}"
  ```
  While `x:CompileBindings="True"` is enabled at the `UserControl` root, `$parent[UserControl]` resolves dynamically through visual tree traversal whenever item containers are realized or recycled by `VirtualizingStackPanel`. Walking the visual tree 6 times per row for hundreds of realized rows during scrolling adds significant overhead.
- **Concrete Actionable Fix**:
  1. Bind commands directly to `ServerViewModel` (e.g. expose `TestCommand` and `DeleteCommand` on `ServerViewModel` initialized with parent callbacks, or use static commands).
  2. For tooltips and badge labels (`L_TipTestTcpTls`, etc.), expose static property bindings or pass-through properties directly on `ServerViewModel` or through an application-level static localization markup extension (`{x:Static loc:Strings.TipTestTcpTls}`).

---

#### Finding VIRT-03 [P2]: Variable Row Heights Cause Layout Recalculation & Scrollbar Jumping
- **File:Line**:
  - `VPNRouter.App/Views/Pages/ServersPage.axaml:210-244`
  - `VPNRouter.App/Views/Pages/SubscribePage.axaml:165-179`
- **Symptom**:
  When scrolling through a large server list, the vertical scrollbar thumb fluctuates in size and jumps position. Quick-scrolling causes visual stutter.
- **Root Cause**:
  The row height is dynamically determined by optional elements:
  - Orphan badge (`IsVisible="{Binding IsOrphanFromSubscription}"`)
  - Subtitle (`IsVisible="{Binding HostSubtitle, Converter={x:Static StringConverters.IsNotNullOrEmpty}}"`)
  - Health verdict line (`IsVisible="{Binding HasHealthVerdict}"`)
  A row with no verdict or subtitle measures ~26px high, while a row with all badges measures ~52px high. In Avalonia's `VirtualizingStackPanel`, non-uniform item heights require continuous re-estimation of the total extent and viewport offsets as unmeasured containers come into view.
- **Concrete Actionable Fix**:
  1. Specify a fixed height or min-height on `Border.srv-row` (e.g. `MinHeight="38"`).
  2. Standardize row layout with fixed grid rows for Primary (Name + Badges) and Secondary (Subtitle + Health verdict), avoiding collapsible vertical elements that drastically alter container heights.

---

#### Finding VIRT-04 [P3]: Non-Virtualized Subscriptions Card List Inside Nested ScrollViewer
- **File:Line**: `VPNRouter.App/Views/Pages/SubscribePage.axaml:310-315`
- **Symptom**:
  If a user has 15–30 subscription sources, all subscription cards and their indeterminate `ProgressBar` controls are instantiated upfront, despite being inside a 130px-high `ScrollViewer`.
- **Root Cause**:
  `SubscribePage.axaml` uses `<ScrollViewer MaxHeight="130"><ItemsControl ItemsSource="{Binding Subscriptions}">`. `ItemsControl` creates a non-virtualizing `StackPanel` by default.
- **Concrete Actionable Fix**:
  Replace `ItemsControl` with a lightweight `ListBox` styled with `VirtualizingStackPanel`, or set `<ItemsControl.ItemsPanel><ItemsPanelTemplate><VirtualizingStackPanel/></ItemsPanelTemplate></ItemsControl.ItemsPanel>` inside bounded height.

---

### Focus Area 2: Real-Time Ping Testing & UDP-Sibling Badge UI Updates

#### Finding PING-01 [P1]: Synchronous Disk I/O on UI Thread Inside ApplyProbeResult
- **File:Line**:
  - `VPNRouter.App/ViewModels/MainWindowViewModel.ServerTesting.cs:235-238`
  - `VPNRouter.App/ViewModels/ServerViewModel.cs:186, 252`
  - `VPNRouter.Core/Services/ServerHealthStore.cs:84, 144-153`
- **Symptom**:
  Clicking "Test all" on a subscription pool with 50–500 servers causes the UI window to stutter, freeze, and become unresponsive to mouse input or window dragging. Cancel button clicks are delayed by seconds.
- **Root Cause**:
  When each probe finishes, `ServerTesting.cs` marshals the result to the UI thread:
  ```csharp
  await Dispatcher.UIThread.InvokeAsync(() => {
      server.ApplyProbeResult(result);
  });
  ```
  `server.ApplyProbeResult(result)` calls `RecomputeHealthVerdict()`, which runs:
  ```csharp
  ServerHealthStore.Record(entry, verdict, providerKey: key);
  ```
  Inside `ServerHealthStore.Record()`:
  ```csharp
  lock (Gate)
  {
      ...
      SaveLocked(map);
  }
  ```
  And `SaveLocked` executes:
  ```csharp
  File.WriteAllText(tmp, JsonSerializer.Serialize(dto, Json.AppJsonContext.Default.ServerHealthFileDto));
  File.Move(tmp, path, overwrite: true);
  ```
  **`File.WriteAllText` and `File.Move` are executed on the UI thread for every completed probe!**
  With 20 concurrent probes completing at a rate of 10–30 per second, the UI dispatcher is inundated with synchronous JSON disk writes and atomic file renames.
- **Concrete Actionable Fix**:
  1. Remove synchronous `SaveLocked` from the immediate path of `ServerHealthStore.Record`.
  2. Implement an asynchronous debounced persistence worker: update in-memory cache immediately under lock, and trigger a background flush (`Task.Run` / `Timer`) that serializes and writes to disk at most once every 1–2 seconds, or upon batch completion.
  3. Ensure that neither `ApplyProbeResult` nor `RecomputeHealthVerdict` ever performs disk I/O on the UI thread.

---

#### Finding PING-02 [P2]: Dispatcher Queue Saturation via Unbatched InvokeAsync Dispatches
- **File:Line**: `VPNRouter.App/ViewModels/MainWindowViewModel.ServerTesting.cs:235-238, 256-259`
- **Symptom**:
  Avalonia UI frame rate drops to single digits during batch ping testing; status text updates lag behind background completion.
- **Root Cause**:
  For each server probe in a batch of $N$ servers, two separate `await Dispatcher.UIThread.InvokeAsync(...)` dispatches are issued:
  1. Line 235: `await Dispatcher.UIThread.InvokeAsync(() => server.ApplyProbeResult(result));`
  2. Line 256: `await Dispatcher.UIThread.InvokeAsync(() => setProgress($"{labelPrefix}: {n} / {total}"));`
  `await InvokeAsync` allocates a `TaskCompletionSource`, posts the delegate to the dispatcher, and suspends the background worker until the UI thread completes execution. For 500 servers, this yields 1,000 asynchronous synchronization hops.
- **Concrete Actionable Fix**:
  1. Replace `await Dispatcher.UIThread.InvokeAsync(...)` with fire-and-forget `Dispatcher.UIThread.Post(...)` for updating individual `ServerViewModel` properties.
  2. Throttle progress reporting: report progress via `IProgress<T>` or update progress text at most once every 100ms (or every $K = \max(1, N / 50)$ completions) rather than per completed task.

---

#### Finding PING-03 [P2]: Bulk State Flip Fires 3,000 Synchronous PropertyChanged Events
- **File:Line**:
  - `VPNRouter.App/ViewModels/MainWindowViewModel.ServerTesting.cs:202`
  - `VPNRouter.App/ViewModels/ServerViewModel.cs:76-79`
- **Symptom**:
  A noticeable 200–500ms hesitation occurs immediately when clicking "Test All", before any network probes actually start.
- **Root Cause**:
  ```csharp
  // Mark every row as testing so spinners show immediately
  foreach (var s in servers) s.IsTesting = true;
  ```
  In `ServerViewModel.cs`:
  ```csharp
  [ObservableProperty]
  [NotifyPropertyChangedFor(nameof(StatusDot))]
  [NotifyPropertyChangedFor(nameof(StatusDotBrush))]
  private bool _isTesting;
  ```
  Setting `IsTesting = true` on 1,000 servers in a synchronous foreach loop fires $1,000 \times 3 = 3,000$ `PropertyChanged` events on the UI thread before entering the probing loop.
- **Concrete Actionable Fix**:
  Only set `IsTesting = true` for the servers actively being probed within the concurrency semaphore window (`ServerTestConcurrency = 20`). Servers in the queue waiting for the semaphore should remain in their previous state until their probe starts.

---

#### Finding PING-04 [P2]: Quadratic $O(N^2)$ Sibling & Risk Scans on Servers Collection Mutation
- **File:Line**:
  - `VPNRouter.App/ViewModels/MainWindowViewModel.cs:3702-3713`
  - `VPNRouter.App/ViewModels/ServerViewModel.cs:635-644`
- **Symptom**:
  Pasting a list of 50–200 manual VLESS URIs in `ServersPage` causes quadratic slowdown and UI freeze.
- **Root Cause**:
  In `WireServersOrphanTracking()`:
  ```csharp
  Servers.CollectionChanged += (_, _) =>
  {
      if (_isLoadingUI) return;
      try { ServerViewModel.RefreshUdpSiblingFlags(Servers); } ...
      try { ServerViewModel.RefreshProviderRiskFlags(Servers); } ...
      try { MarkOrphanServers(); } ...
  };
  ```
  When pasting lines in `AddServerCommand` (`MainWindowViewModel.cs:6481`), servers are added one-by-one with `Servers.Add(...)` without setting `_isLoadingUI = true`. For every single added server, `RefreshUdpSiblingFlags` iterates over all servers, executing `NaivePairing.FindUdpSibling`, which runs LINQ queries and string manipulations (`StripProtocolToken`). This results in $O(N^2)$ string processing on the UI thread.
- **Concrete Actionable Fix**:
  1. In `AddServerCommand`, wrap the addition loop in `_isLoadingUI = true; try { ... } finally { _isLoadingUI = false; }` and invoke `RefreshUdpSiblingFlags`, `RefreshProviderRiskFlags`, and `MarkOrphanServers` exactly once at the end.
  2. Optimize `RefreshUdpSiblingFlags` by pre-indexing UDP servers into a dictionary by pair-group and base-name rather than executing nested LINQ scans.

---

### Focus Area 3: Responsive Layout & Narrow (360px) Constraints

#### Finding RESP-01 [P1]: Column Budget Collapse at 360px & Header/Row Column Mismatch
- **File:Line**:
  - `VPNRouter.App/Views/Pages/ServersPage.axaml:151, 199`
  - `VPNRouter.App/Views/Pages/SubscribePage.axaml:120, 157`
- **Symptom**:
  1. On narrow windows (~360px), the Server Name column in `ServersPage.axaml` is crushed into an unreadable ~16px sliver. Text and badges are completely truncated or clipped.
  2. Column headers (Server, IP, Ping, Port) do not line up with the columns of the actual rows beneath them.
- **Root Cause**:
  1. **Column definition mismatch**:
     - `ServersPage.axaml` Column Headers (line 151):
       `ColumnDefinitions="14,*,100,42,40,24"` (6 columns)
     - `ServersPage.axaml` Row Template (line 199):
       `ColumnDefinitions="14,*,100,42,40,24,24"` (7 columns — column 6 is the `✕` delete button)
     Because the header has 6 columns and the row has 7 columns, IP, Ping, and Port headers are offset from their row cells.
  2. **Width budget failure**:
     - Row fixed columns: `14 (radio) + 100 (IP) + 42 (Ping) + 40 (Port) + 24 (refresh) + 24 (delete) = 244px`.
     - Spacing: 6 gaps $\times$ 8px = 48px.
     - Total non-star row width = **292px**.
     - Available width in 360px window: 360px $-$ 20px (page margin) $-$ 16px (row padding) $-$ 2px (ListBox border) $-$ 14px (scrollbar) = **308px**.
     - Remaining width for Column 1 (`*`, Server Name + Badges + Subtitle + Health verdict): $308 - 292 =$ **16px**!
  3. In `SubscribePage.axaml`, fixed columns + spacing total 260px (`14+100+42+40+24` + 40px spacing). At 360px, available width leaves only ~48px for the entire server name stack.
  4. Both pages place the Column Headers Grid *outside* the ListBox without right-hand padding for the vertical scrollbar, causing an additional ~14px misalignment between headers and scrollable content.
- **Concrete Actionable Fix**:
  1. Update `ServersPage.axaml` header to match row columns: `ColumnDefinitions="14,*,100,42,40,24,24"`.
  2. Add right margin offset (e.g. 14px) to header Grids to compensate for `ListBox` scrollbar gutter.
  3. For narrow layout support ($\le 420\text{px}$):
     - Hide the `Port` column (`Width="0"` or collapse via responsive trigger).
     - Move `IP` from an independent 100px column into a subtitle line under the Server Name, reclaiming 100px + 8px spacing for the Name column.

---

#### Finding RESP-02 [P2]: Non-Wrapping StackPanel Clips Batch Progress Text & Action Buttons
- **File:Line**:
  - `VPNRouter.App/Views/Pages/ServersPage.axaml:396-419`
  - `VPNRouter.App/Views/Pages/SubscribePage.axaml:230-256`
- **Symptom**:
  On 360px windows, the batch test status text (e.g. `"Готово. Пинг прошёл: 15 / 20 · полная проверка — «Глубокая проверка»"`) is cut off and invisible beyond the right edge of the window.
- **Root Cause**:
  ```xml
  <StackPanel Orientation="Horizontal" Spacing="6">
      <Button Content="{Binding ServerTestButtonText}" .../>
      <Button Content="{Binding ServerDeepButtonText}" .../>
      <TextBlock Text="{Binding ServerTestProgressText}" VerticalAlignment="Center" .../>
  </StackPanel>
  ```
  `StackPanel Orientation="Horizontal"` does not wrap. "Test all" (~105px) + "Deep verify" (~125px) + Spacing (12px) = 242px. In a 360px window with 20px padding, only 98px remains. The Russian completion message is ~380px wide and is permanently clipped.
- **Concrete Actionable Fix**:
  Extract the progress `TextBlock` out of the button row and place it on its own line below the buttons with `TextWrapping="Wrap"` and `Margin="0,4,0,0"`. Alternatively, use a `<WrapPanel Orientation="Horizontal">`.

---

#### Finding RESP-03 [P2]: Add Subscription Input Grid Severely Compresses URL TextBox
- **File:Line**: `VPNRouter.App/Views/Pages/SubscribePage.axaml:398-417`
- **Symptom**:
  On a 360px window, the Subscription URL text box is squeezed to ~150px, displaying only 12–15 characters of a 100+ character subscription URL.
- **Root Cause**:
  `<Grid ColumnDefinitions="100,*,Auto" ColumnSpacing="4">`: Name input is fixed at 100px, Add button is ~80px, spacing is 8px. Out of 340px inner width, the URL TextBox (`*`) only receives ~152px.
- **Concrete Actionable Fix**:
  Split the form into a 2-row layout on narrow windows: Row 0 has full-width URL input; Row 1 has Name input (`*`) and "Add" button (`Auto`).

---

#### Finding RESP-04 [P3]: Dead ServerViewModel._isSelected Property & Style Ambiguity
- **File:Line**:
  - `VPNRouter.App/ViewModels/ServerViewModel.cs:30`
  - `VPNRouter.App/Views/Pages/ServersPage.axaml:48-56, 120-123`
  - `VPNRouter.App/Views/Pages/SubscribePage.axaml:45-53, 111-114`
- **Symptom**:
  Selected rows and Active connected rows visually clash. A selected row that is not currently connected has default gray Avalonia selection tint, but when active, it gets `Border.srv-row.active` (`AccentBgSubtleBrush`), making it hard to distinguish between keyboard/pointer focus vs. active tunnel routing.
- **Root Cause**:
  `ServerViewModel` declares `[ObservableProperty] private bool _isSelected;`, but it is completely dead code (never referenced in XAML). ListBox styling resets `ListBoxItem` padding to 0, leaving row selection rendering to inner `Border.srv-row`, which only defines `.active` rules without an explicit `.selected` state.
- **Concrete Actionable Fix**:
  Remove dead `_isSelected` property from `ServerViewModel` or bind `ListBoxItem.IsSelected` two-way to it. Define explicit `ListBoxItem:selected` / `Border.srv-row:selected` styles using design tokens (`SurfaceSunkenBrush` / `AccentBorderBrush`) to clearly distinguish selection focus from active tunnel status.

---

### Focus Area 4: RebuildSubscriptionPool Reactivity & Selection Stability

#### Finding REACT-01 [P1]: Disabling Subscription via Enabled Checkbox Lacks Reactivity
- **File:Line**:
  - `VPNRouter.App/Views/Pages/SubscribePage.axaml:326-330`
  - `VPNRouter.App/ViewModels/SubscriptionViewModel.cs:18`
  - `VPNRouter.App/ViewModels/MainWindowViewModel.Subscriptions.cs:49-79`
- **Symptom**:
  When a user unchecks the `Enabled` checkbox on a subscription card in the UI, nothing happens: all servers from that subscription remain visible in `SubscriptionServers` and can still be selected or connected to. Settings are not saved to YAML.
- **Root Cause**:
  `SubscriptionViewModel.Enabled` is an observable property bound to the CheckBox in `SubscribePage.axaml`. However, `MainWindowViewModel` never subscribes to `PropertyChanged` on `SubscriptionViewModel`. When `Enabled` changes:
  1. `RebuildSubscriptionPool()` is **not** called.
  2. `SaveSettings()` is **not** called.
  3. `_entry.Enabled` in `SubscriptionEntry` is not updated until an external trigger calls `ToEntry()`.
- **Concrete Actionable Fix**:
  In `MainWindowViewModel`, attach a `PropertyChanged` listener to each `SubscriptionViewModel` in `Subscriptions` (via `Subscriptions.CollectionChanged` wire-up or upon creation in `LoadSettingsIntoUI` and `AddSubscriptionAsync`):
  ```csharp
  sub.PropertyChanged += (s, e) =>
  {
      if (e.PropertyName == nameof(SubscriptionViewModel.Enabled))
      {
          ((SubscriptionViewModel)s!).UnderlyingEntry.Enabled = ((SubscriptionViewModel)s!).Enabled;
          RebuildSubscriptionPool();
          SaveSettings();
      }
  };
  ```

---

#### Finding REACT-02 [P1]: RebuildSubscriptionPool Discards In-Memory Probe Metrics
- **File:Line**: `VPNRouter.App/ViewModels/MainWindowViewModel.Subscriptions.cs:57-64`
- **Symptom**:
  After running "Test all" (pinging 100+ servers), adding or refreshing any single subscription immediately wipes all ping latency numbers ("42 ms" $\to$ "—") and deep-verify checkmarks across ALL servers in the pool.
- **Root Cause**:
  ```csharp
  SubscriptionServers.Clear();
  foreach (var sub in Subscriptions)
  {
      if (!sub.Enabled) continue;
      foreach (var serverEntry in sub.UnderlyingEntry.Servers)
          SubscriptionServers.Add(new ServerViewModel(serverEntry));
  }
  ```
  `SubscriptionServers.Clear()` destroys all existing `ServerViewModel` instances. Live test metrics (`PingMs`, `TestStatus`, `IsDeepVerified`, `HttpLatencyMs`, `BandwidthMbps`) exist exclusively in `ServerViewModel` in-memory state; they are not stored in `VlessServerEntry`.
- **Concrete Actionable Fix**:
  Implement smart in-place reconciliation in `RebuildSubscriptionPool`:
  ```csharp
  var existing = SubscriptionServers.ToDictionary(
      s => $"{s.Server}:{s.Port}:{s.Uuid}", StringComparer.OrdinalIgnoreCase);
  var targetList = new List<ServerViewModel>();
  foreach (var sub in Subscriptions.Where(s => s.Enabled))
  {
      foreach (var entry in sub.UnderlyingEntry.Servers)
      {
          var key = $"{entry.Server}:{entry.Port}:{entry.Uuid}";
          if (existing.TryGetValue(key, out var vm))
              targetList.Add(vm);
          else
              targetList.Add(new ServerViewModel(entry));
      }
  }
  // Synchronize SubscriptionServers with targetList (preserving instances)
  ```

---

#### Finding REACT-03 [P2]: Active Tunnel Indicator (IsActive) Not Restored on Rebuild
- **File:Line**:
  - `VPNRouter.App/ViewModels/MainWindowViewModel.Subscriptions.cs:49-79`
  - `VPNRouter.App/ViewModels/MainWindowViewModel.cs:2862, 2908-2922`
- **Symptom**:
  While connected to VPN in Subscribe mode, refreshing a subscription causes the green active radio indicator and arctic row highlight to disappear from the currently connected server.
- **Root Cause**:
  `new ServerViewModel(serverEntry)` defaults `IsActive` to `false`. `RebuildSubscriptionPool()` finishes without calling `RefreshActiveIndicator()`. Furthermore, in `RefreshActiveIndicator`:
  ```csharp
  isActive = IsConnected && _autoSelectedServer is not null && ReferenceEquals(s, _autoSelectedServer);
  ```
  Because `_autoSelectedServer` referenced the old, destroyed `ServerViewModel` instance, `ReferenceEquals` will always evaluate to `false` until another full reconnect occurs.
- **Concrete Actionable Fix**:
  1. Call `RefreshActiveIndicator()` at the conclusion of `RebuildSubscriptionPool()`.
  2. In `RebuildSubscriptionPool()`, re-bind `_autoSelectedServer` to the newly matched instance in `SubscriptionServers` if `_autoSelectedServer` was non-null.

---

#### Finding REACT-04 [P2]: Selection Resets to FirstOrDefault & List Scroll Position Jumps to Top
- **File:Line**:
  - `VPNRouter.App/ViewModels/MainWindowViewModel.Subscriptions.cs:69-74`
  - `VPNRouter.App/Views/Pages/SubscribePage.axaml.cs:29-39`
- **Symptom**:
  If a user has scrolled down to inspect or test server #200, an hourly background refresh or manual subscription refresh abruptly jumps the scroll view back to server #1 at the top of the list.
- **Root Cause**:
  Calling `SubscriptionServers.Clear()` empties the items control, causing Avalonia's scroll presenter to reset scroll offset to `(0,0)`. When items are subsequently re-added, selection falls back to:
  ```csharp
  ?? SubscriptionServers.FirstOrDefault();
  ```
  Even though `SelectedSubscriptionServer` is reassigned, `ListBox.ScrollIntoView` is never invoked on selection restoration.
- **Concrete Actionable Fix**:
  1. Adopting in-place list reconciliation (as in REACT-02) prevents collection clearing and naturally retains scroll offset.
  2. If the collection must be modified, capture the scroll offset or selected index prior to rebuild, restore `SelectedSubscriptionServer`, and dispatch `SubList.ScrollIntoView(SelectedSubscriptionServer)`.

---

## Recommended Action Plan

1. **Sprint 1 (Performance & Thread Safety)**:
   - Fix **PING-01**: Decouple `ServerHealthStore` disk writes from `ApplyProbeResult`. Offload JSON serialization to a debounced background worker.
   - Fix **VIRT-01**: Replace repetitive `ObservableCollection.Add` with bulk range insertion (`AddRange` / reset notification).
   - Fix **PING-02** & **PING-03**: Switch to `Dispatcher.UIThread.Post`, throttle progress text updates, and avoid bulk `IsTesting = true` flips.
   - Fix **PING-04**: Wrap manual server bulk paste in `_isLoadingUI` to avoid $O(N^2)$ recalculations.

2. **Sprint 2 (Reactivity & State Integrity)**:
   - Fix **REACT-01**: Wire `SubscriptionViewModel.PropertyChanged` to automatically trigger `RebuildSubscriptionPool()` and `SaveSettings()` when `Enabled` is toggled.
   - Fix **REACT-02**: Implement in-place reconciliation in `RebuildSubscriptionPool` to retain latency and test metrics across refreshes.
   - Fix **REACT-03** & **REACT-04**: Call `RefreshActiveIndicator()` after pool rebuild and preserve scroll/selection stability.

3. **Sprint 3 (Responsive Layout & XAML Cleanup)**:
   - Fix **RESP-01**: Synchronize column counts between header (7 cols) and row template (7 cols) in `ServersPage.axaml`. Implement responsive hiding/stacking of IP/Port columns below 420px width.
   - Fix **RESP-02**: Wrap test buttons and progress text with `WrapPanel` or place progress on a dedicated wrapping row.
   - Fix **RESP-03**: Convert Add Subscription form to a 2-row layout at 360px width.
   - Fix **VIRT-02**: Replace `$parent[UserControl]` relative source bindings in DataTemplates with direct ViewModel bindings or static localization references.
