# Phase: GC memory conservation, hidden tray UI shedding, and RAM telemetry (Workstream 2A)

- **Date:** 2026-11-25
- **Branch:** `dsh/memory-gc-tray-telemetry-20261125`
- **Base:** `origin/main` (`4e8aa796`, with PR #735 merged)
- **Goal:** Reduce steady-state and tray-idle RAM footprint across desktop, headless, and service hosts via Workstation GC conservation settings, deferred inactive-page unloading on tray hide/minimize, reduced hidden-window polling cadence, cached fallback brushes, and safe process RAM telemetry in diagnostics exports.

## Scope

1. **Workstation GC memory conservation (`.csproj` host configuration):**
   - `VPNRouter.App/VPNRouter.App.csproj`
   - `VPNRouter.Headless/VPNRouter.Headless.csproj`
   - `VPNRouter.Service/VPNRouter.Service.csproj`
   - Declare `RuntimeHostConfigurationOption` entries `System.GC.ConserveMemory=9` and `System.GC.RetainVM=false` while preserving Workstation GC default (`ServerGarbageCollection` stays unset/false).

2. **Hidden-window / tray memory shedding and `DeferredPage` unloading:**
   - `VPNRouter.App/Controls/DeferredPage.cs`: add `internal void UnloadIfInactive()` to clear `Content = null` when `!IsActive && Content != null` so `UpdateState(true)` lazily re-instantiates `PageType` on future activation.
   - `VPNRouter.App/Views/MainWindow.axaml.cs`: add `internal int ReleaseInactivePages()` to snapshot logical and visual descendant `DeferredPage` controls and unload inactive ones, returning the released count.
   - `VPNRouter.App/Services/TrayMemoryTrimmer.cs` and `VPNRouter.App/App.axaml.cs`: add a debounced 15-second `DispatcherTimer` triggered on `HideToTray`, window hide, or `WindowState.Minimized`. Restoring the window cancels any pending timer and calls `_viewModel?.ForceRefreshRuntimeStatus()`. When the timer fires while still hidden/minimized, it calls `mainWindow.ReleaseInactivePages()`, `SkiaSharp.SKGraphics.PurgeAllCaches()`, and schedules a background Gen2+LOH compacting collection (`Task.Run` with `GCLargeObjectHeapCompactionMode.CompactOnce` and `GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true)` — note: CoreCLR throws `ArgumentException` if `GCCollectionMode.Aggressive` is called with `blocking: false`; running it inside `Task.Run` keeps the UI thread non-blocking while allowing actual Gen2+LOH compaction and OS decommit). Timers and event handlers are cleaned up on `desktop.Exit` and `MainWindow.Closed`.

3. **Hidden-window polling reduction and cached fallback brushes:**
   - `VPNRouter.App/ViewModels/MainWindowViewModel.RuntimeStatus.cs`:
     - Raise hidden-window `_runtimeSkipRemaining` backoff from `4` (10s) to `14` (30s).
     - Cache the three fallback brushes in `BadgeBrush` as `private static readonly IBrush` instances using thread-safe `ImmutableSolidColorBrush` instead of allocating `new SolidColorBrush(...)` per fallback lookup.

4. **Process RAM telemetry in diagnostics summary:**
   - `VPNRouter.Core/Services/Diagnostics/DiagnosticsExporter.cs`:
     - Add a `──── Memory ────` section to `BuildSummary` reporting current process PID, WorkingSet, PrivateMemory, ManagedGC (`GC.GetTotalMemory(false)`), GCCommitted (`GC.GetGCMemoryInfo().TotalCommittedBytes`), and Gen0/1/2 collection counts.
     - Report any running `sing-box` / `sing-box-lx` process PID, WorkingSet, and PrivateMemory (disposing every `Process` handle in `finally`), with no paths, command lines, or secrets.

5. **Regression tests:**
   - `VPNRouter.Tests/DeferredPageMemoryTests.cs`: verifies `DeferredPage.UnloadIfInactive()`, `MainWindow.ReleaseInactivePages()`, and `TrayMemoryTrimmer` cancel/restore/trim/close behavior under `[AvaloniaFact]`.
   - `VPNRouter.Tests/RuntimeConfigMemoryTests.cs`: verifies all three host `.csproj` files declare `System.GC.ConserveMemory=9` and `System.GC.RetainVM=false` and do not enable `ServerGarbageCollection`.
   - `VPNRouter.Tests/DiagnosticsExporterTests.cs`: verifies exported `summary.txt` includes the `Memory` section (`WorkingSet`, `ManagedGC`) with zero secrets.

## Invariants

- Preserve `MainWindowViewModel` public API surface 100% unchanged (no added, renamed, or removed public members).
- Keep `#if PLATFORM_WINDOWS` guards compiling cleanly on both `ubuntu-latest` and `windows-latest`.
- Ensure unloading inactive `DeferredPage` controls preserves all state (which lives in `MainWindowViewModel`), unsubscribes page event handlers on `DataContextChanged` / detach, and leaves `VisualDiffTests`, `HeadlessGuiTests`, and `PageScreenshotTests` intact.
- Do not touch `plans/OPEN-DEFECTS.md` or remove existing files under `plans/`.
- Do not add emoji or leak secrets/tokens/UUIDs/paths in telemetry, code, or docs.

## Verification

- Local workstation rule: no `dotnet build` or `dotnet test` executed locally.
- Full verification via GitHub Actions CI (`test.yml`: Linux + Windows + Headless) on the exact PR head SHA.
