# VPNRouter v2.31.0-r4 — UX Closure Pillar (8 polish items)

Closes the v2.31.0 cycle. r4 picks up the eight visible-UI items
deferred from v2.30.7's extended audit + pre-release walk: chevron
flip, tooltip gaps, duplicate header, armed-state visual feedback,
inline confirmation toast, ComboBox surface mismatch, subtitle
truncation. Each is small but cumulative — together they remove most
of the "looks like a bug, isn't actually broken" friction left after
the v2.30.x cycle.

## Fixes

| ID | Where | What |
|---|---|---|
| **F-3** (UX-2) | `SimplePage.axaml` "Конфиг·Режим" card | Chevron `›` was static — didn't visually indicate whether `SmpFormExpanded` was on. Now flips `›` ↔ `▽` via the existing `BoolToChevronConverter` (extended to accept a `"TRUE_GLYPH\|FALSE_GLYPH"` parameter so each call site picks its own orientation). |
| **F-15** | `DpiBypassPage.axaml` Zapret service section | "Открыть меню service.bat" button had no tooltip — the label was self-explanatory only to users who already knew `service.bat`. Added `L_TipOpenServiceMenu` describing what the menu opens (Zapret service install/remove + strategy switch). |
| **F-18** (UX-65) | `FreeConfigsPage.axaml` Search-tab hero | "✓✓ Найти рабочие конфиги" appeared twice — once as section header, once as the button label. Dropped the header; hint + button label together carry the same intent. |
| **F-22** (UX-60) | `FreeConfigsPage.axaml` Saved-tab hint row | Subtitle was being mid-sentence-truncated with `TextTrimming=CharacterEllipsis` on narrow windows. Switched to `TextWrapping=Wrap` — the full hint is purposely informative ("they may stop working over time — click ↻ to recheck"). |
| **F-24** (UX-63) | `FreeConfigsPage.axaml` Speed/Скорость column | Rows with no measurement showed bare `—` with no tooltip explaining. Added `L_FcSpeedColumnTooltip` on every bandwidth cell ("speed measured during Deep verify; — means not measured"). |
| **F-26** | `MainWindowViewModel.RunHealthCheck` | After the Health Check report opened in Notepad, the menu dismissed silently — easy to miss on multi-monitor or when Notepad opened behind VPNRouter. Now an inline toast confirms "Отчёт сохранён и открыт в Блокноте". |
| **F-27** | `MainWindow.axaml` Reset menu item | Pre-fix the destructive "Reset config" menu item flipped its label to "Нажмите ещё раз для сброса" when armed, but had no visual change. Added a `.armed` style modifier — danger-tinted background + 1px danger border so the button rises out of the menu strip when armed. |
| **AU-10** | `MainWindowViewModel.AvailableRuleTypes` | Cards-mode Add-rule ComboBox didn't list `domain_regex` or `process_path` even though the Edit-mode validator accepts both. Surface mismatch — Edit-mode users could author rules of a type the form couldn't represent. Added both to the ComboBox in alphabetical position. |

## No new tests

All eight items are pure XAML / VM-glue UI polish. They depend on
visual rendering or a running app context — no Core-layer logic to
unit-test without a headless Avalonia harness (still backlog). Each
fix is small and the diffs are self-describing.

## Verification

- `dotnet build VPNRouter.sln -c Release` → 0 errors
- 25/25 regression tests pass (no Core changes)

## Cycle progress (closed)

| Pillar | Status |
|---|---|
| 1. Core stability (7+1 items) | r1: 7/8 done (AU-9 deferred to v2.31.1) |
| 2. A11y systemic (~20 items) | r2: 20/20 done |
| 3. ViewModels (4 items) | r3: 4/4 done |
| **4. UX closure (8 items)** | r4: 8/8 done |
| 5. Defer-if-time (3 items) | not picked up — deferred to v2.31.1 |

**Total v2.31.0 scope shipped: 39 fixes** (7 Core + 20 A11y + 4 VM + 8 UX) + 5 unit tests.

## Cycle next steps

When verification gate is green (r4 build + tests + Mac/Linux/APT CI +
12 assets) and no user-reported regressions surface within the usual
window, cut stable v2.31.0 per the rolling-rN policy.

## Cross-refs

- `plans/vpnrouter-v2.31.0-roadmap.md` — full v2.31 plan
- `plans/release-notes-v2.31.0-r3.md` — Pillar 3 (ViewModels)
- `plans/release-notes-v2.31.0-r2.md` — Pillar 2 (A11y)
- `plans/release-notes-v2.31.0-r1.md` — Pillar 1 (Core stability)
- `plans/vpnrouter-extended-audit-2026-05-02.md` — 47-finding audit
- `plans/vpnrouter-ux-audit-2026-05-01.md` — 72-finding audit
