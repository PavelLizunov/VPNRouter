# Phase 1 Audit Remediation — P06 Smart Connect Persistence

**Owner**: Qwen Code (implementation engine); orchestrator handles Git
**Branch**: `codex/qwen-audit-p06-smart-connect-persistence-2026-07-29` (off current `origin/main`)
**Audit source**: `plans/qwen-full-app-audit-2026-07-28/RESULTS.md` (PR #48)
**Adjudication**: `plans/qwen-audit-independent-verification-2026-07-28.md` (P00, commit `b39a28c3`)
**Effort**: ~2-3 h
**Risk**: MEDIUM (Smart Connect is the primary simple-mode connect path; must not break manual server selection or advanced mode)
**Blast radius**: 1 App product file (`MainWindowViewModel.SimpleMode.cs`) + tests
**Rollback**: `git revert <commit>` / branch delete

## Findings in scope

| ID | Orig | P00 Verdict | Final | Confidence |
|---|---|---|---|---|
| FLOW-1 | P1 | CONFIRMED | **P1** | High |

ONLY FLOW-1. Explicitly NOT in scope: UI-1 (update localization, P3), UI-2
(narrow rule layout, P2) — separate packages.

## Execution constraint (overrides methodology gates)

All implementation is performed through Qwen Code. Qwen may read/search/edit code
and write tests, but MUST NOT run local builds, tests, applications, binaries,
services, installers, package restore, VM/WinRM/ADB/MCP/live checks, downloads,
or platform mutations. Validation happens ONLY in remote GitHub CI after the
orchestrator pushes the branch. **Qwen MUST NOT commit or push** — the orchestrator
reviews the diff and handles Git.

## Why

Smart Connect probes all subscription servers, picks the best live winner via
`ConnectionIntentScorer.PickServer`, stores it, saves settings, and connects.
But `SaveSettings` immediately re-derives `ActiveSubscriptionServer` from the
stale `SelectedSubscriptionServer` VM property (which Smart Connect never
updates), overwriting the probed winner. The engine then connects to the
stale/dead entry. The branch fires precisely when the active server is dead
(the exact case Smart Connect exists for), so the feature's primary use case
is silently defeated.

## Current root cause (verified against current code)

- [FACT] `VPNRouter.App/ViewModels/MainWindowViewModel.SimpleMode.cs:519` —
  `var chosen = ConnectionIntentScorer.PickServer(...)` selects the winner.
- [FACT] `:532` — branch fires when `chosen.Name != ActiveSubscriptionServer`
  (i.e., the current active is dead/suboptimal).
- [FACT] `:536` — winner stored: `_settings.App.ActiveSubscriptionServer = chosen.Name;`
- [FACT] `:537` — `SaveSettings();` called immediately after.
- [FACT] `:542` — winner shown in status text only (UI display, not the
  selected-VM property).
- [FACT] `:555` — `ToggleConnectionAsync()` starts the connection.
- [FACT] `VPNRouter.App/ViewModels/MainWindowViewModel.cs:3692` — `private void SaveSettings()`.
- [FACT] `:3782-3783` — SaveSettings re-derives:
  `var activeSub = SelectedSubscriptionServer ?? SubscriptionServers.FirstOrDefault();`
  then `_settings.App.ActiveSubscriptionServer = activeSub?.Name ?? "";`
  **This overwrites the winner stored at SimpleMode.cs:536.**
- [FACT] Smart Connect never sets `SelectedSubscriptionServer` — grep confirms
  no assignment from the Smart Connect path.
- [FACT] `MainWindowViewModel.cs:4252-4263` — `ToggleConnectionAsync` calls
  `SaveSettings()` again, reloads, and hands
  `_settings.Vless.ActiveServer = _settings.App.ActiveSubscriptionServer`
  (the stale name) to the engine.
- [FACT] `ConnectionIntentScorer.cs:26-41` — scoring logic picks the best
  live server; `ServerHealthProbe.cs:94-106` — probe determines liveness.
- [INFER] The fix must update `SelectedSubscriptionServer` to match the winner
  BEFORE `SaveSettings()` runs, so the re-derivation in SaveSettings produces
  the correct value. This is the minimal single-point fix.

## What

### Minimal expected file list
- `VPNRouter.App/ViewModels/MainWindowViewModel.SimpleMode.cs` — update
  `SelectedSubscriptionServer` from the Smart Connect winner before SaveSettings.
- `VPNRouter.Tests/SmartConnectPersistenceTests.cs` (new test class).

### Explicit non-goals
- Do NOT refactor `SaveSettings` or change its re-derivation logic (it serves
  100+ callers correctly for manual selection).
- Do NOT touch `ConnectionIntentScorer`, `ServerHealthProbe`, or the probe pipeline.
- Do NOT modify `ToggleConnectionAsync` or the engine start path.
- Do NOT fix UI-1 (localization) or UI-2 (narrow layout) — separate packages.
- Do NOT add a new property or event for Smart Connect selection.
- Do NOT change advanced-mode server selection behavior.

## How (ordered; fix the shared root cause once)

1. In `SimpleMode.cs`, after the winner is chosen (`:536`) and BEFORE
   `SaveSettings()` (`:537`), set the UI-bound selected server to match:
   ```csharp
   SelectedSubscriptionServer = SubscriptionServers
       .FirstOrDefault(s => s.Name == chosen.Name);
   ```
   This ensures `SaveSettings`'s re-derivation at `:3782` reads the winner
   from `SelectedSubscriptionServer` and persists it correctly.

2. Guard: if `SubscriptionServers` does not contain the winner (e.g., the
   winner came from a different source), fall back to the existing behavior
   (the `_settings.App.ActiveSubscriptionServer` assignment at `:536` still
   runs, and SaveSettings's `FirstOrDefault` fallback applies). The guard is
   a null-check on the `FirstOrDefault` result — if null, do not assign
   `SelectedSubscriptionServer` (leave it unchanged).

3. Preserve the existing `_settings.App.ActiveSubscriptionServer = chosen.Name;`
   at `:536` — it is the canonical store; the `SelectedSubscriptionServer`
   assignment synchronizes the VM state so SaveSettings does not clobber it.

Why minimal/correct: `SaveSettings` re-derives from `SelectedSubscriptionServer`
by design (it is the single source of truth for 100+ callers). The bug is that
Smart Connect writes the model directly without synchronizing the VM property.
Updating the VM property before SaveSettings aligns Smart Connect with every
other server-selection path (manual ComboBox selection, subscription refresh,
free-config apply) that all set `SelectedSubscriptionServer` before saving.

## Callers / consumers to preserve

| What | Where | Note |
|---|---|---|
| Smart Connect flow | `SimpleMode.cs:490-560` | the fixed path |
| `SaveSettings` | `MainWindowViewModel.cs:3692` | 100+ callers; re-derivation at `:3782` unchanged |
| `SelectedSubscriptionServer` setter | `MainWindowViewModel.cs` (ObservableProperty) | set by ComboBox selection, subscription refresh, free-config apply |
| `ToggleConnectionAsync` | `MainWindowViewModel.cs:4252` | re-saves + reloads; reads `ActiveSubscriptionServer` |
| `ConnectionIntentScorer.PickServer` | `ConnectionIntentScorer.cs:26` | pure scorer; unchanged |
| `ServerHealthProbe` | `ServerHealthProbe.cs:94-106` | probe; unchanged |
| Manual server ComboBox | `SimplePage.axaml` binding | sets `SelectedSubscriptionServer` on user click |
| `ActiveSubscriptionServer` model field | `AppSettings.App` | persisted to YAML |
| `_settings.Vless.ActiveServer` | set from `App.ActiveSubscriptionServer` in ToggleConnectionAsync | engine input |

Existing helpers to reuse: `SubscriptionServers` ObservableCollection (already
in scope at `:536`); `SelectedSubscriptionServer` ObservableProperty (already
exists, set by other paths).

## Regression tests (exact)

New `VPNRouter.Tests/SmartConnectPersistenceTests.cs` — match existing VM test
conventions (`ViewModelTests.cs` pattern; `[AvaloniaFact]` for VM construction
on the Avalonia dispatcher thread):

- `SmartConnect_WinnerSurvivesSaveSettings` — **core FLOW-1 pin.** Construct
  `MainWindowViewModel` with a populated subscription (2+ servers, one dead).
  Simulate Smart Connect picking a winner different from the current
  `SelectedSubscriptionServer`. Call the Smart Connect handler. Assert
  `_settings.App.ActiveSubscriptionServer` equals the winner's name AFTER
  `SaveSettings` runs (not just before). Assert `SelectedSubscriptionServer.Name`
  equals the winner.

- `SmartConnect_DeadPreviousSelection_CannotOverwriteWinner` — set
  `SelectedSubscriptionServer` to a dead server. Run Smart Connect (winner is
  a different live server). Assert the persisted `ActiveSubscriptionServer`
  is the winner, not the dead server.

- `SmartConnect_WinnerReachesEngineInput` — after Smart Connect +
  ToggleConnectionAsync, assert `_settings.Vless.ActiveServer` equals the
  winner's name (the engine receives the correct server).

- `ManualServerSelection_UnchangedBySmartConnectFix` — set
  `SelectedSubscriptionServer` manually (simulating ComboBox), call
  `SaveSettings`. Assert `ActiveSubscriptionServer` matches the manual
  selection. Pins that the fix does not regress the normal manual path.

Must stay green: `ViewModelTests.cs` (all), `HeadlessGuiTests.cs`,
`MainWindowViewModelCharacterizationTests.cs` (characterization hash unchanged
unless the test surface changes).

## Risks

- **Compatibility**: the fix adds one `SelectedSubscriptionServer` assignment
  in the Smart Connect path. All other SaveSettings callers are unaffected.
  The `SelectedSubscriptionServer` setter raises `PropertyChanged` (MVVM Toolkit
  `[ObservableProperty]`), which may trigger a ComboBox visual update — this is
  correct behavior (the UI should show the winner as selected).
- **Cross-platform**: `SimpleMode.cs` is cross-platform Avalonia; no OS-specific code.
- **Rollback**: single-file product change; trivial revert. No schema/migration/state.
- **Characterization**: the MVM characterization hash may change if the test
  exercises the Smart Connect path (it currently does not). If the hash changes,
  verify it is solely due to the new `SelectedSubscriptionServer` assignment.

## Dependencies and file overlap with the other seven packages

- **P02 (FAIL-1)**: P02 touches `VpnEngine.cs` internal failover dispatch;
  FLOW-1 touches `MainWindowViewModel.SimpleMode.cs`. P02's brief notes P06
  may be near `VpnEngine.cs` vicinity — but the actual fix is in `SimpleMode.cs`,
  a different file. No file overlap.
- **P05 (DATA-1)**: P05 changes `SettingsLoader.Save` implementation (atomic
  write). FLOW-1 calls `SaveSettings` → `ISettingsStore.Save` → `SettingsLoader.Save`.
  P05 changes the IMPLEMENTATION not the signature/contract, so P06 is unaffected
  and benefits. Sequence-independent.
- **P01 (UPD-1/UPD-2)**: no overlap (update/repair files).
- **P07 (CLI/Android)**: no overlap (CLI/Android files).
- **P08 (SUP-1)**: no overlap (CI workflow).
- **P09 (SEC/OBS)**: no overlap (logging/ACL files).
- **P10 (ZAP-1)**: no overlap (ZapretUpdater).
- No blocking dependency on any other package.

## Zone CLAUDE.md constraints (`VPNRouter.App/CLAUDE.md`)

- `MainWindowViewModel` is a ~7250-line god-file split into 10 partials;
  `SimpleMode.cs` is the simple-mode partial. The fix stays within this partial.
- MVVM Toolkit `[ObservableProperty]` pattern: `SelectedSubscriptionServer`
  is already an observable property; assigning it raises `PropertyChanged`.
- `SaveSettings` is the single source of truth for YAML persistence from the VM;
  do not bypass it.
- No emoji (AGENTS.md #9).
- `InternalsVisibleTo VPNRouter.Tests` configured; internal VM members testable.
- `[AvaloniaFact]` required for tests that construct the VM (dispatcher thread).

## Verification gate (remote-only, tailored)

- [ ] **Gate 1 — Build (remote CI only)**: orchestrator pushes branch; CI compiles 0 errors. Qwen does NOT build locally.
- [ ] **Gate 2 — Tests (remote CI only)**: new `SmartConnectPersistenceTests` green in CI; full existing suite stays green (ViewModelTests, HeadlessGuiTests, characterization included).
- [ ] **Gate 3 — Docs**: brief Outcome filled after CI; no README change expected.
- [ ] **Gate 4 — Self-review**: Qwen static self-review of the diff (VM state synchronization change).
- [ ] **Gate 5 — UI/live**: DEFERRED by explicit owner constraint (no local launch/MCP/VM). Do NOT fake PASS. Note "deferred — Smart Connect path not live-verified" in Outcome.
- [ ] **Gate 6 — Characterization**: verify MVM characterization hash; if changed, confirm the delta is solely the new `SelectedSubscriptionServer` assignment.

## Outcome

**Status**: IMPLEMENTED / REMOTE CI GREEN
**Commits**: `70cb3a8a` (fix(app): persist Smart Connect winner)
**Pushed**: draft PR #56, branch `codex/qwen-audit-p06-smart-connect-persistence-2026-07-29`
**Test deltas**: +77 / -0 (1 new test file: `SmartConnectPersistenceTests.cs` +77)
**Files changed**: 2 · +83 / -0

**Gate results:**
- [x] Gate 1 build (remote CI): PASS — dotnet test run 30444041090 SUCCESS
- [x] Gate 2 tests (remote CI): PASS — run 30444041090 SUCCESS; new `SmartConnectPersistenceTests` green; full existing suite (ViewModelTests, HeadlessGuiTests, characterization) stayed green
- [x] Gate 3 docs: PASS — Outcome filled; no README change needed
- [x] Gate 4 self-review: PASS — static self-review performed during implementation; VM state synchronization change reviewed
- [-] Gate 5 UI/live: deferred (owner constraint) — Smart Connect path not live-verified
- [x] Gate 6 characterization: PASS — MVM characterization hash unchanged (CI green; the fix adds a `SelectedSubscriptionServer` assignment in the Smart Connect path only, which the characterization test does not exercise)

**Local build/test**: NOT run. The mandatory git hook attempted SDK resolution and found SDK 10.0.301 absent; this is not a pass.
**Surprises encountered**: none
**Follow-ups spawned**: none
**Rollback**: `git revert 70cb3a8a` / branch delete
