# Tools-page iteration 2 — TgProxy + Zapret simplification audit

**Status**: planned (post-compaction).
**Branch**: `claude/suspicious-kepler-fa08e0` (current worktree).
**Latest shipped**: `v2.31.6-r4` (TgProxy bug fixes from iteration #1 audit).

User feedback (2026-05-03 night) flagged two pages that need a
proper computer-use audit — not static-only. The reading-the-axaml +
running-headless-tests path I tried in iter #1 missed real interaction
issues that only surface when actually clicking through the live app.

---

## ⚠️ Methodology (CRITICAL — read before any code change)

The point of this iteration is **interactive testing**. Static rendered
screenshots from `PageScreenshotTests` are NOT a substitute. Earlier
audit attempts ("Variant A r1/r2") missed the design handoff entirely
because I trusted my own page renders instead of:

1. running the production app,
2. clicking each element with a mouse,
3. screenshotting the result of EACH click.

This iteration MUST use the **`mcp__vpnrouter-test__*`** MCP tool family.
Never substitute headless tests for live audit:

| Tool | When to use |
|---|---|
| `mcp__vpnrouter-test__list_windows` | Find the VPNRouter window's `hWnd` and bounds |
| `mcp__vpnrouter-test__focus_window` | Bring VPNRouter to foreground before each click |
| `mcp__vpnrouter-test__mouse_click` | Single mouse click at absolute screen coords |
| `mcp__vpnrouter-test__screenshot` | Capture VPNRouter window after the click |
| `mcp__vpnrouter-test__wait` | Pause 300–3000 ms between click and screenshot so async ops settle |
| `mcp__vpnrouter-test__type_text` | Enter text into the focused control (Port input, etc.) |
| `mcp__vpnrouter-test__press_key` | Send keystrokes (Tab, Esc, etc.) |

**Workflow per element** (repeat for each clickable item):

```
1. focus_window(title='VPNRouter')
2. screenshot()                    ← baseline
3. mouse_click(x=…, y=…)           ← exact coords
4. wait(ms=500–3000)               ← let UI react
5. screenshot()                    ← compare with baseline
6. document the diff in commit message / plan log
```

If a click doesn't appear to register: re-focus the window, verify
window bounds with `list_windows`, retry. Earlier iter logged
several "click missed because window moved" cases — re-focusing
catches them.

The iter #1 audit (commit aaf2704) is the reference example —
look at the screenshots produced and the bug log it generated.

**Design handoff cross-reference is mandatory before XAML edits**:
`C:/tmp/vpnrouter-design/vpnrouter-design-system/project/AdvancedMode.html`
contains cells 6 (TgProxy) and 7 (Zapret). Grep that file for the
relevant CSS and walk it selector-by-selector before changing layout.
v2.31.6-r1/r2 violated this and had to be redone in r3; don't repeat.

---

## Iteration TG-2: TgProxy page polish

### What's wrong now (per user feedback)

> «важно разобраться с кнопкой открыть телеграм и запустить прокси —
> сейчас они очень далеко. И вообще наверное корректнее чтоб
> одновременно с запуском прокси открывалось телеграм, а уже если не
> получилось, то делать это через кнопку»

Translation: "Open in Telegram" (3-button row, mid-page) and
"Start Telegram proxy" (footer, bottom of page) are visually far
apart but conceptually a single user-flow on first run. The user
shouldn't be the one doing two clicks 200 px apart in sequence.

### Target UX

1. **Footer toggle becomes the unified action**:
   - When Stopped → label `Start & open Telegram` (or similar) →
     fires `SetupTgProxyAsync` (already wraps download → start →
     open-in-Telegram).
   - When Running → label stays `Stop Telegram proxy` — secondary.
2. **Body "Open in Telegram" demotes to "Re-pair in Telegram"** —
   secondary button next to "Open folder" + "GitHub". Used only if
   the user reset their Telegram client / switched device / wants a
   fresh deep-link with a regenerated secret.
3. **Auto-open is best-effort**: if `OpenInTelegram` fails (no
   Telegram desktop installed — the BUG #1 fix path now triggers a
   toast), the proxy still finishes starting. The user gets the
   toast pointing at desktop.telegram.org and can manually re-pair
   later via the body button.

### Concrete TG-2 task list

1. **Re-test v2.31.6-r4 fixes** via live computer-use:
   - [ ] Click `Open in Telegram` while Telegram desktop is missing.
     Expect: AccentBgSubtle toast banner above the status banner
     reading "Telegram не установлен — скачай с desktop.telegram.org".
     NOT the Windows OS dialog.
   - [ ] Click `Copy`. Expect: toast "Скопировано!" appears above
     banner. Status banner stays "Stopped" (or whatever real state).
     Wait 3 s → toast disappears.
   - [ ] Start the proxy via footer. Click `New` regenerate.
     Expect: toast "Новый secret — перезапусти proxy и Telegram client".
   - [ ] Click `Update TgProxy` while already on latest version.
     Document what shows up (this is BUG #5 territory — likely shows
     "Installed v1.6.5" without "already up to date" cue).
2. **Refactor for unified Start**:
   - [ ] Footer toggle: `Background`/`Foreground` already conditional
     on `TgProxyEnabled` (r2 fix). Change `Command` from
     `ToggleTgProxyCommand` to `SetupTgProxyCommand` ONLY when stopped
     (so it does start + open-in-Telegram). When running, keep
     `ToggleTgProxyCommand` (which stops). Use a tiny VM helper
     `TgProxyMainActionCommand` that branches by state.
   - [ ] Footer label: when Stopped → `L_TgProxyStartAndOpen` (new
     RU/EN string). When Running → existing `LblTgProxyToggle =
     TgProxyStop`.
   - [ ] Body 3-button row: rename primary "Open in Telegram" to
     "Re-pair in Telegram" (`L_TgProxyReopenInTelegram` exists, was
     used in r1/r2 — re-wire). Demote from primary blue to secondary
     so it's clearly the fallback.
3. **Reduce visual gap**:
   - [ ] Consider folding the 3-button row into a more compact form:
     `[Re-pair]   ·   Open folder   ·   GitHub`. Or move re-pair
     to the Advanced settings section since it's a fallback action.
   - [ ] Verify: footer is the visual primary on first-run. Body
     elements (description, Port, Secret, banner, note) all feel
     like reference / control surfaces, not action triggers.
4. **Computer-use verification of new layout** (per Methodology):
   - [ ] Click footer `Start & open Telegram` on a fresh-install state.
     Expect: brief progress (download + start), then deep-link fires;
     if Telegram present → opens Telegram with auto-add prompt.
     If absent → toast "Telegram not installed".
   - [ ] After start succeeds, footer label flips to
     `Stop Telegram proxy`, style flips to white/secondary.
   - [ ] Click body `Re-pair in Telegram` with proxy running. Expect:
     no state change (proxy stays running), Telegram client opens
     with deep-link if installed.
5. **Update screenshot baseline** + add tests pinning new behaviour:
   - [ ] `TelegramPage_StartAndOpenLabel` — pins footer label when
     Stopped reads "Start & open Telegram" (Russian + English variants
     via theory).
   - [ ] `TelegramPage_RepairInTelegramSecondary` — pins the body
     button is secondary-styled (not primary blue).
   - [ ] Update `screenshots/baseline/page-telegram.png` after
     visual confirmation.

### TG-2 design alignment

Cell 6 of `AdvancedMode.html` showed primary = "Открыть в Telegram"
in body, secondary = "Stop" in footer. The user feedback above
suggests promoting the FOOTER to primary instead. This is a
deliberate departure from cell 6 — document it explicitly in the
release notes ("user feedback 2026-05-03: footer is the primary
action because Open-in-Telegram should be a side-effect of start,
not a separate manual click"). Keep the v2.25.6 "don't fight global
Start/Stop VPN" rationale by keeping footer secondary while Running.

---

## Iteration ZAPRET-2: Zapret page audit + simplification

### What we know

Computer-use audit in iter #1 only briefly touched Zapret:

- Master-detail layout (140 px sidebar + scrollable detail)
- 7 sections in the sidebar: Status / Strategy / Hosts / Filters /
  Updates / Diagnostics / Advanced
- Footer: full-width BLUE primary CTA "Start DPI Bypass"
- Status section had: title + description + status card +
  yellow warning banner ⚠ "Windows only. Can be used without
  VPN and alongside VPN."

Iter #1 didn't click into Strategy / Hosts / Filters / etc.
Plan: do that thoroughly and find simplification opportunities.

### Concrete ZAPRET-2 task list

1. **Live computer-use baseline of every section** (per Methodology):
   For each section in [Status, Strategy, Hosts, Filters, Updates,
   Diagnostics, Advanced]:
   - [ ] Click left-nav item → screenshot detail pane
   - [ ] Click each interactive control in the detail pane
     (dropdowns, checkboxes, inputs, buttons, expanders)
   - [ ] Document what each control does (read VM commands if
     unclear)
   - [ ] Note any "this looks confusing / broken / redundant" hits

2. **Cross-reference design handoff cell 7**:
   - [ ] Grep `AdvancedMode.html` for the Zapret section
     (search for "Zapret" + neighboring cells)
   - [ ] Walk each design selector and verify implementation matches
   - [ ] Log every deviation as a candidate fix

3. **Identify simplifications** (output of click-test):
   Likely candidates based on iter #1 spot-checks:
   - 7 sections may be too many. Candidates to consolidate:
     - Diagnostics + Updates → "Maintenance" tab?
     - Hosts + Filters → "Routing" or "Apps" tab?
     - Advanced → behind disclosure expander like TgProxy iter #1
       (NOT like the design handoff disagreed-with-r1 expander, but
       a small toggle for power-user knobs that nobody touches)
   - Strategy default may not need to be the second-most-prominent
     section; for most users "just works" is the goal.
   - Warning banner "Windows only" is informational — maybe move
     to Settings / About since Linux/macOS users won't see this
     page anyway (the Tools tab is hidden on those platforms?).
   - Stats / metrics: are they useful or noise? Computer-use to find out.
4. **Implement consolidated layout**:
   - [ ] Reduce sidebar to 4-5 items max (or remove sidebar if
     content fits in tabs)
   - [ ] Move rare power-user controls behind disclosure
   - [ ] Keep footer primary CTA "Start DPI Bypass" — that part
     was already right per iter #1
   - [ ] Ensure parity-or-better with cell 7 design where they
     overlap

5. **Computer-use verification of new layout** — same workflow as
   TG-2: click, screenshot, document.

6. **Update screenshot baselines + tests**:
   - [ ] Add `ZapretPage_*` screenshot tests for each remaining
     section (after consolidation).
   - [ ] Pin baseline PNGs.

### ZAPRET-2 design alignment

Same rule as TG-2: grep `AdvancedMode.html` cell 7 first. If user
feedback during the audit pushes a deviation, document the rationale
explicitly in release notes.

---

## Cross-cutting bugs to also address (deferred from iter #1)

- **BUG #2** (TG): "Telegram automatically uses this proxy after setup"
  hint shown when Stopped — was r2-only (Variant A status card),
  r3+ already dropped it. Verify still gone in r4.
- **BUG #5** (TG): `Update TgProxy` shows "Installed v1.6.5" even
  when already on latest. Refactor `UpdateTgProxyAsync` to detect
  no-change case and surface "Already up to date" toast. Low
  priority but caught by audit.
- **BUG #6** (cross-page): footer style inconsistency between
  Telegram and Zapret. Ratified as intentional in iter #1 (TG has
  primary in body, Zapret has primary in footer, so footers differ
  by design). Document this in `VPNRouter.App/CLAUDE.md` under "UI
  design rules" so future iterations don't try to "fix" it.

---

## Process commitments to enforce next session

1. **Always run live app + click via `mcp__vpnrouter-test__*` first**.
   Static analysis only AFTER computer-use audit identifies what to
   look for. Never the other way around.
2. **Always grep design handoff** before XAML changes:
   ```
   grep -i -n "<keyword>" "C:/tmp/vpnrouter-design/vpnrouter-design-system/project/AdvancedMode.html"
   ```
3. **Always force-rebuild VPNRouter.Tests** between App-side edits and
   any visual baseline check. Stale `Tests/bin/.../VPNRouter.App.dll`
   silently fooled iter #1 into shipping r2 with the WRONG layout.
   Workflow:
   ```
   cmd.exe /c "taskkill /F /IM testhost.exe 2>nul"
   dotnet build VPNRouter.Tests/VPNRouter.Tests.csproj -c Release
   dotnet test ... --no-build --filter "..."
   ```
4. **Always sync main worktree before `build.ps1`**:
   ```
   git -C "C:/Project/VPNRouter" fetch github main
   git -C "C:/Project/VPNRouter" merge --ff-only github/main
   ```
   build.ps1 reads main worktree's HEAD; out-of-sync = wrong commit
   gets tagged.

---

## Acceptance criteria

A session counts as "iteration complete" only when:

- [ ] Every clickable element on TgProxy + Zapret pages has been
  exercised via `mcp__vpnrouter-test__mouse_click` with a screenshot
  before/after. Bug log is concrete (e.g. "click Copy at (1627, 525)
  → status field showed 'Copied!' for 30 s, never reverted").
- [ ] Each fix has been re-verified by another computer-use click
  cycle on the rebuilt + re-installed app. Not just headless test
  renders.
- [ ] Design handoff cells 6 + 7 walked selector-by-selector,
  deviations explicitly documented in release notes with rationale.
- [ ] Updated screenshot baselines committed (with fresh
  Tests/bin/App.dll, not stale).
- [ ] User flow narrated end-to-end for both pages: "first-time user
  clicks X → gets Y → clicks Z → done". 1-line per step.

---

## Files / tools quick-reference

- Worktree: `C:/Project/VPNRouter/.claude/worktrees/suspicious-kepler-fa08e0/`
- TgProxy axaml: `VPNRouter.App/Views/Pages/TelegramPage.axaml`
- Zapret axaml: `VPNRouter.App/Views/Pages/DpiBypassPage.axaml`
- TgProxy/Zapret VMs: in `MainWindowViewModel.cs` (search for `TgProxy*` / `Zapret*`)
- Strings: `VPNRouter.App/Localization/Strings.cs`
- Screenshot tests: `VPNRouter.Tests/PageScreenshotTests.cs`
- Test baseline: `VPNRouter.Tests/screenshots/baseline/`
- Design handoff: `C:/tmp/vpnrouter-design/vpnrouter-design-system/project/AdvancedMode.html`
  (cells 6 = TgProxy, 7 = Zapret)
- Computer-use MCP: `mcp__vpnrouter-test__*` (clicks + screenshots)
- Production app: PID can be found via `tasklist | grep VPNRouter`.
  Currently running elevated → can't `taskkill /F`. Auto-update
  pulls only stable; prereleases need user opt-in OR manual install.
- Build: `powershell -ExecutionPolicy Bypass -File "C:/Project/VPNRouter/build.ps1" -Version "X.Y.Z-rN" -Upload`
- Ship + finalise: `cut-stable` skill (only after user "cut" command),
  `ship-rolling-candidate` skill for -rN.

---

## What got shipped in iteration #1 (so iter #2 doesn't redo it)

- v2.31.6-r1: Variant A two-state cascade (DEVIATED from design — wrong)
- v2.31.6-r2: r1 + footer styling polish (still wrong — design not consulted)
- v2.31.6-r3: full redo per design handoff cell 6
- v2.31.6-r4: 3 logic bugs from computer-use audit (tg:// guard,
  toast surface, regenerate-while-running warning)

Latest stable: `v2.31.5`. v2.31.6-rN is the in-flight cycle.

---

## After this iteration

Cut `v2.31.6` stable when the user is satisfied with both
TgProxy + Zapret pages on real-device testing.
