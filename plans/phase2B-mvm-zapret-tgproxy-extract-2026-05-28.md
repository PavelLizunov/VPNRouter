# Brief — Phase 2B: extract Zapret/TgProxy cluster from MainWindowViewModel

**Date**: 2026-05-28
**Roadmap**: `plans/v3.0-refactor-roadmap.md` §Phase 2B (the one genuinely-
incomplete refactor item per the 2026-05-28 audit — MVM is still a 7251-LOC
god-file despite 10 existing partials). Ships as v2.38 internal hygiene
(NOT a 3.0 — refactor declared effectively done; 3.0 reserved for Android GA).
**Branch**: `main` (partial-file move, no behaviour change, low risk).

## Why

`MainWindowViewModel.cs` is 7251 LOC — it grew back during the v2.37.0 Zapret
"magic" cycle (the big `ZapretOneClickAsync` orchestration + strategy
display/probe wiring all landed in the main file). The Zapret + TgProxy
surface is ~892 token occurrences and is a self-contained feature concern
(DPI bypass + MTProto proxy) with little coupling to VPN-core connection
state. Pulling it into its own partial is the single biggest, cleanest LOC
reduction available and makes the file navigable again. Zero user-facing
change — pure code organisation.

## What

Create `VPNRouter.App/ViewModels/MainWindowViewModel.ZapretTgProxy.cs` and move
the Zapret + TgProxy members out of `MainWindowViewModel.cs` into it:
- `[ObservableProperty]` backing fields: `_zapret*`, `_tgProxy*`, strategy
  display/probe state (`_zapretStrategies`, `_zapretProbeCts`,
  `LastProbeLogPath`, etc.)
- `[RelayCommand]` methods: `ZapretOneClickAsync`, `StartZapretWithSelected…`,
  `CancelZapretProbe`, `ToggleTgProxyAsync`, `DisableBadComboLockdown`, etc.
- Helpers: `LoadZapretStrategies`, `RefreshZapretStrategiesDisplay`,
  `TryRestoreLastProbeLog`, `NotifyZapretSummaryChanged`, badge builders.
- Keep in main file: only cross-concern glue that genuinely needs the
  constructor/connection state (if any) — minimise.

Move COMPLETE member blocks only (no signature/visibility changes). The class
stays `partial`; the MVVM Toolkit source generator emits the generated
properties/commands into the same partial that declares the backing field —
no surface change.

## How

1. Identify each Zapret/TgProxy member's exact line range in MVM.cs (Grep).
2. Cut contiguous blocks → paste into new partial (same namespace, same
   `public sealed partial class MainWindowViewModel`).
3. New file gets `#nullable enable` + the `using`s the moved code needs.
4. Build → fix any `using` gaps.
5. Run the FULL suite incl. `MainWindowViewModelCharacterizationTests` — the
   public-surface SHA-256 MUST be unchanged (proves zero surface drift).
6. If hash changes → STOP, a member was accidentally added/removed/retyped.

## Verification gate

- [ ] Gate 1 build: `dotnet build VPNRouter.App -c Release` → 0 errors.
- [ ] Gate 2 tests: full suite green, esp. `MainWindowViewModelCharacterizationTests`
      (public-surface hash UNCHANGED — the whole safety net for this split).
- [ ] Gate 3 docs: this brief Outcome filled; App/CLAUDE.md partial list updated
      (+ZapretTgProxy.cs); MVM LOC note refreshed.
- [ ] Gate 4 self-review: pure move — `simplify` N/A (no logic change). Diff is
      large LOC but mechanical; spot-check no method body altered.
- [ ] Gate 5 MCP verify: launch app → Zapret page + TgProxy page render +
      one-tap probe button responds (the moved commands still bind). Screenshot.
- [ ] Gate 6 characterization diff: = Gate 2 hash check (this IS a god-file split).

## Risk

LOW — partial-file move, no behaviour change, guarded by the public-surface
characterization hash + a build + MCP smoke. Worst case: a `using` gap (compile
error, caught by Gate 1) or an accidentally-dropped member (caught by Gate 2
hash). Rollback: `git revert`.

## Sequence (this is increment 1 of the MVM split)

1. **ZapretTgProxy** (this brief) — biggest cluster (~892 refs).
2. Connection (ToggleConnectionAsync / Reconnect / OnEngineStatus) — next.
3. CustomRules — after.
4. Recovery (banner + LaunchFailureCounter) — last.
Each increment is its own commit + characterization-hash check.
