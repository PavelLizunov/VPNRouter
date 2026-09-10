# VPNRouter Heavy UI Elements Swarm Audit (2026-09-08)

Parallel swarm audit conducted via `gemini-swarm` across 6 heavy UI subsystems in `VPNRouter.App`.

## Audited Subsystems

| Worker | Area | Primary Files | Status | Report |
|---|---|---|---|---|
| **W-0** | Root Shell & Navigation | `MainWindow.axaml`, `MainWindow.axaml.cs`, `MainWindowViewModel.cs` | Completed | [`worker-0-shell.md`](worker-0-shell.md) |
| **W-1** | Network & Routing Rules | `NetworkPage.axaml` (2478 LOC), `NetworkPage.axaml.cs`, `MainWindowViewModel.Settings.cs` | Completed | [`worker-1-network.md`](worker-1-network.md) |
| **W-2** | Applications & Steam Catalogue | `ApplicationsPage.axaml`, `MainWindowViewModel.Profiles.cs`, `AppGroupViewModel.cs` | Completed | [`worker-2-applications.md`](worker-2-applications.md) |
| **W-3** | FreeConfigs Pool & Verifier | `FreeConfigsPage.axaml`, `FreeConfigsPageViewModel.cs`, `FreeConfigItemViewModel.cs` | Completed | [`worker-3-freeconfigs.md`](worker-3-freeconfigs.md) |
| **W-4** | Servers & Subscriptions | `ServersPage.axaml`, `SubscribePage.axaml`, `MainWindowViewModel.Subscriptions.cs` | Completed | [`worker-4-servers-subscriptions.md`](worker-4-servers-subscriptions.md) |
| **W-5** | Tools & Runtime Dispatcher | `ToolsPage.axaml`, `DpiBypassPage.axaml`, `TelegramPage.axaml`, `MainWindowViewModel.RuntimeStatus.cs` | Completed | [`worker-5-tools-runtime.md`](worker-5-tools-runtime.md) |

---

## Top P1 Critical Findings

1. **Eager Instantiation of 9 Pages at Startup (`worker-0-shell.md`)**:
   - `MainWindow.axaml` declares all 6 Advanced pages + 2 Tools sub-pages (`DpiBypassPage`, `TelegramPage`) + `SimplePage` in a single overlapping `Grid`.
   - **6,992 lines of XAML** and hundreds of controls are synchronously instantiated on the UI thread on cold start, even when launched minimized to tray (`--minimized`) or starting in Simple Mode.
   - Invisible pages remain rooted in the visual tree, processing `ObservableCollection` updates in the background.
   - **Fix**: Implement deferred/lazy page instantiation (e.g. `ContentControl` or indexed view cache instantiated on first tab selection).

2. **Synchronous UI-Thread Disk I/O Storm on Bulk App Toggles (`worker-2-applications.md`)**:
   - `SelectAll()`, `ClearAll()`, and group toggles fire `SaveSettings()` twice per item: once in `AppItemViewModel.IsChecked` setter and once in `OnAppItemPropertyChanged`.
   - Toggling 200–500 apps triggers 400–1,000 synchronous disk writes (`.bak` copy + YAML serialization) on the UI thread, freezing the UI for 3–10+ seconds.
   - **Fix**: Implement `_isBatchUpdating` guard in `MainWindowViewModel` to suppress per-item saves and persist exactly once at the end of the batch.

3. **Synchronous Directory & Manifest Scan in Steam Import (`worker-2-applications.md`)**:
   - `ImportSteamGames` executes recursive directory scans synchronously on the UI dispatcher, freezing the app for 5–15s on large libraries, followed by unbatched `ObservableCollection.Add` events.
   - **Fix**: Move Steam library scanning to background `Task.Run` and batch-insert items before dispatching to UI.

4. **Unvirtualized `ItemsControl` Allocations in Network Read Mode (`worker-1-network.md`)**:
   - `ReadModeDirectRules`, `ReadModeProxyRules`, and `ReadModeBlockRules` use `<ItemsControl>` which creates unvirtualized StackPanels, allocating up to 3,000 Grids and 12,000 TextBlocks on large rule sets.
   - **Fix**: Replace with virtualized `ListBox` or `ItemsRepeater` with `VirtualizingStackPanel`.

5. **UI Dispatcher Stalls & SKGraphics Texture Purging (`worker-5-tools-runtime.md`)**:
   - `DispatcherTimer` in `MainWindowViewModel.RuntimeStatus.cs` synchronously executes NT process enumeration and TCP table scans every 2s on the UI thread.
   - Every 60s, `SKGraphics.PurgeAllCaches()` runs unconditionally on the UI thread, purging font glyph atlases and GPU textures, causing visible frame drops and re-rasterization jank.
   - **Fix**: Offload process and TCP queries to background tasks and restrict `PurgeAllCaches` to explicit memory-pressure events.

6. **FreeConfigs Selection & Virtualization Reset (`worker-3-freeconfigs.md`)**:
   - During live verification, replacing collection instances tears down Avalonia's `ItemContainerGenerator`, destroying realized visual elements and resetting selection to row 0 due to missing `Equals`/`GetHashCode` on `FreeConfigItemViewModel`.
   - Background verifiers await the UI dispatcher while holding `SemaphoreSlim` permits, allowing UI hitches to throttle verification throughput.
   - **Fix**: Mutate collections in-place, implement proper item equality, and decouple semaphore acquisition from UI dispatcher synchronization.

7. **Bulk Ping Atomic Disk I/O on UI Thread (`worker-4-servers-subscriptions.md`)**:
   - `ApplyProbeResult` during batch ping testing calls `ServerHealthStore.Record` on the UI thread, writing and moving JSON files up to 1,000 times synchronously.
   - **Fix**: Buffer ping results in memory and flush to disk in batched background intervals.

---

## Key P2 Layout & Narrow Viewport (360px) Findings

1. **Third-Party VPN Conflict Banner Collapse (`worker-0-shell.md`)**:
   - Rigid 6-column Grid forces action buttons to consume ~300px, crushing message text to 60px or overflowing past window borders at 360px width.
   - **Fix**: Refactor banner to a 2-row layout with a wrapping button panel.

2. **NetworkPage Master Navigation Rail Starvation (`worker-1-network.md`)**:
   - Fixed 140px master navigation column leaves only ~178px for settings cards at 360px window width, clipping action buttons and confirmation dialogs.
   - **Fix**: Collapse rail to compact icon strip on narrow viewports.

3. **Telegram Proxy Overflow (`worker-5-tools-runtime.md`)**:
   - Unconstrained horizontal StackPanel with air-pill badge and \"Copy Link\" button spans 441px, clipping the copy button off-screen on 360px viewports.
   - **Fix**: Use responsive Grid or WrapPanel for header action pills.

4. **Bare-String Button and CheckBox Clipping across Pages**:
   - Detected across `NetworkPage`, `ApplicationsPage`, and `ServersPage`: buttons with localized Russian text clip without wrapping when display scaling (125%/150%) is enabled.
   - **Fix**: Wrap text in `<TextBlock TextWrapping=\"Wrap\"/>` per `audit-overflow-fix` guidelines.

5. **Theme Invalidation Broken in Converters (`worker-1-network.md`)**:
   - `ActionToTokenBrushConverter` and `BoolToBrushConverter` return static brush references from `Convert(...)` which do not update when switching Light/Dark themes.
   - **Fix**: Replace converter brush lookups with native Avalonia Style Classes and `{DynamicResource}` setters.
