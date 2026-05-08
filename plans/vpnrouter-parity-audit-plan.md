# VPNRouter — comprehensive UI/UX parity audit (desktop ↔ Android)

**Goal**: pixel-by-pixel + behavior-by-behavior comparison of desktop (Win
v2.32.0) and Android (3.0.0-android-alpha) builds. Catalog every divergence
(visual, interactional, state, animation, copy, color, layout). Spawn fix
chips. Re-test. Iterate until parity achieved.

**Created**: 2026-05-09 after AND-PROFILES + AND-CACHE-RECOVERY merge.

**Scope**: comparing the 9 desktop pages × 2 themes × 2 languages × N states each
against their Android counterparts. Approximate: 9 pages × 4 (theme×lang) × ~3
states/page = ~108 screenshot pairs minimum; far more with all hover/disabled
states.

---

## Methodology — 14 capture & analysis methods

User asked for 10+. Here's 14, ordered from cheapest-fastest to deepest:

### M1 — Side-by-side full-page screenshot grids

For each page, capture desktop + Android with identical state. Render in a
3-column markdown grid: desktop / android / delta-note. Visual eyeball pass.

- **Desktop**: `mcp__computer-use__screenshot` after navigating in VPNRouter.exe
- **Android**: `adb shell screencap -p /sdcard/X.png && adb pull` via Mac SSH
- **Output**: `parity-audit/page-N-state-K/desktop.png` + `android.png`

### M2 — ImageMagick `compare` pixel diff

For each page pair where intent is "should look the same":

```bash
magick compare -metric AE -fuzz 5% desktop.png android.png diff.png
```

Outputs differing pixel count + visual diff PNG (red overlay on differing
pixels). Threshold: 5% fuzz to account for AA/font rendering. Report
absolute count + percentage.

### M3 — Perceptual diff via `dssim` / `pdiff`

Pixel diff misses meaningful UX changes (e.g., colors that ARE supposed to be
different but rendered close enough). Perceptual diff catches those. Use
`dssim` (Rust port of SSIM) — sensitive to structural changes humans
actually notice.

```bash
dssim desktop.png android.png  # outputs structural similarity score 0-1
```

### M4 — Color palette extraction

Desktop uses Arctic palette from `Styles/Tokens.axaml`. Android linked the
same file. Verify dominant colors match per page:

```python
from PIL import Image
from collections import Counter
top10 = Counter(Image.open(p).convert("RGB").getdata()).most_common(10)
```

If top 10 dominant colors differ between platforms, document the divergence.

### M5 — Layout grid extraction (heatmap)

Render both screenshots through edge-detection → identify rectangular
content regions → compare counts, sizes, aspect ratios. Catches:
- Card-vs-no-card layout differences
- Padding/margin asymmetry
- Element-count divergence (extra button on one side)

### M6 — Text content audit (OCR or accessibility tree)

Run OCR on each pair (Tesseract) to extract every visible text string.
Diff the string lists. Catches:
- Translation drift (RU on one, EN-leaked on other)
- Different button labels for same function
- Localization key absent on one platform

```bash
tesseract desktop.png - -l rus+eng | sort -u > desktop.txt
tesseract android.png - -l rus+eng | sort -u > android.txt
diff desktop.txt android.txt
```

### M7 — Localization key parity (static analysis)

Don't rely only on visible OCR. Read the source-of-truth localization files:
- `VPNRouter.App/Localization/Strings.cs`
- `VPNRouter.Android/Localization.cs`

Diff key sets and value sets. Catches strings that exist on one platform
but never on the other (potentially never visible on the platform that
lacks them).

### M8 — UI element catalog (XAML × programmatic)

Parse desktop's `*.axaml` page files via XML → list of elements with
{tag, name, automation-id, text}. Parse Android's `AndroidApp.axaml.cs`
via regex on `new Border` / `new TextBlock` / `new Button` etc → same
shape list.

Diff catalogs. Flags:
- Extra element on one side (e.g., desktop has "Reset to defaults" button
  Android doesn't)
- Different element type for same function (Slider vs NumericUpDown)
- Different ordering

### M9 — Computer-use click trace (desktop)

Record desktop click sequence: open app → tap each menu/button/page → log
which page transition happened, what toast appeared, what setting persisted.
Output: `parity-audit/desktop-trace.txt` with timestamped events.

### M10 — adb input trace (Android)

Mirror M9 on Android via `adb shell input tap` + screencap each step. Output:
`parity-audit/android-trace.txt`.

### M11 — Click → state-transition diff

Pair M9 + M10 by canonical action (e.g., "open Servers page", "click first
server", "toggle Connect"). For each canonical action, record:
- What screen appeared on desktop / Android
- What text changed
- What animation played
- Time-to-stable

Diff these. Catches divergent navigation / hidden steps / extra confirmations.

### M12 — Network request capture

Desktop: `Wireshark` or netsh trace. Android: `tcpdump-android` via root
(skip — phone not rooted). Alternative: read application log
`vpnrouter.log` + `singbox.log` for HTTP API calls during identical action
on both sides.

Catches:
- Desktop calls API X, Android doesn't
- Different update-check URLs / patterns
- Subscription fetch differences

### M13 — Theme/language matrix

For each page, do M1+M2 across 4 combinations: (Light RU, Light EN, Dark RU,
Dark EN). 9 pages × 4 = 36 desktop screenshots × 36 Android = 72 captures.

Catches:
- Text overflow when RU translation longer than EN
- Dark mode color regression on one platform
- Missing localization fallback

### M14 — Performance / animation timing

For canonical action (cold start, page transition, connect button):
- Desktop: stopwatch via mcp__computer-use__screenshot before+after,
  measure elapsed
- Android: `adb shell am start -W` reports total time. For animations:
  capture frames at 100ms intervals, count frames until UI stable

Document: cold-start delta, page-transition delta, connect-toggle delta.

---

## Page inventory (9 desktop pages)

Per `VPNRouter.App/Views/Pages/*.axaml`:

| # | Page | XAML | Android counterpart |
|---|---|---|---|
| 1 | Simple | `SimplePage.axaml` | inline at AndroidApp main scroller |
| 2 | Subscribe | `SubscribePage.axaml` | `AndroidApp.SubscribePage.cs` overlay |
| 3 | Servers | `ServersPage.axaml` | `AndroidApp.ServerList.cs` overlay |
| 4 | Free Configs | `FreeConfigsPage.axaml` | `AndroidApp.FreeConfigs.cs` overlay |
| 5 | Applications | `ApplicationsPage.axaml` | per-app picker overlay |
| 6 | Network | `NetworkPage.axaml` | settings overlay |
| 7 | DPI Bypass | `DpiBypassPage.axaml` | settings → DPI mode picker |
| 8 | Telegram | `TelegramPage.axaml` | n/a (TgProxy Win-only) |
| 9 | Tools | `ToolsPage.axaml` | log overlay + diagnostic chips |

Plus: About window, Routing Profiles overlay (Android-only), Crash Log overlay.

---

## State coverage per page

For each page minimum:

| State | Description |
|---|---|
| Empty | First launch, no data |
| Populated | After typical user setup |
| Selected | Item picked/active |
| Loading | While fetching |
| Error | API/network failure simulated |
| Disconnected | VPN off |
| Connected | VPN on |
| Expanded | Drill-down/detail view |

Estimated: 9 pages × 8 states × 2 themes × 2 langs = **288 screenshot pairs** at
maximum. Practical first pass: 9 pages × 3 states × 1 theme × 1 lang =
**27 pairs** to start, expand as findings warrant.

---

## Working tree

```
parity-audit/
├── plan.md (this file in plans/)
├── desktop/
│   ├── page-01-simple/
│   │   ├── empty-light-en.png
│   │   ├── populated-light-en.png
│   │   └── ...
│   ├── page-02-subscribe/
│   └── ...
├── android/
│   ├── page-01-simple/
│   └── ...
├── diff/
│   ├── page-01-simple/
│   │   ├── empty-light-en-diff.png
│   │   └── empty-light-en-metric.txt
│   └── ...
├── catalog/
│   ├── desktop-elements.json (from M8 XAML parse)
│   ├── android-elements.json (from M8 regex parse)
│   ├── desktop-strings.txt (from M6 OCR)
│   ├── android-strings.txt (from M6 OCR)
│   ├── locale-keys-desktop.txt (from M7)
│   └── locale-keys-android.txt (from M7)
├── traces/
│   ├── desktop-click-trace.json (from M9)
│   ├── android-click-trace.json (from M10)
│   └── action-pair-diff.md (from M11)
└── findings.md (compiled report)
```

---

## Findings classification

Each divergence catalogued as:

| Severity | Definition | Example |
|---|---|---|
| **P0 critical** | Breaks core function on one platform | Connect button absent on Android |
| **P1 visible UX** | User-visible inconsistency, would surprise | Different toast wording for same event |
| **P2 polish** | Subtle visual / typographic | RU text wraps differently |
| **P3 platform-justified** | Different by design (n/a Win Service Telegram) | Documented in current-diff.md |

For each P0/P1: **must spawn fix chip**. P2: batched fix-pass. P3: confirmed in
parity-current-diff doc only.

---

## Re-test cycle

After each fix chip merges:

1. Rebuild desktop + Android
2. Re-run M1+M2 on the affected page
3. Verify diff is below threshold
4. If new diff appears elsewhere (regression) → flag for chip
5. Loop until convergence or P3 reclassification

Cycle target: 3 iterations max per page. After 3 iterations without
convergence, escalate (might be platform-justified P3).

---

## Tooling stack

| Stage | Tool |
|---|---|
| Desktop capture | `mcp__computer-use__screenshot` |
| Android capture | adb shell screencap → scp → Read |
| Pixel diff | ImageMagick `compare` |
| Perceptual diff | `dssim` (will install if not present) |
| OCR | `tesseract` (RU+EN) |
| XAML parse | Python `lxml` |
| Visualization | Python PIL composite for side-by-side |
| Reporting | Markdown tables in findings.md |
| Action timing | adb am start -W + computer-use timestamp |

---

## Known divergences from `vpnrouter-platform-current-diff.md`

These are **already documented** as platform-justified (P3) — not goals of
this audit:

- ETW / Firewall / WindowsDnsHardening (Win-only)
- TgProxy / Zapret-via-Cygwin (Win-only)
- Hosts manipulation (Win admin requirement)
- VpnService UID-based vs process_name (Android security model)
- Single-instance Mutex pattern (Win-only)

This audit catalogs everything **else** — all the things that COULD be
identical and AREN'T.

---

## Execution phases

1. **Phase 1 — Plan + tooling** (this file + computer-use access + ImageMagick)
2. **Phase 2 — Desktop capture sweep** (all 9 pages × 1 state)
3. **Phase 3 — Android capture sweep** (mirror)
4. **Phase 4 — Pixel + perceptual diff** (M2+M3)
5. **Phase 5 — Static analysis** (M7+M8)
6. **Phase 6 — OCR audit** (M6)
7. **Phase 7 — Click trace** (M9+M10+M11)
8. **Phase 8 — Compile findings.md** (categorized P0/P1/P2/P3)
9. **Phase 9 — Spawn fix chips** (one per P0+P1)
10. **Phase 10 — Re-test loop** (rebuild → re-capture → re-diff)

Estimated effort: **6-12 hours** for full sweep, depending on findings
density. First pass focuses on Phase 2-8 to catalog; fix-loop continues until
P0/P1 list empty.

---

**Last updated**: 2026-05-09
