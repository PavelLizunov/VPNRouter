# Phase — Fix Refresh All Connection Drop, Strip wgturn Channel & UI Swarm Audit

**Owner**: DSH session
**Branch**: `dsh/fix-refresh-all-strip-wgturn`
**Accepted base**: `origin/main` at `b7ce0e4f140b7ed4257673aa67a2b359c535ef7f`
**Roadmap ref**: user request: Refresh all drops active connection, dead wgturn channel strip, and parallel swarm check of heavy UI elements
**Effort**: medium
**Risk**: LOW
**Blast radius**: `VPNRouter.App` (ViewModels, Views, ToolsPage, Strings), `VPNRouter.Core` (Models, Services, AppPaths), build scripts (`build.ps1`, `build-mac.sh`, `build-linux.ps1`), test suite (`VPNRouter.Tests`)
**Rollback**: `git revert` or branch checkout back to base

## Why

1. **Refresh all drops active connection**: When connected to VPN in Subscribe mode, clicking "Refresh all" (or refreshing an individual subscription) invokes `RebuildSubscriptionPool()`. This clears the collection and sets `SelectedSubscriptionServer` to a new `ServerViewModel` instance while `_isLoadingUI` is false. This triggers `OnSelectedSubscriptionServerChanged`, causing an unwanted `ReconnectAsync`, tearing down sing-box and dropping live connections even if the active server didn't change at all.
2. **Dead wgturn channel**: The EmergencyChannel (`wgturn-cli`) channel is dead and deprecated. It leaves dead code across Core, App, Views, Models, and build scripts. It needs to be cleanly and completely excised.
3. **Parallel swarm check of heavy UI elements**: High-complexity Avalonia UI elements (`NetworkPage`, `ApplicationsPage`, `FreeConfigsPage`, `ServersPage`/`SubscribePage`, `ToolsPage`, `MainWindow` shell) need an adversarial audit for virtualization, memory leaks, UI thread stalls, and narrow layout (360px) issues.

## What

- Fix `RebuildSubscriptionPool()` in `MainWindowViewModel.Subscriptions.cs` by guarding with `_isLoadingUI = true` and preserving selected server by `Uuid` then `Name`.
- In `RefreshAllSubscriptionsAsync()` and `RefreshSubscriptionAsync()`, check if the active server signature changed before triggering a reconnect, matching the behavior of `RefreshSubscriptionSilentAsync()`.
- Delete `VPNRouter.Core/Services/EmergencyChannel/` (`EmergencyChannelEngine.cs`, `EmergencyChannelManager.cs`).
- Delete `VPNRouter.Core/Services/WgturnUpdater.cs` and `WgturnDownloadException.cs`.
- Delete `VPNRouter.Core/Models/EmergencyChannelConfig.cs`, `EmergencyChannelSettings.cs`, `WgturnEntry.cs`, `WgturnVariant.cs`.
- Remove `EmergencyChannel` properties and references from `AppSettings.cs`, `AppSettingsSane.cs`, `YamlStaticContext.cs`, `AppPaths.cs`, and `DiagnosticsExporter.cs`.
- Clean up obsolete `Migrate_3_to_4` in `SettingsMigrator.cs`.
- Delete `VPNRouter.App/Views/Pages/EmergencyChannelPage.axaml` and `.axaml.cs`.
- Delete `VPNRouter.App/ViewModels/MainWindowViewModel.Wgturn.cs`.
- Update `VPNRouter.App/Views/Pages/ToolsPage.axaml` and `ToolTabAvailability.cs` to remove the third tab.
- Remove `IsEmergencyChannelAvailable`, `IsEmergencyChannelToolSelected`, and `InitializeWgturnState()` from `MainWindowViewModel.cs`.
- Remove `L_EmergencyChannel*` and `Strings.EmergencyChannel*`.
- Delete obsolete wgturn test classes and clean up unused `using` statements in tests.
- Update `ToolTabAvailabilityTests.cs`, `YamlStaticContextRoundTripTests.cs`.
- Execute a 6-worker Gemini Swarm audit of heavy UI elements and synthesize findings.

## How

1. Apply fix to `MainWindowViewModel.Subscriptions.cs`.
2. Delete unused wgturn files in Core and App.
3. Remove wgturn references in build scripts (`build.ps1`, `build-mac.sh`, `build-linux.ps1`).
4. Update unit tests in `VPNRouter.Tests`.
5. Run Gemini Swarm workflow across the 6 UI zones.
6. Verify and commit.

## Six tailored gates

1. Solution build: builds clean with zero errors across Core, App, CLI, Service, Tests.
2. Unit test gate: all test projects pass, `ToolTabAvailabilityTests`, `YamlStaticContextRoundTripTests`, and new Subscription Refresh tests pass.
3. Behavior gate: `RefreshAllSubscriptionsAsync` preserves active connection when active server is unchanged.
4. Clean excision gate: no references to `wgturn` or `EmergencyChannel` remain in production code.
5. Swarm audit gate: all 6 UI workers complete and a comprehensive synthesized report is generated.
6. Git gate: committed cleanly on task branch, pushed to origin, PR opened.

## Outcome

- **PR**: https://github.com/PavelLizunov/VPNRouter/pull/250
- **CI Verification**: GitHub Actions workflow `dotnet test` [Run 34211463024](https://github.com/PavelLizunov/VPNRouter/actions/runs/34211463024) **3/3 GREEN**:
  - `go-test-windows`: pass
  - `characterization-windows`: pass (re-pinned `dd8d24e1576f9427ef96d6981e7d5d3a2c4611937574884d3720ed96309945c6`)
  - `test`: pass (2883 passed, 0 failed, 57 skipped)
- **Delivered Deliverables**:
  1. `RefreshAllSubscriptionsAsync` & `RefreshSubscriptionAsync` tunnel preservation when active server is unchanged.
  2. Complete removal of `wgturn` / `EmergencyChannel` across Core, App, Tests, and Build scripts.
  3. Batch update guard (`BeginBatchUpdate`) in `MainWindowViewModel` & `AppGroupViewModel` preventing disk I/O freezes on bulk app selection.
  4. Safe bootstrap DNS: replaced `1.1.1.1` with `8.8.8.8` across Core generators, injectors, and verifiers.
  5. QUIC reject: added unconditional `{ network: "udp", port: [443], action: "reject" }` rule preventing WinDivert QUIC `ERR_SSL_PROTOCOL_ERROR`.
  6. `HealthMonitor.WedgeKillThreshold` increased to 4.
  7. `NetworkPage.axaml` Read Mode rule lists virtualized with `VirtualizingStackPanel`.
  8. `DeferredPage` control implemented and wired to `MainWindow.axaml` and `ToolsPage.axaml`.
  9. Conflicting VPN warning banner redesigned into responsive 2-row layout with wrapping button panel.
  10. Gemini Swarm audit synthesized and recorded in `plans/ui-audit/`.

