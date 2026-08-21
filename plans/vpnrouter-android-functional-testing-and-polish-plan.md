# Android — functional testing + Advanced polish plan

**Date**: 2026-05-10
**Trigger**: Live verification of Advanced shell после Phase A-E parity merge passed (все 6 tabs visually mirror desktop). Next: validate behavior + polish residual visual issues. User feedback:
- "оформление в advansed немного расходиться, особенно плиточек" → tile/card styling polish needed
- "не очень удобно листать по горизонтали" → tab strip horizontal scroll UX is rough
- "нужно протестировать работоспособность каждой функции" → comprehensive functional smoke

**Test asset (user-provided)**:
```
https://example.invalid/redacted-test-subscription
```
Real subscription URL — use for end-to-end VPN test on phone.

---

## Goals

### Polish (visual)
1. Card/tile rendering matches desktop pixel-for-pixel (border-radius, padding, gap, shadow, active/hover states)
2. Tab strip horizontal scroll UX is comfortable (fade indicators / arrows / snap / compress)

### Functional (behavior)
3. Every UI control on every Advanced tab actually does its thing (not just renders)
4. End-to-end flow: paste subscription → fetch servers → pick → connect → VPN works → disconnect

### Regression fence
5. None of polish/test work breaks the v2.32.0 visual parity already shipped
6. Defects found during testing get isolated fix chips (don't bundle with polish)

---

## Polish breakdown

### POL-1 — Card / tile styling alignment

**Symptom (user)**: "плиточки немного расходятся" — likely Border properties on tab content cards drift from desktop.

**Subjects** (per tab):
- Servers tab: server-row Border styling (column header padding, row separator color, latency badge)
- Subscribe tab: Subscriptions section divider, Name+URL inputs Padding/CornerRadius, +Add button styling
- Settings tab: side-nav button states (active highlight vs hover), content cards (Split Tunnel / Full Tunnel radio cards have specific border-radius + padding on desktop)
- Applications tab: category sidebar buttons (active state styling), apps-row Border (right pane)
- Tools tab: Zapret/Telegram sub-tab segments, mode picker radio cards, "Turn on" button corner-radius
- Public tab: green CTA card padding, collapsible Settings border, Configs row template

**Acceptance**: side-by-side composite per tab with 2% pixel-tolerance — Borders/Padding/CornerRadius/Background tokens match desktop XAML.

**Effort**: 4-6h. One chip per tab, or single sweep chip.

### POL-2 — Tab strip horizontal scroll UX

**Symptom (user)**: "не очень удобно листать по горизонтали" — current `ScrollViewer.Horizontal` requires precise swipe; on narrow viewport 6 tabs don't fit.

**Options** (pick one — recommend opt 3):

1. **Edge fade gradient** — add left/right linear gradient masks to indicate "more tabs offscreen"; user still swipes manually. Lowest effort, smallest UX gain.
2. **Side arrow buttons** — `<` and `>` chips next to first/last visible tab; tap scrolls to next batch. Mid effort.
3. **Compress to fit** ⭐ — measure viewport, set `MinWidth` per tab so all 6 fit at once on phone width 1080 px (≈180 dp per tab — very comfortable). Active tab gets larger, inactive shrinks. No horizontal scroll needed. Best UX, mid effort.
4. **Bottom-nav-bar pattern** (mobile-native) — move tabs to bottom of overlay as 6-icon nav. Major restructure. Departs from desktop-mirror principle.

**Acceptance**: 6 tabs visible without horizontal scroll on 1080-px phone width. Touch target per tab ≥44 dp.

**Effort**: 2-3h (option 3) or 4-5h (option 4).

---

## Functional testing breakdown

### TEST-0 — Pre-flight (env setup)

1. Install fresh APK from current main HEAD on phone
2. Launch fresh + clear app data (`adb shell pm clear com.ninitux.vpnrouter`)
3. Verify Simple page renders v2.32.0 baseline (manual·all traffic / "Traffic goes straight" / Connect button)
4. Verify Advanced shell opens via kebab → Advanced button

### TEST-1 — Kebab functions (8 items)

Per item: tap → expected action → screenshot proof.

| # | Item | Expected |
|---|---|---|
| 1.1 | Light/Dark toggle | UI re-tints; setting persists across restart |
| 1.2 | RU/EN toggle | All visible strings flip; persists |
| 1.3 | Open log | Opens log overlay with singbox.log content |
| 1.4 | Check IP leak | Runs IP leak probe → shows result (toast / overlay) |
| 1.5 | Check for updates | Runs UpdateChecker → toast (no update / new version / error) |
| 1.6 | Run Health Check | Runs HealthCheck → result toast |
| 1.7 | Restart in Safe Mode | App restarts with safe-mode flag (no auto-start VPN) |
| 1.8 | Reset settings | Confirmation dialog → wipes settings → restart |

### TEST-2 — Advanced > Servers

| # | Action | Expected |
|---|---|---|
| 2.1 | Tap "Custom Config (JSON)" sub-tab | Sub-tab swaps to JSON paste textarea |
| 2.2 | Tap "Servers" sub-tab back | Returns to server list view |
| 2.3 | Paste valid `vless://` URI in input + tap "+ Add Server(s)" | Server appears in table with Server/IP/Ping/Port columns |
| 2.4 | Tap "Test all" | All listed servers get TCP+TLS probe → Ping column populates |
| 2.5 | Tap "Deep verify" | All servers get HTTP-through-tunnel test → status icons update |
| 2.6 | Tap server row | Row highlights as selected |
| 2.7 | Tap "Remove" with selected | Selected server gone |
| 2.8 | Multi-protocol parse: paste `hy2://...` then `tuic://...` | Both add as separate servers |

### TEST-3 — Advanced > Subscribe (uses user's test URL)

| # | Action | Expected |
|---|---|---|
| 3.1 | Empty state visible (no subs yet) | "No servers in any subscription yet — add one below and click ↻" |
| 3.2 | Type Name "Test" + URL `https://example.invalid/redacted-test-subscription` + tap "+ Add" | Subscription appears in Subscriptions section |
| 3.3 | Tap "Refresh all" | Server table populates with servers from the test sub |
| 3.4 | Verify aggregated table shows server count (e.g., 5 servers) | Visible servers with Server/IP/Ping/Port |
| 3.5 | Tap "Test all" | Latency probes complete, Ping column shows ms |
| 3.6 | Tap "Deep verify" | HTTP-through-tunnel results show |
| 3.7 | Edit subscription (rename/disable/delete — if UI exists) | Action takes effect |

### TEST-4 — Advanced > Settings

Per sub-section:

#### Routing
| 4.1 | Tap "Full Tunnel" radio | Selection moves; on next Connect routes ALL traffic via VPN |
| 4.2 | Tap "Split Tunnel" radio back | Returns to per-app routing; ConfigMode persists |
| 4.3 | Toggle "Russian traffic via real IP" checkbox | Setting persists; route rule applied on next Apply |
| 4.4 | Verify ✓ Auto-saved badge appears, then [Apply] button when needed | Footer state correct |

#### Rules (Android-specific)
| 4.5 | Verify Android explainer text shown ("Custom routing rules not wired yet") | Per AdvSettingsRulesAndroidNote |

#### Leak Protection
| 4.6 | Toggle DNS leak strategy / strict_route checkbox | Setting persists |

#### Content
| 4.7 | Toggle AdBlock / geosite-ads switches | Setting persists; rules added on next Apply |

#### Updates
| 4.8 | Switch update channel stable ↔ prerelease | Channel persists |
| 4.9 | Toggle auto-update | Setting persists |

#### Autostart
| 4.10 | Tap "Always-on VPN" deep-link button | Opens Android Settings → VPN screen |
| 4.11 | Toggle BootReceiver checkbox | Setting persists; verify by adb dumpsys after reboot (deferred) |

### TEST-5 — Advanced > Applications

| # | Action | Expected |
|---|---|---|
| 5.1 | Tap "Discord" category in sidebar | Right pane shows Discord app(s) only with checkbox(es) |
| 5.2 | Toggle a Discord app checkbox | App package added/removed from per-app list (`KeyPerAppPackages`) |
| 5.3 | Switch to "Browsers" category | Right pane swaps to browsers; previous selection preserved |
| 5.4 | Tap "+ New category" with name "Music" | Custom category appears under sidebar |
| 5.5 | Tap "Music" category | Right pane shows empty + add-app picker (or instructions) |
| 5.6 | Verify category counts (numbers next to category names) | Counts reflect selected apps per category |

### TEST-6 — Advanced > Tools

#### Zapret sub-tab
| 6.1 | Tap "Standard" mode radio | DPI bypass mode flips to standard (KeyDpiBypassMode = "standard") |
| 6.2 | Tap "Aggressive" mode radio | Mode flips to aggressive |
| 6.3 | Tap "Off" mode | DPI bypass disabled |
| 6.4 | Tap "Turn on" inline button | tls_fragment outbound activated on next Apply (verify via current.json on phone if accessible) |

#### Telegram proxy sub-tab
| 6.5 | Tap "Open Telegram" deep-link button | Launches `org.telegram.messenger` if installed; falls through to Play Store otherwise |

### TEST-7 — Advanced > Public

| # | Action | Expected |
|---|---|---|
| 7.1 | Tap "✓✓ Find working configs" CTA | FreeConfigs orchestrator runs; configs appear in table |
| 7.2 | Tap "▾ Settings" expander | Filter chips visible (latency / region / protocol) |
| 7.3 | Adjust filter, tap Find again | Filtered results |
| 7.4 | Tap a config row | Row highlights, "Connect to selected" enables |
| 7.5 | Tap "Connect to selected" | VPN connects to selected free config |
| 7.6 | Switch to "★ Saved" sub-tab | Shows configs from previous Find runs (cumulative auto-save — every Verified entry is persisted, no per-row gesture). Empty on first launch. |

### TEST-8 — End-to-end VPN flow (USES user's subscription URL)

Critical real-world flow. Phone has internet via WiFi/cellular before this test.

| # | Step | Expected |
|---|---|---|
| 8.1 | Clear app data, fresh first-launch | Simple page baseline |
| 8.2 | Open Advanced > Subscribe → add `https://example.invalid/redacted-test-subscription` (Name "Test") | Sub appears |
| 8.3 | Tap "Refresh all" | Server list populates |
| 8.4 | Switch to Advanced > Servers → verify aggregated servers visible | Servers shown |
| 8.5 | Tap "Test all" → wait for ping results | All servers show ms latency |
| 8.6 | Pick fastest server (tap row) | Selected |
| 8.7 | Tap persistent footer "▶ Start VPN" | Android VPN permission dialog (first time) → grant → VPN connects |
| 8.8 | Verify Simple page Connected state on next look ("Connected · MM:SS" + green dot) | OK |
| 8.9 | Open phone browser, visit `https://ifconfig.io/ip` | Returns server's exit IP, NOT phone's local IP |
| 8.10 | Tap "Disconnect" | VPN drops; ifconfig.io returns local IP again |
| 8.11 | Reboot phone (if Always-on VPN not enabled) → verify VPN does NOT auto-start | Confirms autostart off |
| 8.12 | Enable Always-on VPN in Android Settings → VPN → reboot | VPN auto-starts after boot |

### TEST-9 — Defect catalog

For every failure (TEST-1 ... TEST-8 step that doesn't match expected), file:
- Severity (P0 = data loss / silent leak / crash; P1 = visible UX bug; P2 = polish)
- Surface (which tab / control)
- Repro steps
- Expected vs actual
- Screenshot/log

Each P0 / P1 defect → separate fix chip (named DEFCT-XXX).

---

## Pool composition (proposed)

When you compact dialog and we spawn:

### Polish chips
- **POL-1-CARDS** — sweep through 6 Advanced tabs, align Border / Padding / CornerRadius / shadow tokens to desktop XAML. ~4-6h.
- **POL-2-TABS** — fix tab strip horizontal scroll: pick option 3 (compress to fit on phone width) by default. ~2-3h.

### Functional test chips
- **TEST-RUN-01** — execute TEST-1 (kebab, 8 items) live on phone via adb. Report PASS/FAIL with screenshots per item. ~1h.
- **TEST-RUN-02** — execute TEST-2 (Servers tab). ~1h.
- **TEST-RUN-03** — execute TEST-3 (Subscribe tab + user's URL load). ~1h.
- **TEST-RUN-04** — execute TEST-4 (Settings sub-sections). ~1.5h.
- **TEST-RUN-05** — execute TEST-5 (Applications categories). ~1h.
- **TEST-RUN-06** — execute TEST-6 (Tools mode picker + Telegram intent). ~0.5h.
- **TEST-RUN-07** — execute TEST-7 (Public Find/Saved/Connect). ~1h.
- **TEST-RUN-08** — execute TEST-8 (end-to-end with subscription URL). ~1.5h. **Critical** — actually starts VPN.

### Defect-fix chips (spawned reactively per failure found)
- **DEFCT-XX-N** — one chip per actionable bug. Spawned only if TEST-RUN finds something.

### Total effort estimate
~14-19h if everything passes first time. Add 4-8h for typical defect-fix loop (estimated 3-5 P1 defects). Realistic: ~20-25h pool.

---

## Order of execution (recommended)

1. **POL-2-TABS first** (smallest, biggest UX gain — comfortable tab navigation makes subsequent tests less frustrating)
2. **POL-1-CARDS** (visual polish before content tests)
3. **TEST-RUN-01 ... 07** in parallel (each chip independent)
4. **TEST-RUN-08** end-to-end (after others — gives confidence in all subsystems before the integration flow)
5. **DEFCT-XX-N** chips spawn as failures surface
6. **Re-run failing TEST-RUN-XX** chips after defect-fix lands

---

## Scope guardrails

- **Don't touch desktop**. Anything that requires desktop change → flag, don't change.
- **Don't ship `-rN`**. All work on `main`; user gates ship cycle.
- **v2.32.0 desktop = canonical reference**, captured per parity-audit findings.
- **Platform-impossible items** (Wintun / ETW / netsh / Windows Service / TgProxy daemon / Zapret winws.exe) — keep Android substitutions in place; flag if test reveals one slipped through.
- **End-to-end VPN test must be on real cellular or WiFi**, not VPN-over-VPN — phone needs unrestricted internet for ifconfig.io probe to work.

---

## Test asset details

- **Test subscription URL**: `https://example.invalid/redacted-test-subscription`
- **Test name**: "Test" or "ninitux"
- **Expected protocol**: VLESS+Reality (likely)
- **Expected server count**: TBD (will see when refresh runs)

If subscription returns 0 servers / errors → **DEFCT** filed instead of test-skip.

---

## Status

Plan ready. Awaiting:
1. User compacts dialog
2. User confirms pool composition (or proposes adjustments)
3. Pool spawns

Notes for when we spawn:
- Each TEST-RUN-XX chip needs explicit live-test-on-phone instructions (capture screenshot per step, log adb output, write report markdown).
- Each chip prompt must include the test asset URL where relevant.
- Defect-fix chips can be spawned in parallel with subsequent tests if independent.
