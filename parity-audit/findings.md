# VPNRouter — UI/UX parity audit findings

**Date**: 2026-05-09 after v2.32.0 + AND-PROFILES merge.
**Scope**: 9 desktop pages × Android equivalent overlays.
**Methods used**: M1 (side-by-side), M6 (text content), M7 (locale keys),
M8 (UI element catalog) — partial M2 (pixel diff), M9-M11 deferred.

**Severity legend**: P0 critical · P1 visible UX · P2 polish · P3 platform-justified.

---

## 🔴 P0 — Critical (immediate ship blocker)

None found. Both platforms function for core "connect VPN" flow.

---

## 🟠 P1 — Visible UX (user-noticeable, fix before next stable)

### F-01 — Localization key sets almost completely diverge (9.3% overlap)

Static analysis of `VPNRouter.App/Localization/Strings.cs` vs
`VPNRouter.Android/Localization.cs`:

| Metric | Count |
|---|---|
| Desktop total keys | **540** |
| Android total keys | **303** |
| Keys ONLY on desktop | 490 |
| Keys ONLY on Android | 253 |
| **Keys shared** | **50 (9.3% overlap)** |

Naming conventions differ:
- Desktop: `AboutBrandName`, `ApplyChanges`, `AppsHint`, …
- Android: `BrandTitle`, `ButtonConnect`, `Cc*` (custom config), …

**Impact**:
- Adding a desktop string never automatically reaches Android
- Translation drift inevitable (~3 documented cases over pool 5+6+7)
- Maintenance cost is ~2x for any text change
- Even when both platforms surface the SAME concept, the stored key
  may differ (`AppsHint` desktop vs `BlockOnVpnFailHint` Android)

**Fix**: Phase H (AND-LOCALIZATION-MERGE chip — 2 spawn attempts didn't
complete). Effort 2-3 hours. Make `VPNRouter.Core/Localization/Strings.cs`
the single source-of-truth, both UIs consume it.

---

### F-02 — Simple page layout fundamentally differs (13 sub-divergences)

Side-by-side rendered + visually compared (`composite/simple.png`):

| # | Element | Desktop | Android |
|---|---|---|---|
| 1 | Header / brand title | absent | "Virtual Penguin Network" + mascot |
| 2 | Quick-toggle chips (VPN/Zapret/TG) | absent | present at top |
| 3 | Status card wording | "Traffic goes **straight**" | "Traffic goes **direct**" |
| 4 | Config·Mode default | "manual · split" | "subscription · all traffic" |
| 5 | Sub-tabs (Subscription/Server/Custom JSON) | absent | present |
| 6 | Action buttons (Save/QR/Refresh) | absent | present |
| 7 | Routing label | "Route through VPN" | "What goes via VPN" |
| 8 | Selected default | Selected apps | All traffic |
| 9 | Selected-apps subtext | "Based on your selected apps" | "By selected apps list (advanced settings)" |
| 10 | All-traffic subtext | "Includes games and banking" | "Including games and banks" |
| 11 | Autostart inline card | present | absent (in kebab→Settings) |
| 12 | Advanced settings card | present (summary list) | absent (use kebab) |
| 13 | Information density | sparse, big margins | dense, scrollable |

**Why critical**: this is the FIRST page user sees. Different wordings,
different default routing modes, different navigation pattern (kebab vs
inline cards) means the two platforms feel like **different products**.

**Fix strategy**:
- **Wording parity** (rows 3, 7, 9, 10): unify via shared Strings.cs (Phase H)
- **Default mode** (row 4): pick one. Suggest Android's default
  (subscription → all traffic) since most users start without explicit
  process selection
- **Layout** (rows 1, 2, 5, 6, 11, 12): structural — pick which platform
  is canonical and port the other
- **Recommendation**: keep desktop layout (advanced-settings card,
  inline autostart) but ADD Android's brand title + quick chips to
  desktop top rail. Ship parity in v2.33 or v2.34.

---

### F-03 — Different navigation models (inline vs kebab)

| Behavior | Desktop | Android |
|---|---|---|
| Settings access | Top-right gear icon → settings page | Kebab top-right → settings overlay |
| About page | Help menu → AboutWindow | Kebab → About item |
| Crash log | n/a (file in logs/ only) | Kebab → View crash log |
| Theme switch | Settings → Appearance | Kebab → Light/Dark buttons |
| Language switch | Settings → Language combo | Kebab → RU/EN buttons |
| Logs | Tools page → Open log button | Kebab → Open log |
| Free Configs | Top tab "Free Configs" page | Kebab → Find a server |
| Profiles | absent | Kebab → Routing profiles |

**Impact**: every user discovery path is different. A desktop user
familiar with the gear icon doesn't find theme switching on Android
without exploring kebab.

**Fix**: standardize on ONE pattern. Recommendation:
- Mobile-first paradigm: kebab (hamburger) for secondary actions
- Desktop has more screen real estate: surface common actions inline
- BUT: copy & icons should match between platforms for shared concepts

---

### F-04 — Default ConfigMode differs

- Desktop: `manual · split` (user must populate vless URL + select apps)
- Android: `subscription · all traffic` (user pastes URL → auto)

User installing both and trying same workflow gets confused: "why does
the Android version connect and Windows doesn't, with the same URL?"

**Fix**: pick one default. Recommend Android's (subscription·all) as it's
the lower-friction onboarding. Desktop should match in v2.33 onboarding.

---

### F-05 — Brand presentation absent on desktop simple page

Android shows large "Virtual Penguin Network" title + penguin mascot at
top of main scroller. Desktop has none — user just sees status + form.

**Fix**: port the brand heading to desktop SimplePage. The asset
(`penguin_mascot.png`) is already shared via `<AvaloniaResource Link=...>`
so this is pure layout addition.

---

## 🟡 P2 — Polish (batch fix in v2.33+)

### F-06 — Status card icon differs

Desktop: small dot (●). Android: same dot. Visually consistent, but the
animated states (connecting/connected) may render differently. **Need
captures in those states to confirm**.

### F-07 — Card border-radius / shadow

Desktop cards: subtle border + minimal shadow. Android cards: same border
+ slight drop shadow. Small visual inconsistency. Likely Avalonia FluentTheme
handling differently between desktop and mobile renderer.

### F-08 — Localization wording drift on shared strings (sample)

For the 50 keys that DO appear on both platforms, audit if values match.
Sampling shows divergence on:

- "All traffic" subtext: desktop vs Android (F-02 row 10)
- Status sub-text: "straight" vs "direct" (F-02 row 3)
- "Route through" vs "What goes via" (F-02 row 7)

Need full RU+EN value-by-value diff (deferred — file-level read of
both Strings.cs files for the 50 shared keys).

### F-09 — Quick-toggle chips on Android lack desktop equivalent

Android has VPN/Zapret/TG chips at top providing one-tap toggle. Desktop
has same toggles but in NetworkPage's right rail. Discoverability
differs significantly.

---

## 🔵 P3 — Platform-justified (already documented, not goal of audit)

Per `vpnrouter-platform-current-diff.md` §15:

- TgProxy and Zapret service-on-Win-only — Android uses tls_fragment outbound
- Process-name vs UID routing — fundamental Android security model
- File system paths
- Update mechanism (helper.cmd vs PackageInstaller)
- Single-instance / foreground promotion lifecycles

These are already accepted divergences, not issues to fix.

---

## Summary metrics

| Category | Count |
|---|---|
| P0 critical | 0 |
| P1 visible UX | 5 (F-01..F-05) |
| P2 polish | 4 (F-06..F-09) |
| P3 platform-justified | 10 (already documented) |

**Total fix-required findings**: 9.

**Estimated effort to close P1**: ~10-14 hours (most of it is Phase H
locale merge + desktop layout updates).

---

## Methods used in this audit

- ✅ M1 — Side-by-side composite (8 pages, ImageMagick `+append`)
- ✅ M6 — Text content review (eyeballed visible labels)
- ✅ M7 — Locale key static diff (50/540/303 stats)
- ✅ M8 — UI element comparison (manual)
- ⏸️ M2 — Pixel diff (deferred: layouts diverge structurally so pixel-diff
  is meaningless without first aligning structure)
- ⏸️ M3 — Perceptual diff (same reason)
- ⏸️ M4 — Color palette extraction (deferred, partial via eyeball)
- ⏸️ M5 — Layout grid heatmap (deferred)
- ⏸️ M6 OCR — tesseract not installed locally
- ⏸️ M9-M11 — click trace (partially done in earlier UI verify session)
- ⏸️ M12 — network capture
- ⏸️ M13 — theme/lang matrix (only Light EN captured; Dark + RU pending)
- ⏸️ M14 — animation timing

**First-pass productivity**: methods 1+7+8 alone surfaced 9 findings.
Pixel-diff (M2-M5) only useful AFTER structural alignment — it's 100%
divergent now because layouts differ.

---

## Captures inventory

```
parity-audit/
├── desktop/  (9 PNGs from PageScreenshotTests headless)
│   ├── page-simple.png
│   ├── page-subscribe.png
│   ├── page-servers.png
│   ├── page-network.png
│   ├── page-applications.png
│   ├── page-tools.png
│   ├── page-dpi-bypass.png
│   ├── page-telegram.png
│   └── page-free-configs.png
├── android/  (12 PNGs from live phone via Mac SSH + adb)
│   ├── page-simple.png
│   ├── page-kebab-menu.png
│   ├── page-subscribe.png
│   ├── page-servers.png
│   ├── page-custom-json.png
│   ├── page-applications-mode.png
│   ├── page-applications-picker.png
│   ├── page-network-settings.png
│   ├── page-dpi-bypass-settings.png
│   ├── page-free-configs.png
│   ├── page-profiles.png
│   ├── page-tools-log.png
│   ├── page-crash-log.png
│   └── page-settings-full.png
├── composite/  (8 side-by-side from ImageMagick)
└── catalog/
    ├── desktop-locale-keys.txt (540 entries)
    └── android-locale-keys.txt (303 entries)
```

---

## Next steps

1. **Spawn fix chips** for F-01 (locale merge) and F-02 (Simple layout
   parity) — these are the highest-leverage.
2. **Capture remaining state matrix** — Dark theme, RU language, error
   states, connected state.
3. **Re-run composite** after F-01 fix to verify locale parity.
4. **Re-run composite** after F-02 fix to verify Simple page parity.
5. **Repeat** until P1 list empty.
