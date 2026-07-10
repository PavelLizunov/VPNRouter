# TZ (Codex): Applications — include/exclude must show independent selections

**Status:** backlog (do NOT implement in the r5 session; hand to Codex).
**Reported:** 2026-07-06 by user, while checking how to enable true-split.

## Symptom

On the **Приложения / Applications** page the Include/Exclude segmented toggle
("Только выбранные → VPN" / "Кроме выбранных → VPN") appears to drive **one and
the same** checked app list — switching the mode does not visibly give a
separate selection. This matters for **true-split**: true-split engages only in
**exclude** mode with ≥1 excluded app, and a user coming from include mode (which
ships ~57 default routed apps) would see those same apps carried over as the
"excluded" set instead of a clean, independently-curated exclude list.

## What already exists (so scope is smaller than "add a second list")

The data model + VM bridge already model TWO independent lists:

- `VPNRouter.Core/Models/AppConfig.cs` — `RoutingAppsInclude` and
  `RoutingAppsExclude` are separate `List<string>`; the engine reads the
  mode-appropriate one (`SplitTunnelPolicy` / `VpnEngine.TryEngageSplitDriverAsync`
  uses `RoutingAppsExclude` for the driver).
- `VPNRouter.App/ViewModels/AppItemViewModel.cs` — `IsChecked` is a **mode-aware
  bridge**: `ReadMode(processName)` reads from the currently-active list,
  `WriteMode(processName, value)` writes to it; `RaiseIsCheckedChanged()` is meant
  to be called on a mode flip so each row re-reads from the now-active list.
  Doc-comment (AM-3, v2.32.2) explicitly says "Both lists hold their own state
  independently — toggling between modes never copies or wipes the inactive list."

So the intended behaviour is already "separate lists, checkboxes reflect the
active mode, refresh on flip." The bug is that the refresh (or the toggle→refresh
wiring) does not fire, so the UI shows stale checks and reads as one shared list.

## What Codex should do

1. **Reproduce**: Advanced → Приложения. Note the checked apps in
   "Только выбранные → VPN" (include). Flip to "Кроме выбранных → VPN" (exclude).
   Expected: checks change to the (initially empty) exclude list. Bug: same checks
   remain.
2. **Find the gap** in the mode-toggle path:
   - `VPNRouter.App/ViewModels/MainWindowViewModel.Profiles.cs` — the
     `RefreshAppCheckboxes` method + wherever `IsRoutingAppsModeInclude/Exclude`
     (the toggle setter, likely in `MainWindowViewModel` / `.Settings.cs`) is
     handled. Confirm the setter actually calls `RefreshAppCheckboxes` (or
     re-raises `IsChecked` on every `AppItemViewModel` in every `AppGroupViewModel`)
     when the mode changes.
   - `VPNRouter.App/ViewModels/AppGroupViewModel.cs` — the per-app bridge wiring
     (`ReadMode`/`WriteMode` set during `LoadApps`); verify groups propagate the
     refresh to their child items.
   - `VPNRouter.App/Views/Pages/ApplicationsPage.axaml` — the segmented
     `ToggleButton`s bind `IsChecked="{Binding IsRoutingAppsModeInclude/Exclude,
     Mode=TwoWay}"`; make sure the mode change routes through the VM setter (not a
     pure view-side toggle that skips the refresh).
3. **Fix**: on mode flip, refresh every app row's `IsChecked` from the now-active
   list (call the existing `RefreshAppCheckboxes` / `RaiseIsCheckedChanged`), and
   confirm the category counts / "N selected" summaries recompute for the active
   list (see App CLAUDE.md Rule F1 — secondary views must refresh with the list).
4. **Guardrails**: the characterization hash pins `MainWindowViewModel`'s public
   surface — a fix that only re-wires existing calls should not add public members;
   if it must, update the pin per `VPNRouter.Tests/CLAUDE.md`.
5. **Tests**: add a VM test — set include list = [A,B], exclude list = [C]; assert
   that with mode=include the rows for A,B are checked and C unchecked, and after
   flipping to exclude only C is checked (and writes land in the correct list).

## Acceptance

- Flipping include↔exclude visibly swaps the checked selection; each list is
  curated independently and persists (`routing_apps_include` /
  `routing_apps_exclude` in config.yaml).
- Entering exclude mode from a fresh state shows an empty exclude selection (not
  the include defaults), so true-split's excluded set is what the user actually
  picks.
- Existing include-mode behaviour + the r5 protocol chips unaffected.

## Refs
- `plans/r10-stas-confirmed-and-apps-2mode.md` §3 (AM-3 acceptance — original
  two-mode design).
- `plans/goal-w1-mullvad-split-driver-2026-07-03.md` (true-split; exclude-mode
  engage gate).
- App CLAUDE.md "F. State sync across VM-list rebuilds".
