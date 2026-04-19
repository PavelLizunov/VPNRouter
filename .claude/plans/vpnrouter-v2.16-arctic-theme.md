# VPNRouter — Roadmap v2.16 "Arctic theme migration"

**Baseline**: v2.15.8 stable Latest (Block 1–4 of v2.15 roadmap complete).

**Source of truth**: `VPNRouter Design System/` folder at project root.
Generated via Claude Design on 2026-04-19. Contents:
- `tokens.css` — 245-line reference with semantic tokens, light + dark
- `system/{colors,typography,spacing,components,brand}.html`
- `UIKit.html` — main-window mockup in the new system
- `assets/penguin.svg`, `assets/logo-lockup.svg` — **not used; we keep our
  existing penguin_logo.png** (explicit user decision)

**Goal**: replace the scattered raw-hex colours currently sprinkled across
every `.axaml` (≈60+ unique hex values) with a single semantic-token
system, enable a proper bespoke dark theme, and rebrand the accent from
the ubiquitous indigo `#2563EB` to the arctic cyan `#38BDF8` / `#0EA5E9`
that matches the "Virtual Penguin Network" name.

**Kept from current**:
- Product name: **Virtual Penguin Network**
- Penguin mascot PNG (`Assets/penguin_logo.png`) — user likes it;
  dark-theme variant will reuse existing `b_icon.png` / `w_icon.png`
  (already in Assets, not currently wired up) OR generate inverted
  version. **No new SVG.**
- Window title font / sizes
- Six-tab primary nav: Manual · Subscribe · Network · Apps · Tools · Free
- Master-detail 160px sidebar layout on complex pages
- Avalonia FluentTheme as base (our tokens override selected brushes on top)

**Not in scope**:
- Layout changes to existing pages (that's a separate audit pass)
- New features or screens
- macOS-specific design work (DMG InstallGuide stays as-is)

---

## Priority order

### Block 1 — Tokens infrastructure
1. **v2.16.0** — Create `Styles/Tokens.axaml` resource dictionary + inject
   into `App.axaml`. No UI change yet.

### Block 2 — Migrate UI to tokens
2. **v2.16.1** — Migrate `MainWindow.axaml` header + tab strip to tokens.
3. **v2.16.2** — Migrate Servers / Subscribe pages (hottest pages, share
   the ServerViewModel DataTemplate).
4. **v2.16.3** — Migrate Network / Applications / Tools / DpiBypass /
   Telegram pages.
5. **v2.16.4** — Migrate FreeConfigsPage (biggest single file, most hex).

### Block 3 — Dark theme as first-class citizen
6. **v2.16.5** — Proper dark theme via token overrides + theme-aware
   logo swap (light/dark PNG).

### Block 4 — Scale consistency
7. **v2.16.6** — Typography shakedown: collapse ~6 ad-hoc FontSize
   variants onto the 9/10/11/12/13/15/18/22 scale.
8. **v2.16.7** — Spacing + radius consistency pass (4 px grid, 3/6/8/10/14/pill).

### Closing
9. **v2.16.8** — Motion + focus ring polish (optional; only if users
   report jarring transitions after .5/.6/.7).

---

# v2.16.0 — Tokens infrastructure

**Goal**: zero user-visible change, but `Styles/Tokens.axaml` exists and
is loaded globally so subsequent releases can start using `DynamicResource`.

## Files to change

### New: `VPNRouter.App/Styles/Tokens.axaml`
ResourceDictionary mirroring `tokens.css`. Export every semantic token as
both a `Color` AND a `SolidColorBrush` (Avalonia uses brushes in bindings;
colours are useful for programmatic mixing).

Token families to port (from tokens.css):
- `SurfaceApp` / `SurfaceSunken` / `SurfaceBase` / `SurfaceRaised` / `SurfaceOverlay`
- `TextPrimary` / `TextSecondary` / `TextMuted` / `TextInverse` / `TextAccent`
- `BorderSubtle` / `BorderDefault` / `BorderStrong` / `BorderAccent`
- `AccentBgSubtle` / `AccentBgMuted` / `AccentBorder` / `AccentFg` /
  `AccentSolid` / `AccentSolidHover` / `AccentOnSolid`
- State families (`Success*` / `Warning*` / `Danger*` / `Info*` — each
  with Bg / Border / Fg / Solid)
- `ShadowXs/Sm/Md/Lg` — BoxShadows (Avalonia-friendly conversion)

Light values = tokens.css `:root`.
Dark values = tokens.css `:root[data-theme="dark"]` — store in a second
file `Styles/Tokens.Dark.axaml`, swapped at runtime in v2.16.5.

Also expose the type scale as StaticResources:
- `FsXs / FsSm / FsMd / FsLg / FsXl / Fs2Xl / Fs3Xl` (double values)
- `FwRegular / FwMedium / FwSemibold / FwBold` (`FontWeight`)

And geometry:
- `RadiusXs / Sm / Md / Lg / Xl` (`CornerRadius`)
- `Space1..10` (`Thickness` helpers if needed; raw double too)

### Modified: `VPNRouter.App/App.axaml`
Add `<ResourceInclude Source="avares://VPNRouter.App/Styles/Tokens.axaml"/>`
inside `Application.Resources`.

## Testing
- `dotnet build VPNRouter.sln` → 0 errors
- Launch app → all pages render identical to v2.15.8 (no consumer yet)
- Open XAML designer → Tokens.axaml resources resolvable via IntelliSense

## Acceptance
- [ ] `Styles/Tokens.axaml` exists with 40+ named brushes
- [ ] `Styles/Tokens.Dark.axaml` exists with parallel dark values
- [ ] Tokens loaded in `App.axaml`
- [ ] No user-visible change on any page

## Risk
- If a token name clashes with Avalonia's built-in `SystemControlForegroundBaseMediumLowBrush`
  etc. — prefix all our keys (e.g. `Vpn.AccentSolid`) to be safe.
- FluentTheme colours still provide the base for controls we don't
  override; don't try to replace all of them in v2.16.0.

---

# v2.16.1 — MainWindow header + tab strip

**Goal**: the header (logo + title + version + status badges + theme/lang
toggles + update notification banner) and the tab strip use tokens only.

## Files to change

### `VPNRouter.App/Views/MainWindow.axaml`
Replace every literal hex in the header + tab strip:

| Current literal | Token to use |
|---|---|
| `#2563EB` (title) | `{DynamicResource AccentFg}` — arctic accent text |
| (if any) `#FEF3C7` update banner bg | `{DynamicResource WarningBg}` |
| (if any) `#F59E0B` update banner border | `{DynamicResource WarningBorder}` |
| (if any) `#92400E` update banner text | `{DynamicResource WarningFg}` |
| Tab strip divider | `{DynamicResource BorderDefault}` |
| Badge backgrounds | `{DynamicResource SuccessSolid}` / `WarningSolid` / `DangerSolid` / neutral |

Badge brush now lives in VM (`VpnBadgeBrush` etc.). Decide: keep VM
returning raw `SolidColorBrush`, OR return key name and bind via
`{Binding ..., Converter=LookupResource}`. Simplest: leave VM brushes
as-is but change their Color values to the new `Success/Danger/Gray`
(emerald → `#16A34A`, red → `#DC2626`, gray → `#94A0B2`).

### `VPNRouter.App/Views/MainWindow.axaml.cs`
No change (still uses DataContext from MainWindowViewModel).

### Typography
Header already uses FontSize 9/10/13 — acceptable, align with scale
`FsXs=10, FsSm=11, FsLg=13`. Bump version text from 9 → 10 for a11y.

## Testing
- Light mode (default): header looks similar but accent blue is cyaner
- Tab selected: underline/chip uses AccentSolid
- Status dashboard badges change colour as before (emerald / red / gray)
- Tooltip hover still works
- Hover on tabs: `AccentBgMuted` tint

## Acceptance
- [ ] MainWindow.axaml has zero raw hex (`grep "#[0-9a-fA-F]\{6\}"` empty)
- [ ] Title colour is arctic, not classic blue
- [ ] Badge colours unchanged in meaning (emerald/red/gray) but match
  new `success-solid` / `danger-solid` / `slate-400` values
- [ ] Tab strip rendering unchanged in layout

---

# v2.16.2 — Servers + Subscribe pages

**Goal**: both pages (share the ServerViewModel DataTemplate) use tokens.
The Test-all / Deep-verify buttons drop their ad-hoc `#059669` / `#7C3AED`
for semantic tokens.

## Button colour mapping (user-visible change)

| Button | v2.15 | v2.16 |
|---|---|---|
| Connect / primary | `#2563EB` | `{DynamicResource AccentSolid}` = `#0EA5E9` |
| Test all | `#059669` (green) | `{DynamicResource SuccessSolid}` = `#16A34A` |
| Deep verify | `#7C3AED` (purple) | Keep purple OR rebrand to accent-variant. **Pending user preference** — default keep purple (only 2 uses, semantically distinct). |
| Cancel / Remove | `#EF4444` | `{DynamicResource DangerSolid}` = `#DC2626` |

## Status dot colours in ServerViewModel

Currently in `ServerViewModel.StatusDotBrush`:
- `#10B981` (emerald) → `{DynamicResource SuccessSolidBrush}`
- `#F59E0B` (amber)   → `{DynamicResource WarningSolidBrush}`
- `#EF4444` (red)     → `{DynamicResource DangerSolidBrush}`
- `#9CA3AF` (gray)    → `{DynamicResource TextMutedBrush}` (or `slate-400`)

Because ServerViewModel can't use `DynamicResource` directly from code,
expose these as static readonly brushes loaded from `Application.Current.Resources`
on first access.

## Files to change
- `Views/Pages/ServersPage.axaml`
- `Views/Pages/SubscribePage.axaml`
- `ViewModels/ServerViewModel.cs` — refactor BadgeBrush getters

## Acceptance
- [ ] Both files grep-clean of `#[0-9a-fA-F]{6}` (allow emoji fg exceptions)
- [ ] Per-row status dot renders identically in terms of semantics
- [ ] Test-all / Deep-verify buttons still visually distinct

---

# v2.16.3 — Network / Applications / Tools / DpiBypass / Telegram

**Goal**: finish the mid-complexity pages.

## Per-page notes
- **NetworkPage** — mostly checkboxes + labels. Border colours on the
  2-column settings layout go from ad-hoc to `BorderDefault` + `SurfaceSunken`
  for the inner cards.
- **ApplicationsPage** — category chips, app-list rows; active category
  uses `AccentBgSubtle`.
- **ToolsPage** — sub-tab strip ListBox → token-driven.
- **DpiBypassPage** — strategy picker accent + "Zapret running" badge =
  SuccessBg/SuccessFg.
- **TelegramPage** — "Copy secret" button accent; Python-runtime state
  badge.

## Acceptance
- [ ] All 5 files grep-clean of raw hex
- [ ] Checkbox ticks use AccentSolid (via FluentTheme accent override)
- [ ] No regressions on Tools sub-navigation

---

# v2.16.4 — FreeConfigsPage migration

**Goal**: FreeConfigsPage is the biggest offender — ~30 unique hex in a
single file (Quickstart banner, dashboard cards, Smart-refresh panel,
Fast-scan panel, Deep-verify section, pool-aggregator card, bottom
status bar, empty-state CTA). Dedicated release so a regression here
doesn't block the shorter pages.

## Special cases

- **Quickstart banner** (`#EFF6FF` + `#2563EB`) → `InfoBg` + `InfoBorder`
- **6-card dashboard** — each card has its own tint today:
  - Total (gray)       → `SurfaceSunken` + `TextPrimary`
  - Verified (emerald) → `SuccessBg` + `SuccessFg`
  - Working (blue)     → `InfoBg` + `InfoFg`
  - Fake (amber)       → `WarningBg` + `WarningFg`
  - TLS failed (red)   → `DangerBg` + `DangerFg`
  - Unreach (gray)     → `SurfaceSunken` + `TextMuted`
- **Smart refresh panel** (`#EFF6FF` bg + `#1E3A8A` fg) → `AccentBgSubtle`
  + `AccentFg`
- **Fast scan panel** (`#FEF3C7` + `#78350F`) → `WarningBg` + `WarningFg`
- **Deep verify panel** (`#ECFDF5` + `#065F46`) → `SuccessBg` + `SuccessFg`

## Acceptance
- [ ] FreeConfigsPage.axaml grep-clean of raw hex
- [ ] All 6 dashboard cards render semantically consistent colours
- [ ] No regressions in the 6-section master-detail layout
- [ ] Security warning dialog still visually distinct

---

# v2.16.5 — Proper dark theme + inverted logo

**Goal**: dark mode stops being "Avalonia Fluent darkening" and becomes a
first-class bespoke experience.

## Files to change

### `App.axaml.cs`
At theme-toggle time, swap the ResourceDictionary:
```csharp
// On ToggleTheme:
var tokens = IsDarkTheme
    ? "avares://VPNRouter.App/Styles/Tokens.Dark.axaml"
    : "avares://VPNRouter.App/Styles/Tokens.axaml";
// Load + replace Application.Current.Resources.MergedDictionaries[0]
```

Keep FluentTheme at base layer; our tokens override only what's named
in Tokens.axaml.

### `ViewModels/MainWindowViewModel.cs`
`LogoSource` becomes theme-aware:
```csharp
public Bitmap LogoSource => IsDarkTheme ? _logoDark : _logoLight;
[NotifyPropertyChangedFor(nameof(LogoSource))]
private bool _isDarkTheme;
```
`_logoLight` = current `penguin_logo.png` (unchanged).
`_logoDark` = same penguin with inverted/adapted colours — options:
- **Option A (preferred)**: pre-render a dark variant PNG. User has
  existing `w_icon.png` (495 KB) and `b_icon.png` (673 KB) in Assets/ —
  if either matches the penguin invert, wire that up.
  Otherwise add `penguin_logo_dark.png` generated manually in an
  image editor (keep aspect + glass shine; invert only body/outline).
- **Option B**: load the PNG at startup and programmatically invert
  RGB channels (preserve alpha). Works but looks flat — cheapest.
- **Option C**: Avalonia `ColorMatrixEffect` on the Image. Live invert,
  no asset duplication; cost: blurs colour nuance.

Default: try Option A first using existing assets. Fall back to B if
they don't match.

### `Views/MainWindow.axaml`
The Window itself picks up `SurfaceApp` background from tokens, so no
direct colour change needed. Existing ApplyTheme() call stays (toggles
FluentTheme variant for controls we don't style).

## Testing
- Toggle Dark → accent turns to `ref-arctic-300` (lighter cyan for
  contrast against dark slate), text is `#EEF1F6`, borders are
  `rgba(255,255,255,0.08)`, badges keep semantic meaning but brighter
  text on darker tinted backgrounds
- Logo swaps without flicker
- Return to Light → everything reverts crisply
- Screen capture both modes to `.claude/plans/v2.16-dark-before-after.png`
  for regression comparison

## Acceptance
- [ ] Dark theme reads as arctic, not sepia/washed
- [ ] Logo is legible in both modes (check against header gradient/surface)
- [ ] No page has illegible text (contrast ratio ≥ 4.5 for body,
  ≥ 3.0 for large)
- [ ] Theme persists across app restart (AppSettings.Theme already does this)

---

# v2.16.6 — Typography scale consistency

**Goal**: collapse current ad-hoc font sizes (8 / 9 / 10 / 11 / 12 / 13
— chosen arbitrarily) onto the design system scale (9 / 10 / 11 / 12 /
13 / 15 / 18 / 22).

## Audit step first
`grep -rhE 'FontSize="[0-9]+"' VPNRouter.App/Views/ | sort -u | uniq -c`

Expected outcome: a handful of remappings, ~50 lines touched:
- `FontSize="8"` → `FontSize="{StaticResource Fs2Xs}"` (9 px)
- `FontSize="9"` → `FontSize="{StaticResource Fs2Xs}"`
- `FontSize="10"` → `FontSize="{StaticResource FsXs}"`
- `FontSize="11"` → `FontSize="{StaticResource FsSm}"`
- `FontSize="12"` → `FontSize="{StaticResource FsMd}"`
- `FontSize="13"` → `FontSize="{StaticResource FsLg}"`
- `FontSize="14"` → `FontSize="{StaticResource FsLg}"` (collapse 14 → 13)

## Acceptance
- [ ] All .axaml FontSize= values reference a StaticResource
- [ ] Visually compare before/after screenshots — no layout breakage
  (typography shrinks 14→13 are tight; re-widen a column if a header wraps)

---

# v2.16.7 — Spacing + radius pass

**Goal**: `Margin`, `Padding`, `CornerRadius` use the design system
rhythm.

## Grid enforcement
Most of our numbers are already multiples of 2 or 4. Outliers:
- `Padding="6,4"` on inputs → keep (fits `space-3, space-2`)
- `Padding="10,6"` on banners → `Padding="{StaticResource Space5,Space3}"` = 12,6
- `CornerRadius="3"` → `{StaticResource RadiusXs}` (3)
- `CornerRadius="4"` → `{StaticResource RadiusSm}` (6) — yes, slight bump
- `CornerRadius="6"` → `{StaticResource RadiusSm}`
- `CornerRadius="8"` → `{StaticResource RadiusMd}`
- `CornerRadius="10"` → `{StaticResource RadiusLg}`

## Shadows
Add a `BoxShadow` to raised cards using `ShadowMd` for dark-mode depth
(fluent light mode needs no shadows usually — keep current flat look).

## Acceptance
- [ ] All `CornerRadius=` use a StaticResource
- [ ] Raised cards on dark mode have `ShadowMd`
- [ ] No element looks "too rounded" after 4→6 bump (spot-check chips,
  buttons, list items)

---

# v2.16.8 — Motion + focus (optional)

**Goal**: add subtle motion + a clear focus ring.

## Motion
Define `Styles/Motion.axaml` with `Duration` + `Easing` resources:
- `DurationFast` 140 ms, `DurationBase` 220 ms
- `EaseOut` cubic-bezier(0.2, 0.8, 0.2, 1)

Apply to hover/press transitions on Buttons, TabItems, checkboxes.

## Focus ring
3 px outer ring using `AccentBgMuted` (rgba arctic 0.35) on focused input.
Currently Fluent uses a 2 px dark border — replace via Button/TextBox
Style triggers.

## Acceptance
- [ ] Button hover has ~140 ms fade, not instant
- [ ] Tab switch animates subtly
- [ ] Keyboard focus is unmistakable (Tab through every input)

---

## Operational notes (for Claude implementing)

### Grep hygiene per release
After every XAML migration release run:
```powershell
Select-String -Path VPNRouter.App/Views -Pattern '#[0-9a-fA-F]{6}' -Recurse
```
The file the release targets should come back empty (or only contain
intentional exceptions like emoji forced colours).

### Regression prevention
- Never change a token's *semantic meaning* between releases — only its
  hex value. Downstream consumers shouldn't know.
- Keep `FluentTheme` enabled throughout (FluentTheme at base, our tokens
  override on top). Killing it breaks Scrollbars, ComboBox popup, ContextMenu.

### Dark-theme specific
- Avalonia caches `DynamicResource` per-root; swapping the merged dict
  at runtime requires invalidating the window visual tree. If flash is
  ugly, rebuild MainWindow like we do on language toggle (v2.15.6).
- The status badges on the MainWindow are SolidColorBrush allocated in
  C#. When dark theme flips, brushes don't auto-refresh — expose them as
  `DynamicResource` lookups in `UpdateRuntimeStatus` instead.

### Release cadence
Ship one block at a time behind `--prerelease` until a whole block is
smoke-tested, then promote. v2.16 shouldn't be promoted to stable
until Block 3 (dark theme) is done and verified — otherwise users get
half-migrated colours.

### Consolidation (do during Block 2 or 3 cleanup)
Move `FreeConfigDeepVerifier` and `VlessDeepVerifier` onto a shared
`SingBoxProbeHost` class. Noted in `workflow.md` as deferred from v2.15.3.

---

## Summary table

| Version  | Block | Deliverable                                   | Est. effort |
|----------|-------|-----------------------------------------------|-------------|
| v2.16.0  | 1     | Tokens.axaml + Tokens.Dark.axaml infra        | M           |
| v2.16.1  | 2     | MainWindow migrated                           | S           |
| v2.16.2  | 2     | Servers + Subscribe migrated                  | M           |
| v2.16.3  | 2     | Network + Apps + Tools + DpiBypass + Telegram | L           |
| v2.16.4  | 2     | FreeConfigsPage migrated                      | M           |
| v2.16.5  | 3     | Dark theme via tokens + inverted logo         | M           |
| v2.16.6  | 4     | Typography scale pass                         | S           |
| v2.16.7  | 4     | Spacing + radius pass                         | S           |
| v2.16.8  | 4     | Motion + focus ring (optional)                | S           |

Legend: S = 1-2 h, M = 3-5 h, L = 1-2 days

Total for a full v2.16 rollout: ~3-4 days of focused work.

---

## Status tracker

- [ ] v2.16.0 — Tokens.axaml infrastructure
- [ ] v2.16.1 — MainWindow migration
- [ ] v2.16.2 — Servers + Subscribe migration
- [ ] v2.16.3 — Network / Apps / Tools / DpiBypass / Telegram migration
- [ ] v2.16.4 — FreeConfigsPage migration
- [ ] v2.16.5 — Bespoke dark theme + inverted logo
- [ ] v2.16.6 — Typography scale pass
- [ ] v2.16.7 — Spacing + radius pass
- [ ] v2.16.8 — Motion + focus ring (optional)

Keep this checklist updated as each release ships so future
context-compacted sessions pick up where we left off.

---

## References

- Source tokens: `VPNRouter Design System/tokens.css`
- Reference mockup: `VPNRouter Design System/UIKit.html` (two variants:
  Technical / Friendly — we implement Technical as default)
- Component specs: `VPNRouter Design System/system/components.html`
- Brand voice + principles: `VPNRouter Design System/system/brand.html`
- Previous v2.15 roadmap (for structure): `.claude/plans/vpnrouter-v2.15-roadmap.md`
- Workflow policies (git remotes, release flow, etc.): `.claude/workflow.md`

## Explicit user decisions recorded here

1. **Keep our penguin logo** (`Assets/penguin_logo.png`). Do not swap
   to the Claude-Design-generated `assets/penguin.svg`.
2. **Dark theme icon** = inverted colour variant of the same penguin
   (reuse existing `w_icon.png` / `b_icon.png` if suitable, else
   generate `penguin_logo_dark.png` with inverted palette). Do NOT
   redesign the mascot.
3. **Product name stays** "Virtual Penguin Network".
4. **Accent rebrand approved**: indigo `#2563EB` → arctic cyan `#38BDF8` / `#0EA5E9`.
