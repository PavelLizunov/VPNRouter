# VPNRouter — Roadmap v2.18.4 – v2.19.x "UI unification + mode-apply fix"

**Baseline**: v2.18.3 prerelease (Simple mode compact design shipped, 3
rounds of user feedback folded in).

**Goal**: Close the last batch of user-reported gaps between Simple mode
and Advanced mode — header visual language, chip styling, and the
routing-mode apply flow that currently requires manual Disconnect +
Reconnect when the Windows Service owns the tunnel.

**User-reported issues (as of 2026-04-20, post-v2.18.3)**:
1. Header height + layout differs between Simple and Advanced. User
   prefers the Simple compact mini-header.
2. Status chips (VPN / Zapret / TG) look different in the two modes.
   User prefers the Simple pill-chip styling.
3. User likes the Simple mode's command logic (⋯ menu for
   theme/language/logs/leaks/updates/advanced).
4. **Autostart + mode mismatch**: if Autostart=true with
   RoutingMode=full baked into config.yaml at boot, and the user
   switches the routing mode knob in the UI (either mode's toggle),
   the running sing-box keeps routing "full" because nothing picks up
   the new config.
5. **Split ↔ Full toggle requires manual Disconnect + Connect**: in
   Simple mode the user changes the Split/Full radio while VPN is
   running; the UI saves YAML but the actual tunnel still routes by
   the old rules. User has to click Disconnect then Connect to get
   the new mode to apply.

**Not in scope**:
- Reworking Advanced tab contents (Servers / Subscribe / etc.).
- New features.
- macOS updater (fixed in v2.18.1, leave alone).

---

## Priority order

### Block 1 — Mode-apply functional bug (ship first, smallest surface)
1. **v2.18.4** — When `IsServiceManagedVpn`, `ApplyPendingChangesAsync`
   should auto-restart the Windows Service instead of showing
   "Stop and Start VPN to apply" text. Service restart makes it
   re-read config.yaml with the new RoutingMode.

### Block 2 — Header / chip unification (bigger visual change)
2. **v2.19.0** — Replace the Advanced header with the Simple-mode
   compact mini-header layout (40 px logo + brand + 3 pill-chips +
   ⋯ menu). Drop the 44 px logo block, the separate subtitle row
   ("NiniTux · vX · sing-box"), the scattered Logs / Check leaks /
   Theme / Lang / Advanced / Check-for-updates button row, and the
   three big status badges. All of that moves into the ⋯ menu or
   the pill-chips. Tab strip stays untouched.

### Block 3 — Fallback / polish (as needed)
3. **v2.19.1** — Any user-reported regressions from v2.19.0 header
   rework. Placeholder until feedback lands.

---

# v2.18.4 — Auto-restart service on mode change

**Symptom**: user changes Split ↔ Full (or any `RoutingMode`-affecting
setting) while connected via the Windows Service. UI saves config.yaml
and displays "Settings saved. Stop and Start VPN to apply — the service
re-reads config.yaml on start." — but the tunnel keeps the old routing
rules until the user manually clicks Disconnect then Connect.

Separately: if Autostart is on, the service boots with whatever
RoutingMode is persisted, and toggling the UI knob afterwards never
takes effect because the service process keeps running with the old
config.

## Root cause

`ApplyPendingChangesAsync` in `MainWindowViewModel.cs:1023`:

```csharp
if (IsServiceManagedVpn)
{
    HasPendingAppChanges = false;
    StatusText = "Settings saved. Stop and Start VPN to apply...";
    return;
}
```

We deliberately *punt* on service-managed VPN because the local
`_engine` has no sing-box process to hot-reload. But
`ServiceVm.RestartServiceCommand` already exists
(`ServiceViewModel.cs:122`) and does exactly the right thing:
`WindowsServiceHelper.Stop()` → `WindowsServiceHelper.Start()`. The
service on boot re-reads config.yaml via `SettingsLoader.Load` and
spawns sing-box with the updated RoutingMode.

## Fix

In `ApplyPendingChangesAsync`, replace the service-managed branch
with an actual service restart:

```csharp
if (IsServiceManagedVpn)
{
    StatusText = IsRussian
        ? "Перезапускаю службу с новыми настройками..."
        : "Restarting service with new settings...";
    await ServiceVm.RestartServiceCommand.ExecuteAsync(null);
    HasPendingAppChanges = false;
    // SyncConnectedWithVpnRuntime in the 2-second poll tick will
    // catch the new service state and refresh the status line.
    return;
}
```

Leave the fallback message (old text) for the case where
`ServiceVm.IsAvailable = false` (e.g. service not installed at all —
shouldn't happen if `IsServiceManagedVpn` is true, but belt-and-braces).

### Files
- `VPNRouter.App/ViewModels/MainWindowViewModel.cs` —
  `ApplyPendingChangesAsync`, replace the punt-branch with
  `ServiceVm.RestartServiceCommand.ExecuteAsync`.

### Testing
- Autostart on, VPN running via service, RoutingMode=full in config.
- Open app → toggle Simple "Selected apps" radio (IsSplitTunnel=true).
- Expected: brief "Restarting service..." status, then connection
  resumes with "connected via service [split]" and actual routing
  matches Split (Steam direct, Discord through proxy).

### Acceptance
- [ ] Split ↔ Full toggle while service-managed-connected no longer
  requires manual Disconnect + Connect.
- [ ] No regression on non-service (local-engine) connect flow.
- [ ] Autostart + subsequent UI mode toggle → mode actually applies.

## Gotcha
`WindowsServiceHelper.Stop()` may need several seconds to settle
depending on how long sing-box takes to exit cleanly. The existing
`RestartService` command already awaits both operations, so we
naturally inherit that timing. UI shows `IsApplying=true` during
the whole thing, so the connect button is disabled and the status
line carries a clear message — no UX confusion.

---

# v2.19.0 — Unified compact header

**Current state**:
- **Simple mode header** (shipped in v2.18.0, polished v2.18.3):
  40 px logo + brand + 3 pill-chips (VPN / Zapret / TG, clickable) +
  ⋯ menu. Single row. Total height ≈ 56 px.
- **Advanced mode header** (unchanged since Arctic v2.16):
  44 px logo + brand + "NiniTux · vX · sing-box … " subtitle +
  3 big pill-buttons (VPN / Zapret / TG — different visual weight
  than Simple chips) + separate row with Logs, Check leaks, Theme,
  Lang, Advanced(toggle), Check-for-updates. Two rows. Total height
  ≈ 90-100 px.

User reports the height mismatch is jarring when flipping between
modes, and prefers the Simple compact layout across the board.

## Goal

One header, used in both modes. Based on the current
`SimplePage.axaml` mini-header, but rendered at the MainWindow
level so the tab strip in Advanced can live immediately underneath
without a second logo row.

### Proposed layout

```
┌────────────────────────────────────────────────────────────────┐
│ [40px logo]  Virtual Penguin Network          [VPN][Zapret][TG] [⋯] │
│              <brand subtitle line>                               │
└────────────────────────────────────────────────────────────────┘
```

- Logo: 40 × 40, arctic-subtle rounded container (matches v2.18.3).
- Brand: "Virtual Penguin Network" in fs-lg bold + muted
  subtitle `vX.Y.Z · sing-box 1.13.3` (preserves the "what version
  am I on" affordance that Advanced used to show prominently).
- Chips: same as Simple v2.18.3 — pill-shaped Button, clickable,
  state-coloured (VPN = SuccessBg/WarningBg/SurfaceSunken depending
  on connect state; Zapret / TG = ZapretBadgeBrush / TgProxyBadgeBrush
  bindings we already have).
- ⋯ menu: theme, language, logs, leaks, check-for-updates, switch
  Simple ↔ Advanced. Same MenuFlyout markup as SimplePage, lifted
  into MainWindow.

### What moves, what stays

- Theme / Lang / Advanced / Check-for-updates row — **removed** from
  header, moves into ⋯ menu (both modes).
- Logs / Check leaks — moves into ⋯ menu (both modes).
- Big VPN/Zapret/TG badge buttons — replaced by compact pill-chips
  (both modes).
- Update notification banner — stays exactly where it is (Row=1 of
  the root Grid), unchanged.
- Tab strip in Advanced (ListBox with Servers/Subscribe/...) — stays
  exactly where it is, sits directly under the unified header.
- SimplePage's own mini-header — **removed** from SimplePage.axaml.
  SimplePage content becomes status card + config row + form +
  CTA + Advanced card only; the header lives one level up.

### Files
- `VPNRouter.App/Views/MainWindow.axaml` — replace the current
  `<Border Grid.Row="0">` block with the new compact header Grid.
- `VPNRouter.App/Views/Pages/SimplePage.axaml` — drop the local
  mini-header (lines 34-136 of the current file).
- `VPNRouter.App/ViewModels/MainWindowViewModel.cs` — no changes
  expected; all the bindings already exist.
- Possibly `Strings.cs` — some `SmpMenu*` strings now belong to
  both modes; rename to `HeaderMenu*` (cosmetic, optional).

### Acceptance
- [ ] Header visually identical in Simple and Advanced modes.
- [ ] Flipping Simple ↔ Advanced no longer shifts content vertically.
- [ ] All affordances present in old Advanced header are reachable
  via ⋯ menu or pill-chips.
- [ ] Pill-chips navigate correctly from both modes (Zapret → Tools
  tab + Zapret sub-section; TG → Tools + TgProxy; VPN → no-op in
  Simple, switch to Manual/Subscribe tab in Advanced — matches the
  existing `NavigateToVpnCommand` behaviour).
- [ ] Tab strip in Advanced sits directly under the unified header
  with no stray padding.
- [ ] Update banner still appears when an update is available.
- [ ] MinWidth=360 still fits without wrapping.

### Risk
Advanced power users may miss the prominent subtitle line
("by NiniTux · vX · sing-box N.N.N"). Mitigation: keep the version
visible as the brand subtitle (below "Virtual Penguin Network").
The sing-box version + NiniTux credit move to the ⋯ menu's
"About" item (if we add one) or just live in release notes.

---

## Status tracker

### Known issues (as of 2026-04-20, post-v2.18.3)
- [ ] Issue #1 — header height differs between modes
- [ ] Issue #2 — chip style differs between modes
- [ ] Issue #3 — user prefers Simple command logic (⋯ menu)
- [ ] Issue #4 — Autostart + UI mode change = stale routing
- [ ] Issue #5 — Split ↔ Full toggle while connected requires
  manual Disconnect + Connect

Issues #4 and #5 are the same root cause, both fixed by v2.18.4.
Issues #1-#3 solved together by v2.19.0 header unification.

### Release tracker
- [x] v2.18.4 — auto-restart service on mode change (fixes #4 + #5)
- [x] v2.19.0 — unified compact header (fixes #1 + #2 + #3)
- [x] v2.19.1 — post-v2.19.0 feedback: logo 40→56, removed duplicate
  brand title + version subtitle from in-window header (OS title bar
  already shows the name; version moved into ⋯ menu as a disabled
  info item at the bottom), added a visible "◂ Simple" pill-button
  in Advanced mode as an obvious return-home affordance.

---

## References
- `plans/vpnrouter-v2.17-v2.18-bugfixes.md` — v2.17.9 → v2.18.2
  roadmap (all shipped)
- `plans/vpnrouter-v2.17-simple-mode.md` — v2.17 roadmap
- `.claude/workflow.md` — git remotes, release policy
- `VPNRouter Design System 2/handoff/SimpleMode.html` — compact header
  reference (the layout we're propagating to Advanced)
