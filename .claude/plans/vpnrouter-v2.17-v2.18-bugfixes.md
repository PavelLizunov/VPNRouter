# VPNRouter — Roadmap v2.17.9 – v2.18.x "Simple mode stabilisation + audit"

**Baseline**: v2.17.8 prerelease (Simple mode MVP live, one XAML crash
already fixed in .8).

**Goal**: clear the backlog of user-reported bugs on SimplePage, audit
for hidden ones introduced during v2.16/v2.17, and answer the open design
question about the header in Simple mode. Keep Advanced untouched —
nothing we ship here is allowed to regress the Advanced flow.

**User-reported issues (as of 2026-04-20)**:
1. Opening the "Change config or mode" expander makes the hero card
   flip to "VPN is off" even while the VPN is clearly still running.
2. Clicking the theme or language toggle teleports the window to the
   centre of the screen. Didn't happen before v2.17.
3. RAM footprint is ~200 MB. Previously reported as lower. Unknown
   whether this is new or has been silently accumulating since Free
   Configs aggregator landed.
4. Open design question: does Simple mode need the full header at all?
   Logs / Check leaks / 3 status badges / Dark / RU / Advanced / Check
   for updates is a LOT for a non-technical landing page.
5. "There might be more" — user explicitly asked for a safety-net audit.

**Not in scope**:
- Anything that touches Advanced layout (Servers / Subscribe / Tools /
  FreeConfigs / Network tabs).
- New features.
- macOS-specific work.

---

## Priority order

### Block 1 — Functional bugs on SimplePage
1. **v2.17.9** — fix the Expander inverts IsConnected via `{Binding !X}`
2. **v2.17.10** — fix the window teleport on theme/language toggle

### Block 2 — Header rework for Simple
3. **v2.18.0** — minimal-header variant when IsSimpleMode

### Block 3 — Performance
4. **v2.18.1** — memory investigation + whatever low-hanging fixes
   surface

### Block 4 — Safety net
5. **v2.18.2** — hidden-bugs audit (grep-based + manual pass over all
   pages) + regression tests for the patterns that bit us

---

# v2.17.9 — Fix Expander collapsing connection

**Symptom**: user has VPN running (hero card: "VPN is running", green
dot, Stop button). Clicks to open "Change config or mode ▾" expander.
Hero card flips to "VPN is off", grey dot, Start button. VPN itself is
still up — only the UI thinks it's down.

**Hypothesis**: in `SimplePage.axaml` v2.17.7 I wrote

```xml
<Expander IsExpanded="{Binding !IsConnected}" ...>
```

Avalonia's `!` shortcut on a binding is not always strictly one-way. In
some versions it compiles to a two-way binding where Expander's
interactive toggle writes `IsExpanded` back into the source — the
inverter reverses the boolean, so the source `IsConnected` gets
flipped. Clicking the expander to open it writes `true → !IsConnected`
which sets `IsConnected = false`. Boom: the whole VM reacts as if the
connection dropped (status text resets, ConnectButtonText flips, etc.)
even though the engine is still running.

## Fix

### Option A (minimal) — explicit `Mode=OneWay`
```xml
<Expander IsExpanded="{Binding !IsConnected, Mode=OneWay}" ...>
```
Safe but fragile — user still can't manually collapse the form while
disconnected or expand it while connected without the binding snapping
back on the next IsConnected change.

### Option B (preferred) — dedicated observable
Introduce `[ObservableProperty] bool _smpFormExpanded = true;` in
`MainWindowViewModel.SimpleMode.cs`. Two-way bind the Expander to it.
On `IsConnected` change, update `SmpFormExpanded` via partial
`OnIsConnectedChanged` but only if the user hasn't manually toggled
(track with a "manual" flag).

Simplest compromise: just default it to `!IsConnected` at load time
and let user toggle freely afterwards. Don't auto-change on connection
state — that's actually nicer UX (user keeps the form open to switch
server without re-expanding).

### Files
- `VPNRouter.App/ViewModels/MainWindowViewModel.SimpleMode.cs` — add
  `_smpFormExpanded` observable, initial value `true` (starts expanded).
  Reset to true inside `LoadSettingsIntoUI` only when disconnected.
- `VPNRouter.App/Views/Pages/SimplePage.axaml` — change expander to
  `IsExpanded="{Binding SmpFormExpanded, Mode=TwoWay}"`.

### Testing
- Connect VPN → hero shows "VPN is running"
- Click expander open → form shows, hero STAYS "VPN is running"
- Click expander closed → form collapses, hero unchanged
- Disconnect → form auto-reopens (natural first-run UX)
- Reconnect → form stays in whatever state user left it

### Acceptance
- [ ] Hero card reflects ONLY real engine state, never expander state
- [ ] Expander works like a normal control (open/close without side
  effects)
- [ ] Form auto-opens on first disconnect if it was auto-closed by us
  earlier

## Gotcha
Check all other `{Binding !X}` bindings in the codebase — the pattern
might have the same trap elsewhere. Put this on the v2.18.2 audit list
as "grep `!` in binding expressions".

---

# v2.17.10 — Fix window teleport on toggle

**Symptom**: user clicks the 🌙/☀ theme button OR the RU/EN language
button, the window abruptly jumps to the centre of the screen. Did not
happen in v2.16.x.

**Hypothesis**: v2.15.6 added `ReloadMainWindowForLocalization()` which
rebuilds the entire MainWindow when the language toggles so
`{x:Static loc:Strings.*}` bindings re-parse. That rebuild preserves
`Position / Width / Height / WindowState` by copying them to the new
Window BEFORE `Show()` — but the new MainWindow has
`WindowStartupLocation="CenterScreen"` in its XAML, which when used by
Avalonia overrides the manually-set `Position` at the moment of Show().

For the theme toggle path, `ToggleTheme` → `ApplyTheme()` doesn't
rebuild the window. BUT: v2.16.5 uses `RequestedThemeVariant` swap,
which in some Avalonia versions triggers a soft re-render that in
combination with our `ReloadMainWindowForLocalization` pattern (only
language triggers rebuild) may not even be the culprit. User could be
conflating "both move the window" when only the language one does.

## Investigation step 1 — confirm which toggle teleports
Add a log line at the start of `ToggleTheme` and `ToggleLanguage` so
the next reproduction nails the culprit. 30-second change; ships first.

## Fix (likely)
In `ReloadMainWindowForLocalization()`:
```csharp
var newWindow = new Views.MainWindow
{
    DataContext = this,
    Position = pos,
    Width = width,
    Height = height,
    WindowState = state,
    WindowStartupLocation = WindowStartupLocation.Manual,  // ← key fix
};
```

`WindowStartupLocation.Manual` tells Avalonia "trust the Position
property, don't re-centre". The XAML default is `CenterScreen`.

## If theme DOES also teleport
Check what `RequestedThemeVariant` change triggers in our setup. In
v2.16.5 I don't think I rebuild the window on theme change — but verify
by adding the log above. If it does rebuild somewhere, same fix applies.

## Files
- `VPNRouter.App/ViewModels/MainWindowViewModel.cs` —
  `ReloadMainWindowForLocalization` set `WindowStartupLocation.Manual`.
- Bonus: add one log line each in `ToggleTheme` and `ToggleLanguage`
  to make future reports instantly diagnosable.

## Acceptance
- [ ] Move window to any corner, toggle language → stays put
- [ ] Same for theme toggle
- [ ] Logs show which toggle fires when

---

# v2.18.0 — Minimal header for Simple mode

**Current header (both modes)**:
```
[logo] Virtual Penguin Network   Logs  Check leaks  Dark  RU  Advanced ▸
       by NiniTux · v2.17 · sing-box ...                 Check for updates
       🟢 VPN  ⚪ Zapret  ⚪ TgProxy
```

For a user who picked Simple ("I just want VPN"), 7 controls + 3 badges
+ subtitle is too busy. Questions to answer:

### Decisions (open for user approval)
1. **Brand block**: keep logo + title + version? OR drop to just a tiny
   logo icon in a corner?
2. **Logs / Check leaks / Check for updates**: which of these are safe
   to hide in Simple?
   - Logs: power-user tool, hide.
   - Check leaks: opens ipleak.net — maybe keep as a nice
     "verify it works" affordance? Or move into the hero card when
     connected?
   - Check for updates: keep — non-technical users want updates.
3. **Zapret / TgProxy badges**: hide. (User can reach them via
   Advanced + badge nav we fixed in v2.17.7.)
4. **VPN badge**: hide (SimplePage hero card is the VPN status).
5. **Theme / language / Advanced**: keep — these are the 3 that matter.

### Proposed Simple header
```
[tiny logo]                          ☀  EN  [ Advanced ]
```

- Logo: 24×24, left, no text.
- Right: theme (24×24 emoji button), language (flag emoji), Advanced
  (filled arctic-solid pill, clear CTA).
- Everything else from v2.17.x header → hidden when IsSimpleMode.

### Implementation
`MainWindow.axaml` header Grid becomes two Stacked layers:

```xml
<Grid>
  <!-- Advanced header (unchanged, existing content) -->
  <Grid IsVisible="{Binding !IsSimpleMode}"> ... </Grid>
  <!-- Simple header (new, minimal) -->
  <Grid IsVisible="{Binding IsSimpleMode}">
      <StackPanel Orientation="Horizontal" ...>
        <Image Source="{Binding LogoSource}" Width="24" Height="24"/>
        <Button Content="{Binding ThemeToggleText}" ... Theme="Ghost"/>
        <Button Content="{Binding IsRussian, Converter=...}" ... Theme="Ghost"/>
        <Button Content="{Binding UiModeToggleText}"
                Background="{DynamicResource AccentSolidBrush}"
                Foreground="{DynamicResource AccentOnSolidBrush}" ... />
      </StackPanel>
  </Grid>
</Grid>
```

### Acceptance
- [ ] Simple header fits on one line at MinWidth=360
- [ ] Three controls only: theme, language, Advanced
- [ ] No info loss — everything hidden is reachable via Advanced
- [ ] Advanced header unchanged (hard regression check)

### Risk
Some users currently use "Check leaks" quickly from the header. Keeping
it as a tiny icon on the hero card (SimplePage) rather than hiding
entirely may be a compromise. Decision pending.

---

# v2.18.1 — Memory footprint investigation

**Symptom**: user reports ~200 MB RAM (previously less). Unknown whether
this is recent regression or accumulated since Free Configs.

## Investigation plan

### Step 1 — baseline measurement
Run the app, take a memory snapshot:
```
dotnet-counters monitor --process-id <PID> System.Runtime
```
Record Working Set, GC Heap, LOH, Gen0/1/2 allocation rates.

### Step 2 — identify top allocators
Take a heap snapshot with `dotnet-gcdump`:
```
dotnet-gcdump collect -p <PID>
```
Open in PerfView / dotMemory. Sort by retained size. Expected culprits:
1. **`_allConfigs` in FreeConfigsPageViewModel** — 25 000 FreeConfigEntry
   objects with URI strings, GeoIP data, status. Each ~400-800 bytes →
   10-20 MB. Not huge but not small.
2. **Avalonia / Skia baseline** — ~80-100 MB expected and non-optional.
3. **ETW buffers** — process-monitor session buffers events. If
   buffer size is too high, could be 10-30 MB.
4. **RuntimeStatusDetector timer** — harmless, ~0.
5. **Inverted logo WriteableBitmap** (v2.16.5) — ~1-5 MB.
6. **Subscription servers list** — usually <5k entries.

### Step 3 — targeted fixes (if any)
Most likely fix candidates:
- Lazy-load `_allConfigs` only when user opens Free tab. Currently
  loaded at app startup via cache.
- Clear the inverted logo WriteableBitmap after copying to final Bitmap
  (if we allocate both).
- Reduce ETW session buffer size.
- Null out `FreeConfigsVm` caches when Advanced → Simple toggle.

### Acceptance
- [ ] Memory snapshot taken, top 20 retainers documented
- [ ] Working set drops by at least 40 MB in a 30-minute idle run
  (target: ~150 MB)
- [ ] No UX regression from any fix

### Note
200 MB is not alarming for an Avalonia + Skia + TUN + ETW desktop app.
If investigation shows ~80 MB is Avalonia baseline, ~50 MB Free Configs
cache, ~30 MB ETW, ~20 MB misc — that's expected and we just document
the breakdown. The goal is understanding, not artificial reduction.

---

# v2.18.2 — Hidden-bugs audit + safety net

**Goal**: find bugs that are in the tree RIGHT NOW but haven't been
reported yet, before more users hit them.

## Grep-based checks (scripted)

### Check 1 — `{Binding !X}` two-way trap
The v2.17.9 fix exposed that Avalonia's `!` binding can be writable.
Find every usage:
```
Select-String -Path VPNRouter.App/Views -Pattern '\{Binding !' -Recurse
```
For each hit, assess: is that control WRITABLE (CheckBox, RadioButton,
Expander)? If yes and currently no `Mode=OneWay` — potential bug.
Convert to explicit OneWay or a dedicated negation property.

### Check 2 — UserControl referencing MainWindow.Resources
v2.17.8 bug: SimplePage used `StaticResource StatusColorConverter` from
MainWindow.Resources, crashed at runtime parse. Every UserControl in
`Views/Pages/*.axaml` should declare its own local converter resources
OR only use app-level (Tokens.axaml) resources. Grep:
```
Select-String -Path VPNRouter.App/Views/Pages -Pattern '\{StaticResource \w+Converter\}'
```
For each hit, ensure the converter is declared in that UserControl's
own `<UserControl.Resources>`.

### Check 3 — Hardcoded hex colours that slipped through Arctic migration
```
Select-String -Path VPNRouter.App/Views -Pattern '#[0-9A-Fa-f]{6}' -Recurse
```
Known intentional exceptions: the purple `#7C3AED` family in DpiBypass
(documented in v2.16.3 commit) and the 3 Deep Verify buttons. Anything
else surfaced here is a regression.

### Check 4 — FontSize outside the scale {9,10,11,12,13,15,18,22,24,32}
```
Select-String -Path VPNRouter.App/Views -Pattern 'FontSize="(\d+)"' -Recurse \
  | ForEach-Object { $_.Matches.Groups[1].Value } \
  | Sort-Object -Unique
```
Anything NOT in the scale list is an off-grid value that slipped in.

### Check 5 — Padding / CornerRadius outside the grid
```
Select-String -Path VPNRouter.App/Views -Pattern 'Padding="\d+,\d+"' -Recurse
```
Same idea — anything using odd numbers (3, 5, 7, 9, 11, 13) is probably
accidental.

### Check 6 — `SmpInput` contains VLESS, user already on subscription
Simple-mode user pastes a single `vless://` URI on top of an existing
subscription config. Current code flips IsSubscribeMode=false,
IsVlessMode=true. Does the existing subscription entry stay in
settings? Could cause stale routing. Test: paste vless:// over
subscription, Start, Advanced → Subscribe tab → verify subscription
list is empty or unchanged as expected.

## Manual visual audit

Run the app, open each page one by one in both Simple and Advanced,
both Light and Dark, both RU and EN. Look for:
- Text overflow
- Misaligned control columns
- Colour contrast issues in dark mode (target AA 4.5:1 for body)
- Broken tooltips (hover every control, confirm non-trivial ones
  show a tip)
- Disabled/enabled state consistency (if we're connected, which
  controls should be disabled?)

## Regression tests
Add to `VPNRouter.Tests`:
- `SmpInputDetectorTests` — classify a handful of real vless:// URIs,
  real subscription URLs, garbage input, empty, whitespace.
- `SimpleConnectTransitionTests` — asserts that opening the Expander
  when IsConnected=true does NOT mutate IsConnected.

## Acceptance
- [ ] All 6 grep checks produce only intentional results
- [ ] Visual audit covers 8 pages × 2 modes × 2 themes × 2 languages =
  64 screenshots stored in `.claude/plans/v2.18-audit-screenshots/`
- [ ] At least 3 unit tests added for the just-fixed bug classes
- [ ] Every surfaced bug that can't be fixed in v2.18.2 filed as a
  new release in this roadmap with its own acceptance block

---

## Operational notes

### Release cadence
Each of v2.17.9 / v2.17.10 / v2.18.0 / v2.18.1 / v2.18.2 ships as its
own prerelease. Promote v2.18.2 → stable Latest only after visual
audit screenshots come back clean AND the user has smoke-tested the
whole Simple flow once.

### Rollback safety
v2.17.8 is the last known-good prerelease. If any of v2.17.9+ breaks
the launch (like v2.17.7 did), revert by demoting the broken release
and re-promoting v2.17.8.

### Bug-reporting convention
When a new bug surfaces during v2.18.x work, add it to the "Status
tracker" section at the bottom of this file with a link to its
diagnostic location. Don't let bugs accumulate only in git history.

### Grep hygiene after every UI release
Run Checks 1-5 as a pre-commit self-check on any release that modifies
`.axaml` files. Can be automated later; manual for now.

---

## Summary table

| Version   | Block | Deliverable                                    | Est. effort |
|-----------|-------|-------------------------------------------------|-------------|
| v2.17.9   | 1     | Expander write-back bug fixed                   | S           |
| v2.17.10  | 1     | Window teleport bug fixed                       | S           |
| v2.18.0   | 2     | Minimal Simple-mode header                      | M           |
| v2.18.1   | 3     | Memory investigation + low-hanging fixes        | M           |
| v2.18.2   | 4     | Hidden-bugs audit + unit tests + grep checks    | L           |

Legend: S = 1-2 h, M = 3-5 h, L = 1-2 days.

---

## Status tracker

### Known bugs (as of 2026-04-20)
- [x] Bug A — Expander collapses `IsConnected` via `{Binding !IsConnected}`
  two-way write-back — **fixed v2.17.9** (dedicated `SmpFormExpanded` observable)
- [x] Bug B — Window teleports to centre on theme/language toggle —
  **fixed v2.17.9** (WindowStartupLocation.Manual on rebuilt MainWindow,
  bundled into v2.17.9 since fix is 1 line + log statements)
- [ ] Bug C — 200 MB RAM, unknown cause
- [x] Design Q — Answered by the "VPNRouter Design System 2" handoff
  (`SimpleMode.html` — Variant A · Calm). Implemented in v2.18.0.

### Release tracker
- [x] v2.17.9  — Fix Bug A + Bug B (combined, both Simple-mode UX bugs)
- [~] v2.17.10 — merged into v2.17.9
- [x] v2.18.0  — Compact Simple mode (mini-header + status card +
  config row + 3-state CTA + Advanced card); big MainWindow header
  hidden in Simple mode; Disconnect CTA is accent-solid (benign
  toggle), NOT red/danger per design
- [ ] v2.18.1  — Bug C investigation
- [ ] v2.18.2  — Audit pass

Update this list as each release ships.

---

## Recorded user decisions (2026-04-20)

- Context will be compacted before work starts → keep this file
  complete enough to pick up cold.
- User prefers concrete plan with per-release acceptance criteria over
  quick iterative fixes (pattern from v2.15 / v2.16 / v2.17 plans).
- Safety-net audit (Block 4) is explicitly requested.

## References
- `.claude/plans/vpnrouter-v2.17-simple-mode.md` — v2.17 roadmap
- `.claude/plans/vpnrouter-v2.16-arctic-theme.md` — design system
- `.claude/plans/vpnrouter-v2.15-roadmap.md` — v2.15 roadmap
- `.claude/workflow.md` — git remotes, release policy, hotfix flow
