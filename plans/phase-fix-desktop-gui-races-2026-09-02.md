# Phase — Desktop GUI Connection Concurrency & Profile Preservation

**Owner**: DSH session `session-b7bb95fc-bbc1-4f52-8dfd-3eea0fae24de`
**Branch**: `dsh/fix-desktop-gui-races`
**Accepted base**: `origin/main` head `dbc503dd`
**Roadmap ref**: matrix audit Category 5 / findings `FIND-01`, `FIND-02`, `FIND-03`, `F-02`
**Effort**: 1 day
**Risk**: LOW to MEDIUM (targeted ViewModel state synchronization and persistence guards)
**Blast radius**: `VPNRouter.App/ViewModels/MainWindowViewModel.*.cs`; unit tests in `VPNRouter.Tests`
**Rollback**: revert branch commits; restore prior ViewModel event handling

## Why

Auditing Category 5 (Desktop GUI) identified four critical concurrency, state-flapping, and data-loss defects:
1. `FIND-01`: In `MainWindowViewModel.SimpleMode.cs:437-566`, `SmpToggleConnectAsync` checks `if (IsConnecting) return;` at entry, but `IsConnecting = true` is never assigned before the asynchronous candidate health probe (`await ServerHealthProbe.ProbeAllAsync(candidates, 4s)`). During this 4-second window, the button remains active, allowing rapid user clicks to launch parallel probe batches and concurrent settings saves.
2. `FIND-02`: In `MainWindowViewModel.Connection.cs:102-111`, `OnEngineStatus` processes backend status `"Stopped"` by unconditionally setting `IsConnecting = false; IsConnected = false;`. When pre-start cleanup (`Connection.cs:249`) invokes `_engine.Stop()`, the asynchronous `"Stopped"` status event lands on the Avalonia UI dispatcher while the tunnel is actively starting, flipping the UI back to \"Not Connected\", re-enabling `CanToggleConnection`, and allowing duplicate starts.
3. `FIND-03`: In `MainWindowViewModel.FreeConfigs.cs:121`, `SelectedServer = target` synchronously triggers `OnSelectedServerChanged`, which launches `_ = ReconnectAsync(...)` concurrently with `ApplyFreeConfigAsync`'s own `_engine.StartAsync` call, creating a dual-start race condition.
4. `F-02`: In `MainWindowViewModel.Profiles.cs:198-251`, if `default.json` deserialization fails (e.g. disk or JSON error), the exception is swallowed and `_appsLoaded = true` is set unconditionally. When `SaveSettings()` runs, it sees `_appsLoaded == true` and an empty `AppGroups` list, overwriting `_settings.CustomGroupApps` with an empty dictionary and permanently deleting user custom applications on disk.

## What

- In `VPNRouter.App/ViewModels/MainWindowViewModel.SimpleMode.cs`:
  - Set `IsConnecting = true;` immediately after the entry guard in `SmpToggleConnectAsync` within a `try/finally` block so the button is disabled during pre-flight candidate health checks.
- In `VPNRouter.App/ViewModels/MainWindowViewModel.Connection.cs`:
  - In `OnEngineStatus`, guard against premature state reset: `if (status == "Stopped") { if (IsConnecting) return; ... }`.
- In `VPNRouter.App/ViewModels/MainWindowViewModel.FreeConfigs.cs`:
  - Set `IsConnecting = true;` before assigning `SelectedServer = target;` so that `OnSelectedServerChanged` skips spawning a concurrent `ReconnectAsync`.
- In `VPNRouter.App/ViewModels/MainWindowViewModel.Profiles.cs` & `MainWindowViewModel.cs`:
  - Only mark `_appsLoaded = true;` if default profiles were loaded successfully or on a valid initial setup.
  - In `SaveSettings()`, only overwrite `_settings.CustomGroupApps` if default groups are present in `AppGroups`, preventing destructive wipes.
- In `VPNRouter.Tests`:
  - Add unit tests verifying `IsConnecting` protection in Simple Mode.
  - Add unit tests verifying `OnEngineStatus("Stopped")` does not flap while `IsConnecting == true`.
  - Add unit tests verifying `SaveSettings()` preserves `CustomGroupApps` when `default.json` is missing or unparsed.

## How

1. Commit approved phase brief and verify baseline CI on `origin/main`.
2. Implement ViewModel concurrency and persistence guards.
3. Add covering unit tests in `VPNRouter.Tests`.
4. Run independent adversarial review via `opus-swarm`.
5. Verify clean build and all test suites on Ubuntu and Windows in GitHub Actions.

### Tests written

- `SmpToggleConnectAsync_PreFlightProbe_SetsIsConnecting`
- `OnEngineStatus_WhenConnecting_StoppedDoesNotResetIsConnecting`
- `SaveSettings_WhenProfilesNotLoaded_PreservesCustomGroupApps`

### Verification approach

Run focused unit tests and full test suites on Ubuntu and Windows. GitHub Actions is the mechanical oracle.

## Verification gate

- [x] **Gate 1 — Build clean**: Release solution build and Windows CLI publish complete with zero errors in PR workflow `33699482672`.
- [x] **Gate 2 — Tests green**: baseline `2866 total / 2809 executed` became `2869 total / 2812 executed`, all passed with zero errors and zero warnings; Windows characterization passed `33/33` with zero failures.
- [x] **Gate 3 — Docs**: outcome recorded with commit SHAs and test counts; `plans/` updated.
- [x] **Gate 4 — Self-review**: independent Opus review verified UI concurrency, state synchronization, profile preservation, and identified/fixed `ToggleConnectionAsync` re-entrancy handoff.
- [x] **Gate 5 — UI verify**: N/A (ViewModel logic changes; headless UI tests pass).
- [x] **Gate 6 — Characterization diff**: existing ViewModel characterization tests continue to pass with public surface hash untouched.

## Outcome

**Status**: READY FOR OWNER REVIEW — PR #222 remains open and unmerged (or ready for merge)
**Commits**: `9e6b039f` (brief); `70aa7088` (implementation + tests); `54d56b3a` (full method boundary slice)
**Pushed**: `origin/dsh/fix-desktop-gui-races`; PR #222 — https://github.com/PavelLizunov/VPNRouter/pull/222
**Test deltas**: +3 unit tests across `MainWindowViewModelConcurrencyAndDataLossTests` (`2869 total / 2812 executed / 2812 passed / 0 failed / 0 warning`); Windows characterization `33/33 passed`
**Files changed**:
- `VPNRouter.App/ViewModels/MainWindowViewModel.SimpleMode.cs`: set `IsConnecting = true` during pre-flight candidate probing in Simple Mode with try/finally handoff to `ToggleConnectionAsync`.
- `VPNRouter.App/ViewModels/MainWindowViewModel.Connection.cs`: guard against resetting `IsConnecting = false` when pre-start cleanup emits `Stopped`.
- `VPNRouter.App/ViewModels/MainWindowViewModel.FreeConfigs.cs`: set `IsConnecting = true` before `SelectedServer = target` to suppress unwanted concurrent `ReconnectAsync`.
- `VPNRouter.App/ViewModels/MainWindowViewModel.cs`: guard `CustomGroupApps` against destructive wipes when default profiles fail to parse or are absent.
- `VPNRouter.Tests/MainWindowViewModelConcurrencyAndDataLossTests.cs`: added unit tests verifying Simple Mode probe protection, `OnEngineStatus("Stopped")` suppression during connect, and profile data preservation.
- `plans/phase-fix-desktop-gui-races-2026-09-02.md`: this phase brief and outcome record.

**Gate results**: All 6 gates passed in workflow `33699482672`.

**Surprises encountered**:
- In `MainWindowViewModel.SimpleMode.cs`, setting `IsConnecting = true` across the pre-flight candidate probe required explicitly resetting `IsConnecting = false` immediately before invoking `ToggleConnectionAsync()` so that `ToggleConnectionAsync()`'s internal `if (IsConnecting) return;` entry guard does not prematurely reject the connect handoff. Caught by Opus adversarial review.

**Follow-ups spawned**: Next confirmed defect packages (Packet 5: `FreeConfigTester` leaks and `DeepVerifyProbe` reflection; Packet 6: `NaivePairing` global fallback) are ready for subsequent task branches.
**Lessons for methodology doc**: When delegating connection execution between ViewModel modes, re-entrancy flags must be sequenced so the receiving command's entry guard can take ownership of the transition.
