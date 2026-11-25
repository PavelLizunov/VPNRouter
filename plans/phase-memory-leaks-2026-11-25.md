# Phase: Fix desktop and core memory leaks (1+ GB RAM growth)

- **Date:** 2026-11-25
- **Branch:** `dsh/fix-memory-leaks-20261125`
- **Base:** `origin/main` (`ff2f612a`, tag `v2.51.1-r1`)
- **Goal:** Eliminate all confirmed unbounded memory leaks, Large Object Heap (LOH) churn, unmanaged/COM handle leaks, and unbounded `sing-box` Go heap growth causing 1+ GB RAM usage in long-running desktop sessions.

## Confirmed root causes

1. **[P0] `PictogramText` foreground binding leak & O(N^2) `PropertyChanged` LOH churn (`VPNRouter.App/Controls/PictogramText.cs:118`):**
   - Every `PictogramText.Update(TextBlock)` creates `new Pictogram` controls and calls `icon.Bind(Pictogram.ForegroundProperty, control.GetObservable(TextBlock.ForegroundProperty))` while discarding the `IDisposable`.
   - Each call appends a permanent delegate to `control.PropertyChanged`, retaining every abandoned `Pictogram` + `InlineUIContainer` forever and forcing `MulticastDelegate` O(N^2) LOH array reallocations on `SimplePage` (`ConnectionStatsText`, 2s tick), `TelegramPage` (`TgProxyStats`), `MainWindow` (`StatusText`), and `FreeConfigsPage`.
   - `ContentProperty` and `TipProperty` also allocate a fresh child `TextBlock` on every change.

2. **[P0] Periodic ~80-120 MB LOH allocation spikes in `DiagnosticsExporter.TailLines` + `ProblemDetector` + `HealthCheck`:**
   - Every 3 minutes while connected, `MainWindowViewModel.Problems.cs` tails 4,000 lines of `sing-box.log` via `DiagnosticsExporter.TailLines`, which reads up to 12 MiB with `ReadToEnd()`, duplicates it with `.Replace("\r\n", "\n")`, allocates 100k+ strings via `.Split('\n')`, copies arrays, and re-splits in `ProblemDetector.CountProxiedGamePorts` and `HealthCheck.CountProxyDialTimeouts`.
   - `_problemScanCts` is cancelled without disposal.

3. **[P0] Unbounded `sing-box` Go heap (`VPNRouter.Core/Services/SingBoxManager.Lifecycle.cs`, `FreeConfigDeepVerifier.cs`, `VlessDeepVerifier.cs`):**
   - `sing-box` spawns with no `GOMEMLIMIT`, allowing Go GC to grow RSS to 500 MB - 1.5+ GB under sustained TUN / Clash API connection churn.

4. **[P1] Windows ETW 64 MB kernel/user buffers, unused `ProcessStop` allocations, and Stop/Dispose race (`VPNRouter.Core/Services/EtwProcessMonitor.cs`, `StartupPipeline.cs`):**
   - `TraceEventSession` defaults to 64 MB buffers; 4 MB is ample for process start events.
   - `ProcessStopped` has zero subscribers yet allocates `ProcessEventArgs` on every system process exit; Full Tunnel mode (`profile.Processes.Count == 0`) runs an ETW session with zero matching targets.
   - `Stop()`/`Dispose()` races with `RunSession()` before `_sessionReady` (can orphan an active ETW session or hit `ObjectDisposedException` on `_sessionReady.Set()`).

5. **[P1] Resource ownership, disposal, and Skia cache gaps (`VpnEngine.cs`, `VpnEngine.StartupHost.cs`, `ConfigSanityCheck.cs`, `MainWindowViewModel.cs`, `MainWindowViewModel.ServerTesting.cs`, `MainWindowViewModel.AssistantRepair.cs`, `App.axaml.cs`, `MainWindow.axaml.cs`, `AboutWindow.axaml.cs`, `MainWindowViewModel.ThemeAndLogo.cs`):**
   - `VpnEngine.Dispose()` skipped `_healthMonitor`, `_etw`, `_connHealthStream`, and `_sanityCheck` when `_singBox == null`.
   - `ConfigSanityCheck` owns an `HttpClient` without implementing `IDisposable`, and `VpnEngine.StartupHost.OnFailoverRequested` allocated a fresh `ConfigSanityCheck` on every failover instead of reusing `_engine._sanityCheck`.
   - `MainWindowViewModel` opened a duplicate Serilog file sink to `vpnrouter.log` even when `Program.Main` already initialized `Serilog.Log.Logger`, and `MainWindowViewModel.Dispose()` did not dispose `_engine`'s owned resources, `_ownedLogger`, `_problemScanCts`, or cancel `_serverTestCts`/`_serverDeepCts`.
   - `App.axaml.cs` did not cap `SKGraphics.SetResourceCacheTotalByteLimit`, leaked `AssetLoader.Open` streams for tray icons and logo, `MainWindow` did not unhook `PropertyChanged` on `Closed`, and `AboutWindow` did not dispose its decoded logo `Bitmap` on `Closed`.

6. **[P1] WMI COM leaks & `TunOwnershipLock` 200ms forever polling (`ProcessScanner.cs`, `SplitTunnelDriverManager.Service.cs`, `TunOwnershipLock.cs`, `ProcessOwnership.cs`):**
   - `ManagementObjectSearcher.Get()` collections and `ManagementObject` instances were not disposed in `ProcessScanner` and `SplitTunnelDriverManager.Service`.
   - `TunOwnershipLock.StartOwnerRecordMonitor` ran a full OS process scan (`GetProcessesByName` + `NtQueryInformationProcess`) + double disk read every 200ms forever even after publishing the active child PID.

7. **[P2] `FreeConfigs` disposal hygiene & bounded GeoIP cache (`FreeConfigAggregator.cs`, `FreeConfigPoolFetcher.cs`, `FreeConfigGeoIp.cs`, `FreeConfigTester.cs`, `FreeConfigDeepVerifier.cs`, `FreeConfigsPageViewModel.cs`):**
   - Undisposed `SemaphoreSlim`s in `FreeConfigTester`, `FreeConfigDeepVerifier`, `VlessDeepVerifier`, `MainWindowViewModel.ServerTesting`, and `FreeConfigsPageViewModel`.
   - `FreeConfigAggregator`, `FreeConfigPoolFetcher`, and `FreeConfigGeoIp` owned `HttpClient`s and `SemaphoreSlim` without implementing `IDisposable`; `_ipToCountry` had no upper bound; `_refreshCts` was not disposed across runs.

## Scope & invariants

- Preserve `MainWindowViewModel` public surface (do not add/rename/remove public members).
- Preserve `process_name` casing and Windows Service coexistence (`DisposeOwnedSession()` must not restore DNS when another owner holds the tunnel).
- Preserve `VpnEngineLifecycleTests` monitor lifecycle expectations (`etw.Start()` still called; `EtwProcessMonitor` skips kernel session only when zero subscribers exist) and `SingBoxManagerProcessRunnerTests` / `ServiceAppCoexistenceTests` contracts.
- Keep `#if PLATFORM_WINDOWS` guards cleanly compiling on both `ubuntu-latest` and `windows-latest`.

## Verification plan

- Add targeted regression tests in:
  - `VPNRouter.Tests/PictogramTextTests.cs` (GC collectability after repeated `SetText` + direct/inherited `Foreground` propagation + `Content`/`Tip` TextBlock reuse)
  - `VPNRouter.Tests/DiagnosticsExporterTailBoundedTests.cs` (streaming ring-buffer edge cases: CRLF/LF, seek mid-line, single long line, custom `maxReadBytes`, empty file)
  - `VPNRouter.Tests/EtwProcessMonitorTests.cs` (4 MiB buffer constant, zero-subscriber no-op Start, Stop/Dispose idempotency)
  - `VPNRouter.Tests/SingBoxManagerProcessRunnerTests.cs` (`GOMEMLIMIT` default env override and preservation of caller override)
  - `VPNRouter.Tests/VpnEngineLifecycleTests.cs` (`VpnEngine.Dispose()` disposes monitors/sanity check when `_singBox == null`, `DisposeOwnedSession()` preserves service DNS when idle, `ConfigSanityCheck` disposal)
  - `VPNRouter.Tests/ProblemDetectorTests.cs` (CRLF and large-input streaming parity)
- Verify via GitHub Actions CI (`test.yml`: Linux + Windows + Headless) at exact PR head SHA; zero local `dotnet build`/`dotnet test` runs on the workstation.
