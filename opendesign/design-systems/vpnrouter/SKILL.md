# VPNRouter — Chosen Art Direction

> **Status:** Decisive direction chosen.
> **Date:** 2026-09-06
> **Author:** Astra (art director review), grounded in observed repo assets.
> **Scope:** Mascot, logo, UI glyphs, palette, stroke rules, size simplification, acceptance rubric.

---

## 1. The Chosen Direction: "Inked Companion — Arctic Edition"

### Decision summary

Preserve and refine the **hand-drawn human-with-headphones holding a penguin** mascot.
Do not replace it with a generic penguin. The existing identity is distinctive, warm,
and immediately recognizable at every size it currently ships. The SVG penguin in
`design/project/assets/penguin.svg` and the gradient lockup in `logo-lockup.svg`
are design explorations that should remain design-only references — they must not
displace the hand-drawn mascot in production.

The direction is:

> **Clean up the hand-drawn lineart into a controlled-weight single-path SVG master,**
> **then derive every platform asset from it with deterministic stroke/fill rules**
> **per size tier, using the existing Arctic / Glacier token palette exclusively.**

---

## 2. Grounding: Observed Asset Audit

### 2.1 Mascot PNG variants (VPNRouter.App/Assets/)

| File | Dimensions | Color mode | Role observed |
|---|---|---|---|
| `penguin_mascot.png` | 640×640 | 8-bit colormap, transparent bg | **Primary.** Black lineart on alpha. Used for light-theme header (28 px display via `LogoSource`) and `.ico` source. |
| `penguin_mascot_tile.png` | 640×640 | 8-bit RGBA | Linux window icon (`MainWindow.axaml.cs:45`). Tile variant with solid dark background — Linux WMs display this better than transparent lineart in taskbars. |
| `penguin_mascot_white.png` | 640×640 | 8-bit RGBA | White lineart on dark background. Used for dark-theme tray icon on Linux/macOS (`GetTrayIconUri`). |
| `penguin_logo.png` | 640×640 | 8-bit RGB, **no alpha** | About dialog mascot (`AboutWindow.axaml.cs:28`). Older full-color variant — note: no alpha channel, so it renders with a solid white background rather than transparency. |
| `b_icon.png` (root) | 552×712 | RGBA, 673 KB | Full-color photo/render of the white-on-dark mascot. **Not used in production**; reference source material only. |

### 2.2 ICO files (VPNRouter.App/Assets/)

| File | Sizes embedded | Usage |
|---|---|---|
| `penguin_mascot.ico` | 7 sizes (16–256) | `ApplicationIcon` in `.csproj`; Windows tray icon (non-dark). |
| `penguin_mascot_white.ico` | 7 sizes (16–256) | Linux tray; macOS dark-theme tray. |
| `penguin_mascot_tile.ico` | 7 sizes (16–256) | Not referenced in runtime code. The `.png` source variant IS used for Linux window icon, but this `.ico` derivative is not loaded. Candidate for removal. |
| `penguin_logo.ico` | 1 size (32×32) | Not referenced in runtime code — superseded. |
| `avalonia-logo.ico` | — | Avalonia default, not referenced — dead asset. |

### 2.3 Android launcher (VPNRouter.Android/Resources/mipmap-*/)

| Density bucket | Size | Color mode |
|---|---|---|
| mdpi | 48×48 | 8-bit grayscale |
| hdpi | 72×72 | 8-bit grayscale |
| xhdpi | 96×96 | 8-bit grayscale |
| xxhdpi | 144×144 | 8-bit grayscale |
| xxxhdpi | 192×192 | 8-bit grayscale |

**Observation:** All grayscale. The Android launcher shows the mascot inside a white
circular adaptive-icon frame — visible in `r5-02-launcher-search.png` where the icon
reads clearly at ~48 dp on the dark launcher. However, the grayscale encoding means
the lineart has no antialiasing against colored backgrounds — it relies on the
adaptive-icon circle to mask this.

### 2.4 Design SVGs (design/project/assets/)

| File | Content | Issue |
|---|---|---|
| `penguin.svg` | Solo generic penguin on radial-gradient ice circle. Uses `linearGradient` body + `radialGradient` background. | **Violates constraints**: gradient-heavy, drops the human+headphones identity entirely, replaces it with a generic penguin. This SVG must remain a reference exploration only. |
| `logo-lockup.svg` | Rounded-rect icon + "VPNRouter" + "VIRTUAL · PENGUIN · NETWORK" subtitle. Icon is a gradient-filled rectangle with a simplified penguin face. | **Violates constraints**: uses `linearGradient` fill on the icon container. The lockup text layout and subtitle are usable references for typography. |

### 2.5 In-app usage (runtime code)

- **Desktop header:** `MainWindow.axaml` line 388–398 → 28×28 px container, 26×26 image, `RadiusSm` (6 px) corners, `AccentBgSubtle` background.
- **Theme switching:** `ThemeAndLogo.cs` → loads `penguin_mascot.png`, runtime RGB-inverts for dark theme (black lineart → white lineart, alpha preserved).
- **Tray icon:** `App.axaml.cs` line 294–302 → `penguin_mascot.ico` (normal) or `penguin_mascot_white.ico` (Linux/macOS dark).
- **Android header:** Same mascot, ~40 dp in header bar with `AccentBgSubtle` container.

### 2.6 UI glyph patterns (XAML Path icons)

- **DPI Bypass hero:** Shield-with-lightning SVG path, 20×20 in a 36×36 bordered container (`RadiusLg` 10, `AccentBorder` 1 px, `AccentFg` stroke 1.8 px, round caps/joins).
- **Telegram hero:** Paper-plane SVG path, same 36×36 container, same stroke rules.
- **Pattern:** Stroke-only icons, `AccentFgBrush`, `StrokeThickness="1.8"`, `StrokeLineCap="Round"`, `StrokeJoin="Round"`, inside bordered containers with `SurfaceBase` fill + `AccentBorder` + `BoxShadow`.

### 2.7 Token palette (Tokens.axaml / tokens.css)

Named "Arctic / Glacier" — cool-slate neutrals + cyan-sky accent ramp.

**Signature colors extracted from tokens:**

| Token | Light | Dark | Hex |
|---|---|---|---|
| AccentSolid (brand primary) | `#0EA5E9` | `#38BDF8` | sky-500 / sky-400 |
| AccentFg (icon stroke) | `#0369A1` | `#67E8F9` | sky-700 / cyan-300 |
| AccentBgSubtle (container bg) | `#ECFEFF` | `rgba(56,189,248,0.08)` | cyan-50 / sky-400@8% |
| TextPrimary | `#0F1320` | `#EEF1F6` | slate-900 / slate-50 |
| SurfaceApp | `#F5F7FA` | `#070A14` | slate-50 / slate-950 |
| WarningSolid (beak accent) | `#D97706` / `#F59E0B` | amber-600 / amber-500 |

---

## 3. Construction Rules

### 3.1 Mascot Master — SVG Redraw Specification

**Source:** Trace from `penguin_mascot.png` (the 640×640 transparent-bg lineart).

**Canvas:** 64×64 viewBox (power-of-two friendly, maps cleanly to 16/24/32/48/64/128/192/256).

**Geometry rules:**
- Single compound `<path>` for the entire human+penguin figure.
- Uniform stroke weight: **3 px** at 64×64 (scales to 0.75 px at 16×16, 1.5 px at 32×32).
- `stroke-linecap="round"` and `stroke-linejoin="round"` everywhere — matches existing UI glyph pattern.
- No fills in the master path — stroke only. Fills are applied per-variant (see §3.3).
- Anchor the composition so the combined figure sits within a 56×56 centered safe area (4 px margin on each side) — this ensures Android adaptive-icon and Windows tile safe zones are respected.

**Anatomy (preserve these recognition features):**
1. Human head: roughly circular, slightly larger than center.
2. Bangs/fringe: characteristic short horizontal strokes across forehead.
3. Headphones: two filled circles at ear positions connected by arc over head.
4. Smile: single curved stroke.
5. Penguin: held at lower-left, head tilted up, open beak, round eyes with catch-lights.
6. Human's visible arm curving around penguin.

### 3.2 Logo Lockup

**Composition:** `[mascot icon] [text block]`, horizontal, vertically centered.

**Text block:**
- Line 1: "Virtual Penguin Network" — `FwBold` (700), `FsMd` (12 px in Avalonia context) or equivalent.
- Line 2 (optional, formal contexts only): "VIRTUAL · PENGUIN · NETWORK" — `FwMedium` (500), `tracking-caps` (0.06em), `TextSecondary` color.
- Font: system stack per tokens (`-apple-system, Segoe UI Variable, system-ui, …`).

**Icon in lockup:** 28×28 px at 1× (matches current header usage). Container: `RadiusSm` (6 px) corners, `AccentBgSubtle` fill, no border at this size.

### 3.3 Stroke / Fill Rules by Context

| Context | Stroke color | Fill | Background |
|---|---|---|---|
| Light-theme header (28 px) | `TextPrimary` (#0F1320) | None (stroke only) | `AccentBgSubtle` container |
| Dark-theme header (28 px) | `TextPrimary` (#EEF1F6) — via runtime RGB-invert | None | `AccentBgSubtle` container |
| Windows taskbar icon (16 px) | Filled silhouette, `TextPrimary` | Solid fill, no stroke | Transparent |
| Windows tray icon (16–24 px) | Same as taskbar | Solid fill | Transparent |
| Linux/macOS tray (dark bg) | Filled silhouette, white (#EEF1F6) | Solid fill | Transparent |
| Android launcher (48–192 px) | Stroke, `TextPrimary` | Headphone cups + penguin body filled | White adaptive-icon circle |
| Splash / About dialog (≥64 px) | Full-detail stroke, `TextPrimary` or inverted | Optional penguin body fill | `AccentBgSubtle` or none |

### 3.4 UI Glyph Rules (Non-Mascot Icons)

All page-hero icons and toolbar icons follow the established DpiBypass/Telegram pattern:

| Property | Value | Source |
|---|---|---|
| Stroke weight | 1.8 px (at 20×20 render size) | DpiBypassPage.axaml line 201 |
| Stroke color | `AccentFgBrush` | DpiBypassPage.axaml line 200 |
| Line caps | Round | Line 202 |
| Line joins | Round | Line 203 |
| Container | 36×36, `RadiusLg` (10 px), `SurfaceBase` bg, `AccentBorder` 1 px, `BoxShadow 0 2 6 0 #14000000` | Lines 189–196 |
| Icon render size | 20×20, `Stretch="Uniform"` | Line 198 |
| Fill | None (stroke-only). Exception: compound shapes like shield body use stroke for outline + internal detail. | Observed pattern |

**Rule:** Never introduce filled/solid glyph icons. The entire icon language is stroke-only with round terminals. New icons must follow this exactly.

---

## 4. Palette — Canonical Brand Colors

These are the **only** colors permitted in mascot/logo/icon work. Every value comes from `Tokens.axaml` / `tokens.css`.

### 4.1 Primary brand

| Name | Hex | Usage |
|---|---|---|
| Arctic-400 | `#38BDF8` | Brand accent. Borders, focus rings, interactive highlights. |
| Arctic-500 | `#0EA5E9` | Primary action fills (buttons). Light-theme `AccentSolid`. |
| Arctic-700 | `#0369A1` | Light-theme icon strokes (`AccentFg`). Darkest accent for text. |
| Cyan-300 | `#67E8F9` | Dark-theme icon strokes (`AccentFg`). Bright accent on dark. |

### 4.2 Neutrals

| Name | Hex | Usage |
|---|---|---|
| Slate-900 | `#0F1320` | Light-theme lineart, `TextPrimary`. |
| Slate-950 | `#070A14` | Dark-theme app background. |
| Slate-50 | `#F5F7FA` | Light-theme app background. |
| White | `#FFFFFF` | Light-theme card surfaces. |
| Near-white | `#EEF1F6` | Dark-theme `TextPrimary`, inverted lineart color. |

### 4.3 Accent (beak, warm detail)

| Name | Hex | Usage |
|---|---|---|
| Amber-500 | `#F59E0B` | Penguin beak in SVG mascot (both `penguin.svg` and `logo-lockup.svg` use this). |
| Amber-600 | `#D97706` | Light-theme warning solid. |

**Rule:** The beak is the **only** element permitted to use amber/warm color. It provides
the single warm accent that makes the mascot pop against the cool Arctic palette.
Never introduce additional warm colors.

### 4.4 Forbidden

- No gradients (linear or radial) in production assets. The `penguin.svg` and
  `logo-lockup.svg` gradients are exploration artifacts.
- No drop shadows on the mascot itself (shadows belong on containers per token system).
- No colors outside the token palette. No trendy neons, no purple, no orange beyond
  the beak amber.

---

## 5. Simplification Strategy by Size

### Tier A: 16 px (Windows tray, macOS menu bar, smallest ICO frame)

**Problem observed:** At 16×16 the current `penguin_mascot.ico` renders the full
lineart composition at a size where stroke detail turns to noise. The human's bangs,
the penguin's catch-lights, and the arm curve all merge into an indistinct blob.

**Rule — "Filled Silhouette":**
- Convert the master SVG to a **filled shape** (no strokes). The figure becomes a
  solid dark silhouette on transparent background.
- Simplify: remove bangs detail, merge penguin body with human arm into one connected
  shape. Keep headphone cups as the strongest recognition anchor (two filled circles).
- The penguin's beak remains the single amber accent dot at 16 px — a 2×2 px amber
  square is enough to register.
- **Test:** The icon must be identifiable as "person with headphones + small companion"
  in a Windows 11 system tray next to other 16×16 icons.

### Tier B: 24 px (Windows 11 tray @150% DPI, status bar)

**Rule — "Heavy Stroke":**
- Use the master SVG with stroke weight increased to 4 px (at 64 viewBox → 1.5 px
  rendered). Headphone cups are filled.
- Bangs simplified to 2 strokes instead of full detail.
- Penguin beak: amber filled triangle.

### Tier C: 28–32 px (Desktop header, Android header, ICO 32)

**Rule — "Standard Stroke" (current production appearance):**
- Master SVG at native 3 px stroke (renders ~1.3–1.5 px). Full detail.
- This is what `MainWindow.axaml` currently displays — the existing `penguin_mascot.png`
  at 26×26 in a 28×28 container. The current appearance is correct and should be
  preserved exactly.

### Tier D: 48–96 px (Android launcher, ICO 48+, About dialog)

**Rule — "Detailed Stroke + Selective Fill":**
- Full master SVG detail.
- Headphone cups: filled with `TextPrimary`.
- Penguin belly: optionally filled with `AccentBgSubtle` or `SurfaceBase` (a subtle
  cool tint to differentiate from background).
- Beak: filled amber triangle.
- Human's smile and eyes remain stroke-only.

### Tier E: 128–256 px (Store listing, marketing, splash)

**Rule — "Full Illustration":**
- Full master SVG at maximum detail.
- All fills active (headphone cups, penguin body, beak).
- Container: 8 px rounded-rect with `AccentBgSubtle` fill, `AccentBorder` 1 px,
  `ShadowMd`.
- This is the only tier where the beak amber accent is allowed to be a fully
  rendered triangle rather than a simplified shape.

---

## 6. Platform Safety Zone Compliance

### Windows

- **Taskbar icon (ICO):** 16×16 and 32×32 frames. Content must fit within a 14×14 /
  28×28 safe area (1 px padding on each edge). The current `.ico` files
  use the full 16×16 canvas — the filled-silhouette tier naturally handles this.
- **Tile:** Not currently used. If needed, 44×44 content in a 48×48 frame.

### macOS

- **Menu bar (tray):** 22×22 @1× / 44×44 @2×. Template image (monochrome,
  alpha-only). The `penguin_mascot_white.ico` approach is correct — white-on-
  transparent. Must respect 18×18 @1× safe area.

### Android

- **Adaptive icon:** 108×108 dp total (72 dp visible), but the current implementation
  uses legacy `ic_launcher.png` in mipmap buckets with a white circular frame applied
  by the Android launcher. Content must stay within the inner 66 dp (66%) safe zone.
- The current grayscale launcher icons already sit within this zone (visible in
  `r5-02-launcher-search.png`).

### Linux

- **Tray:** 22×22 or 24×24 depending on DE. Same white-on-transparent approach
  as macOS. `penguin_mascot_white.ico` is already used for this.

---

## 7. Acceptance Rubric

A new mascot asset set is accepted when **every** criterion passes:

| # | Criterion | How to verify |
|---|---|---|
| 1 | **Identity preserved.** The image clearly depicts a human wearing headphones holding/standing with a small penguin. Not a solo penguin, not a generic icon. | Visual comparison with `penguin_mascot.png` — same character, same composition. |
| 2 | **Hand-drawn character.** The lineart has the warm, slightly imperfect quality of the original. Not a corporate-clean vector illustration. Controlled imperfection, not slop. | Side-by-side with `penguin_mascot.png`. Stroke weight is uniform but line paths may have subtle hand-drawn wobble. |
| 3 | **Reads at 16 px.** The filled-silhouette tier is recognizable as "person with headphones + companion" in a Windows 11 system tray screenshot. | Render ICO 16×16 frame, place in a mock tray bar, confirm headphone cups and companion shape are distinct. |
| 4 | **Reads at 24 px.** The heavy-stroke tier shows identifiable headphones, face, and penguin as three distinct elements. | Render at 24×24, compare. |
| 5 | **28 px parity.** The standard tier, displayed in a 28×28 container with `AccentBgSubtle` background and `RadiusSm` corners, matches current production appearance in `MainWindow.axaml`. | Screenshot comparison with current desktop app header. |
| 6 | **Dark theme inversion works.** RGB-inverting the black-lineart version produces clean white lineart with no fringing, halo, or lost detail. | Apply the existing `TryBuildInvertedLogo` algorithm or equivalent; compare with current `_logoDark` rendering on dark theme. |
| 7 | **No gradients.** No `linearGradient`, `radialGradient`, or CSS gradient in any production asset. | grep the SVG/CSS source files. |
| 8 | **Palette compliance.** Every color used in the asset maps to an existing token from `Tokens.axaml`. The only warm color is amber for the beak. | Hex comparison against §4 palette table. |
| 9 | **Single SVG master.** All sizes derive from one `mascot-master.svg` (64×64 viewBox) via stroke/fill rules, not from separate hand-edited files per size. | File count; visual consistency across tiers. |
| 10 | **Android safe zone.** The mascot content sits within the inner 66% of the adaptive-icon canvas. No content is clipped by circular, squircle, or rounded-rect masks. | Overlay safe-zone template on rendered `ic_launcher.png`. |
| 11 | **UI glyph consistency.** Any new page-hero icons use stroke-only, 1.8 px weight at 20×20, round caps/joins, `AccentFgBrush` color, inside the standard 36×36 bordered container. | Pattern match against DpiBypassPage / TelegramPage hero icon implementation. |
| 12 | **No dead assets.** Unused files (`avalonia-logo.ico`, `penguin_logo.ico`, `penguin_mascot_tile.ico`) are removed or explicitly documented as archived. Active assets (`penguin_logo.png` in AboutWindow, `penguin_mascot_tile.png` on Linux) are preserved or replaced. | grep for `avares://` references; confirm runtime code loads or does not load each file. |

---

## 8. Asset Status Inventory (Verified Against Runtime Code)

### Active — must be preserved or replaced 1:1

| File | Runtime reference | Role |
|---|---|---|
| `penguin_mascot.png` | `ThemeAndLogo.cs:40`, Android `AndroidApp.axaml.cs:1779` | Primary mascot for light-theme header (desktop + Android). Source for runtime RGB-invert (dark theme). |
| `penguin_mascot_white.png` | `App.axaml.cs:300` (via `.ico` derivative) | Dark-theme tray icon source for Linux/macOS. |
| `penguin_mascot.ico` | `VPNRouter.App.csproj:23` (ApplicationIcon), `App.axaml.cs:301` | Windows app icon + Windows tray icon. |
| `penguin_mascot_white.ico` | `App.axaml.cs:300` | Linux tray icon; macOS dark-theme tray icon. |
| `penguin_logo.png` | `AboutWindow.axaml.cs:28` | About dialog mascot image. |
| `penguin_mascot_tile.png` | `MainWindow.axaml.cs:45` | Linux window icon (tile variant with solid bg — Linux WMs need this for proper taskbar display). |

### Dead — confirmed no runtime references, safe to remove

| File | Evidence |
|---|---|
| `VPNRouter.App/Assets/avalonia-logo.ico` | No grep hits in any `.cs`, `.axaml`, or `.csproj` file. Avalonia scaffold remnant. |
| `VPNRouter.App/Assets/penguin_logo.ico` | Only mentioned in a `.csproj` comment (line 17) documenting its supersession. Not in any `avares://` URI. Single 32×32 frame — superseded by `penguin_mascot.ico`. |
| `VPNRouter.App/Assets/penguin_mascot_tile.ico` | No runtime references found. The `.png` variant is used for Linux window icon, but the `.ico` derivative is not loaded anywhere. |

---

## 9. Summary of What NOT to Do

1. **Do not replace the human+penguin with a solo penguin.** The `penguin.svg` exploration
   drops the human entirely — it loses the distinctive "person with headphones" identity
   that makes VPNRouter recognizable.

2. **Do not introduce gradients.** The `logo-lockup.svg` and `penguin.svg` both use
   gradients. Production assets must be flat: solid strokes, solid fills, token colors.

3. **Do not create a "clean vector" redraw** that loses the hand-drawn quality. The
   charm of the mascot is its sketchy warmth. Corporate-clean Bézier curves would
   make it look like a stock icon.

4. **Do not add new warm colors.** Amber is reserved for the beak. The entire UI and
   brand language is cool-toned Arctic. Adding orange, pink, or red to the mascot
   would clash with the palette.

5. **Do not create size-specific hand-edited variants.** One SVG master, multiple
   renderings via stroke/fill rules. Divergent hand-edited files drift over time.

---

*This document is the single reference for all mascot/logo/icon decisions in VPNRouter.
It supersedes any conflicting guidance in design exploration files. Implementation
should begin with the SVG master redraw (§3.1), then derive platform assets per the
simplification tiers (§5), then clean up dead assets (§8).*
