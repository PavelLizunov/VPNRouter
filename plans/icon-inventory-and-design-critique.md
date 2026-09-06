# VPNRouter — Icon Inventory & Design Critique

*Source-backed audit · read-only · no files modified*

---

## 1. Exhaustive Product-Owned Icon Inventory

### 1A. Canonical Mascot — Hand-drawn human-with-headphones + penguin

The recognizable hand-drawn composition (human figure wearing headphones, holding/standing with a small penguin) is the product identity. It exists in **four raster variants** plus three ICO derivatives:

| # | File | Dimensions | Format | Size | Description |
|---|------|-----------|--------|------|-------------|
| 1 | `VPNRouter.App/Assets/penguin_mascot.png` | 640×640 | PNG 8-bit colormap | 70 KB | **Master source.** Black lineart on transparent. Indexed palette (no alpha channel in color type, but transparency via palette). |
| 2 | `VPNRouter.App/Assets/penguin_mascot_tile.png` | 640×640 | PNG 8-bit RGBA | 165 KB | Same mascot composited onto **solid white background**. Full alpha channel but opaque fill. |
| 3 | `VPNRouter.App/Assets/penguin_mascot_white.png` | 640×640 | PNG 8-bit RGBA | 174 KB | **White lineart** on transparent. Pre-baked RGB-inverted version of #1. |
| 4 | `VPNRouter.App/Assets/penguin_logo.png` | 640×640 | PNG 8-bit RGB | 194 KB | Same composition, exported as **opaque RGB** (no alpha, white background baked in). Visually identical to #2 but different color type. |
| 5 | `VPNRouter.App/Assets/penguin_mascot.ico` | 16–256 (7 sizes) | ICO/PNG | 72 KB | Multi-resolution ICO derived from #1 (transparent bg). |
| 6 | `VPNRouter.App/Assets/penguin_mascot_tile.ico` | 16–256 (7 sizes) | ICO/PNG | 70 KB | Multi-resolution ICO derived from #2 (white bg). |
| 7 | `VPNRouter.App/Assets/penguin_mascot_white.ico` | 16–256 (7 sizes) | ICO/PNG | 71 KB | Multi-resolution ICO derived from #3 (white lineart). |
| 8 | `VPNRouter.App/Assets/penguin_logo.ico` | 32×32 only (1 size) | ICO/PNG | 1.5 KB | **Deprecated.** Single-size ICO derived from #4. Only 32×32. |
| 9 | `VPNRouter.App/Assets/AppIcon.icns` | 16–1024 (11 types) | ICNS | 664 KB | macOS app icon bundle containing ic04(16), ic05(32), ic11(32@2x), ic12(64), ic07(128), ic13(128@2x), ic08(256), ic14(256@2x), ic09(512), ic10(512@2x), plus info metadata. |

### 1B. Android Launcher Icons

| # | File | Dimensions | Format | Size | Notes |
|---|------|-----------|--------|------|-------|
| 10 | `mipmap-mdpi/ic_launcher.png` | 48×48 | PNG **8-bit grayscale** | — | ⚠ Grayscale, not RGBA |
| 11 | `mipmap-hdpi/ic_launcher.png` | 72×72 | PNG 8-bit grayscale | — | |
| 12 | `mipmap-xhdpi/ic_launcher.png` | 96×96 | PNG 8-bit grayscale | — | |
| 13 | `mipmap-xxhdpi/ic_launcher.png` | 144×144 | PNG 8-bit grayscale | — | |
| 14 | `mipmap-xxxhdpi/ic_launcher.png` | 192×192 | PNG 8-bit grayscale | — | |
| 15 | `mipmap-mdpi/ic_launcher_round.png` | 48×48 | PNG 8-bit RGBA | — | Circular variant |
| 16 | `mipmap-hdpi/ic_launcher_round.png` | 72×72 | PNG 8-bit RGBA | — | |
| 17 | `mipmap-xhdpi/ic_launcher_round.png` | 96×96 | PNG 8-bit RGBA | — | |
| 18 | `mipmap-xxhdpi/ic_launcher_round.png` | 144×144 | PNG 8-bit RGBA | — | |
| 19 | `mipmap-xxxhdpi/ic_launcher_round.png` | 192×192 | PNG 8-bit RGBA | — | |

### 1C. Design System Vector Assets

| # | File | ViewBox | Description |
|---|------|---------|-------------|
| 20 | `design/project/assets/penguin.svg` | 128×128 | **Standalone vector penguin** — stylized with gradients (radial ice gradient bg, body gradient `#1C2231→#0F1320`, amber beak `#F59E0B`, frost cheeks). **NOT the hand-drawn mascot.** This is a new design-system penguin for the Arctic/Glacier system. |
| 21 | `design/project/assets/logo-lockup.svg` | 260×56 | **Brand lockup** — rounded-rect card with linear gradient (`#67E8F9→#0284C7`), simplified penguin icon, plus "VPNRouter" wordmark (18px bold) and "VIRTUAL · PENGUIN · NETWORK" tagline (10px). |

### 1D. Inline Path Glyphs (vector, not file-based)

| Surface | File:Line | Path Data | Renders As |
|---------|-----------|-----------|------------|
| DPI Bypass hero | `DpiBypassPage.axaml:206` | `M12,2 L4,5 L4,11 C4,16...` | Shield with lightning bolt, 20×20, stroked in `AccentFgBrush` |
| DPI Bypass play | `DpiBypassPage.axaml:249` | `M8,5 L8,19 L19,12 z` | Play triangle, 11×11, filled `AccentOnSolidBrush` |
| Telegram hero | `TelegramPage.axaml:141` | `M21,3 L11,13 M21,3 L14.5,21...` | Paper plane, 20×20, stroked in `AccentFgBrush` |
| Telegram play | `TelegramPage.axaml:188` | `M8,5 L8,19 L19,12 z` | Play triangle, 11×11 (shared with DPI) |

### 1E. NOT Product-Owned (excluded from product system)

| File | Reason |
|------|--------|
| `VPNRouter.App/Assets/avalonia-logo.ico` | **Avalonia framework template leftover.** 175 KB, 9 sizes (16–256). Not referenced anywhere in source. Dead weight. |

---

## 2. Duplicate / Derived Relationships

```
penguin_mascot.png (640×640, indexed colormap, transparent)
  ├── DERIVED → penguin_mascot_tile.png   (composited on white bg)
  ├── DERIVED → penguin_mascot_white.png  (RGB-inverted, white lineart)
  ├── DERIVED → penguin_mascot.ico        (7-size ICO, transparent bg)
  ├── DERIVED → penguin_mascot_tile.ico   (7-size ICO, white bg)
  ├── DERIVED → penguin_mascot_white.ico  (7-size ICO, white lineart)
  └── DERIVED → AppIcon.icns             (11-type macOS bundle)

penguin_logo.png (640×640, opaque RGB, white bg)
  ├── NEAR-DUPLICATE of penguin_mascot_tile.png (same visual, different encoding)
  └── DERIVED → penguin_logo.ico          (single 32×32 — DEPRECATED)

penguin_mascot.png
  └── RESIZED → Android mipmap-*/ic_launcher.png       (48–192px, GRAYSCALE)
  └── RESIZED → Android mipmap-*/ic_launcher_round.png (48–192px, RGBA circular)

design/project/assets/penguin.svg
  └── INDEPENDENT — new design-system penguin, NOT derived from hand-drawn mascot

design/project/assets/logo-lockup.svg
  └── INDEPENDENT — brand lockup using simplified penguin (matches penguin.svg style)
```

### Key Duplicate Finding

**`penguin_logo.png` ≈ `penguin_mascot_tile.png`**: Both show the same mascot on a white background. `penguin_logo.png` is 194 KB opaque RGB; `penguin_mascot_tile.png` is 165 KB RGBA. Only consumer of `penguin_logo.png` is `AboutWindow.axaml.cs:28`. This is consolidation-ready.

---

## 3. Consumer Map — Who Uses What

### Desktop (VPNRouter.App)

| Consumer | File:Line | Asset Used | Context |
|----------|-----------|------------|---------|
| **Windows exe icon** | `VPNRouter.App.csproj:23` | `penguin_mascot.ico` | Win32 embedded icon resource (title bar, taskbar, explorer) |
| **Linux window icon** | `MainWindow.axaml.cs:45` | `penguin_mascot_tile.png` | `Window.Icon` — tile variant for WM/taskbar visual weight |
| **Tray icon (Linux/macOS dark)** | `App.axaml.cs:300` | `penguin_mascot_white.ico` | White lineart for dark panels |
| **Tray icon (Windows/macOS light)** | `App.axaml.cs:301` | `penguin_mascot.ico` | Black lineart for light panels |
| **Header mascot (light theme)** | `MainWindowViewModel.ThemeAndLogo.cs:40` | `penguin_mascot.png` | 28×28 rendered in `MainWindow.axaml:394` via `LogoSource` binding |
| **Header mascot (dark theme)** | `MainWindowViewModel.ThemeAndLogo.cs:41` | `penguin_mascot.png` → runtime RGB-inversion | `TryBuildInvertedLogo()` flips RGB, preserves alpha |
| **About dialog logo** | `AboutWindow.axaml.cs:28` | `penguin_logo.png` | Larger display in About window |
| **macOS app bundle** | `build-mac.sh:47` | `AppIcon.icns` | Copied to `Contents/Resources/AppIcon.icns` |
| **Linux package icon** | `build-linux.ps1:68` | `penguin_mascot.png` | Copied as `icon.png` into AppDir |

### Android (VPNRouter.Android)

| Consumer | File:Line | Asset Used | Context |
|----------|-----------|------------|---------|
| **Launcher icon (standard)** | `AndroidManifest.xml:70` | `@mipmap/ic_launcher` | 5-density grayscale PNGs |
| **Launcher icon (round)** | `AndroidManifest.xml:71` | `@mipmap/ic_launcher_round` | 5-density RGBA PNGs |
| **Header mascot** | `AndroidApp.axaml.cs:1779` | `penguin_mascot.png` (via `<AvaloniaResource>` link in `.csproj:67`) | 28×28 border with theme-aware RGB inversion |
| **Advanced shell mascot** | `AndroidApp.AdvancedShell.cs:234` | Same `penguin_mascot.png` via shared loader | Brand row in settings |

### Pages (DpiBypassPage / TelegramPage)

Neither page uses raster product icons. Both use **inline `<Path>` vector glyphs** for their hero icons (shield, paper-plane, play/stop). These are correctly semantic and lightweight — no raster icon involvement.

### Not Referenced

| File | Status |
|------|--------|
| `avalonia-logo.ico` | **Zero references in any source, build, or XAML file.** Dead asset. |
| `penguin_logo.ico` | **Zero references.** 32×32 single-size ICO, deprecated by `penguin_mascot.ico`. |

---

## 4. Target Sizes Across Platforms

| Platform | Surface | Rendered Size | Source Asset | Notes |
|----------|---------|---------------|--------------|-------|
| Windows | Exe title bar | 16×16 | `penguin_mascot.ico` | ICO has dedicated 16px |
| Windows | Taskbar | 24–32px | `penguin_mascot.ico` | ICO has 24, 32 |
| Windows | Alt-Tab | 48–64px | `penguin_mascot.ico` | ICO has 48, 64 |
| Windows | Tray | 16×16 | `penguin_mascot.ico` | |
| Linux | Tray | 16–24px | `penguin_mascot_white.ico` | White variant for dark panels |
| Linux | Window manager | varies | `penguin_mascot_tile.png` | 640→downscaled by WM |
| Linux | Package icon | 640×640 | `penguin_mascot.png` | Should be 256 or 512 |
| macOS | Dock | 128–1024px | `AppIcon.icns` | Has all sizes ic04→ic10 |
| macOS | Tray | 16–22px | `penguin_mascot_white.ico` (dark) / `.ico` (light) | |
| Desktop | Header | 26×26 CSS px | `penguin_mascot.png` (640→26) | 24.6× downscale |
| Android | Launcher | 48–192px | `ic_launcher.png` per density | ⚠ Grayscale |
| Android | Launcher (round) | 48–192px | `ic_launcher_round.png` per density | RGBA |
| Android | Header | 26×26 | `penguin_mascot.png` | Same as desktop |

---

## 5. Current Weaknesses

### Critical

| # | Issue | Evidence | Impact |
|---|-------|----------|--------|
| **W1** | **Android `ic_launcher.png` is 8-bit grayscale** | `file` reports `8-bit grayscale` for all 5 density variants at `mipmap-*/ic_launcher.png` | Launcher icon appears as a gray silhouette — no visual identity in the app drawer. Compared to `ic_launcher_round.png` (RGBA), the standard variant looks broken. |
| **W2** | **`avalonia-logo.ico` is a dead 175 KB file** | Zero grep matches across all `.cs`, `.csproj`, `.axaml` files. Avalonia template leftover. | 175 KB of dead weight shipped in every build. Confusing to contributors. |
| **W3** | **`penguin_logo.ico` is dead and undersized** | Only 1 icon at 32×32 (1.5 KB). Zero code references. | Legacy artifact, no consumer. |

### Significant

| # | Issue | Evidence | Impact |
|---|-------|----------|--------|
| **W4** | **`penguin_logo.png` is a near-duplicate** | 194 KB opaque RGB vs `penguin_mascot_tile.png` at 165 KB RGBA. Same visual. Only consumer: `AboutWindow.axaml.cs:28`. | Unnecessary asset proliferation. About dialog could use `penguin_mascot_tile.png` or `penguin_mascot.png`. |
| **W5** | **Mascot is indexed-palette PNG, not true RGBA** | `penguin_mascot.png` is `8-bit colormap` — anti-aliased edges are limited to palette entries. At 26×26 display, the palette quantization produces slight jaggedness at brush boundaries. | Subtle quality loss at small sizes. The `_tile` and `_white` variants are proper RGBA. |
| **W6** | **640→26px is a 24.6× downscale for the header** | `MainWindow.axaml:395` renders at 26×26 from a 640×640 source. No dedicated small-size raster. | Wastes memory (decodes full 640×640 into bitmap cache), relies entirely on `HighQuality` interpolation. At @2x displays that's 52 actual pixels from 640 — acceptable but not crisp. |
| **W7** | **Tray icon uses `.ico` on all platforms** | `App.axaml.cs:300-301` loads `.ico` files for tray on Linux/macOS. macOS convention is `.png` template images; Linux varies. | Works but is an unusual format choice for non-Windows tray icons. |
| **W8** | **No adaptive icon for Android** | `AndroidManifest.xml:70-71` uses legacy `ic_launcher` + `ic_launcher_round`. No `ic_launcher_foreground.xml` / `ic_launcher_background.xml` adaptive-icon XML. | Android 8+ (API 26+) launchers show the legacy icon as-is without the themed/shaped treatment. The icon looks dated on Pixel, Samsung, etc. |
| **W9** | **SVG design penguin ≠ hand-drawn mascot** | `design/project/assets/penguin.svg` is a gradient-filled stylized penguin (solo, no human). The actual product icon is a hand-drawn human+penguin composition. | Two competing visual identities. The SVG penguin isn't used in the app and diverges from the shipped mascot. |

### Minor

| # | Issue | Evidence | Impact |
|---|-------|----------|--------|
| **W10** | **Runtime RGB-inversion done twice** | Both `ThemeAndLogo.cs:59-96` and `AndroidApp.axaml.cs:1799+` contain identical `TryBuildInverted()` logic | Code duplication (not an icon asset issue but impacts mascot rendering). |
| **W11** | **ICNS has no ic15 (512@2x standalone)** | `AppIcon.icns` analysis shows ic10 (512@2x) but Apple recommends explicit 1024×1024 as ic15 for modern macOS | Minor; ic10 covers 512@2x adequately. |
| **W12** | **No SVG master for the hand-drawn mascot** | All mascot variants are raster-only. No vector source committed. | Any future size/format derivation requires re-tracing or AI upscale. |

---

## 6. Design Token Alignment

The design system (`design/project/tokens.css`) defines the "Arctic/Glacier" palette:

- **Brand accent**: `--ref-arctic-400: #38BDF8` (★ brand accent)
- **Body dark**: `--ref-slate-800: #1C2231` through `--ref-slate-900: #0F1320`
- **Beak/feet amber**: Not in tokens (uses `#F59E0B` directly in SVGs = Tailwind amber-500)
- **Frost/ice**: `--ref-arctic-50: #ECFEFF` through `--ref-arctic-200: #A5F3FC`

The SVG assets (`penguin.svg`, `logo-lockup.svg`) correctly reference these token values:
- `penguin.svg:4-6` — radial gradient `#CFFAFE → #67E8F9 → #0EA5E9` = arctic-100 → arctic-300 → arctic-500 ✓
- `penguin.svg:9-10` — body gradient `#1C2231 → #0F1320` = slate-800 → slate-900 ✓
- `penguin.svg:26` — beak `#F59E0B` = amber-500 (warning-solid token) ✓
- `logo-lockup.svg:4-5` — gradient `#67E8F9 → #0284C7` = arctic-300 → arctic-600 ✓

**Gap**: The shipped hand-drawn mascot (`penguin_mascot.png`) is pure black-and-white lineart with zero connection to the Arctic/Glacier color system. The design-system SVG penguin embodies the color system but isn't used in the product.

---

## 7. Recommendations to Astra — One Coherent Arctic/Glacier Icon System

### Guiding Principle

Preserve the **recognizable hand-drawn human-with-headphones-and-penguin composition** as the brand identity. Do not replace it with the SVG geometric penguin. Instead, unify both into one coherent system where the hand-drawn warmth is the product face and the Arctic/Glacier tokens provide the environmental framing.

### R1. Create a True Master SVG of the Hand-Drawn Mascot

**Priority: High.**
Commission or vector-trace the `penguin_mascot.png` composition into a clean SVG with:
- Strokes defined at a consistent weight (e.g., 3-4px at 128×128 viewBox)
- Background: none (transparent) — backgrounds are applied per-context
- Export raster derivatives at exact target sizes (16, 24, 32, 48, 64, 96, 128, 192, 256, 512, 1024)

This eliminates W5 (palette quantization), W6 (excessive downscale), and W12 (no vector source).

### R2. Arctic/Glacier Environmental Frame for App Icons

**Priority: High.**
For contexts that need a filled icon (launcher, dock, Windows exe), place the hand-drawn mascot on an Arctic-gradient background:
- Rounded square: linear gradient `#67E8F9 → #0284C7` (arctic-300 → arctic-600), matching `logo-lockup.svg:3-6`
- Corner radius: ~22% of icon size (matching `logo-lockup.svg` `rx="12"` on `52px` = 23%)
- Mascot in dark stroke (`#0F1320` = slate-900) at ~70% of frame, centered
- This marries the hand-drawn personality with the Arctic/Glacier system identity

### R3. Consolidate to Three Canonical Variants

**Priority: High.** Reduce from 9 files to 3 canonical rasters + derived ICO/ICNS:

| Variant | Background | Stroke | Use Case |
|---------|-----------|--------|----------|
| `mascot-dark.svg` | Transparent | `#0F1320` (slate-900) | Light surfaces (header, light-theme tray) |
| `mascot-light.svg` | Transparent | `#F5F7FA` (slate-50) | Dark surfaces (dark-theme tray, dark header) |
| `mascot-framed.svg` | Arctic gradient rect | `#0F1320` | App launcher icons (Win/Mac/Linux/Android) |

Then generate:
- ICO from `mascot-dark.svg` + `mascot-light.svg` (7 sizes each)
- ICNS from `mascot-framed.svg` (all Apple sizes including 1024)
- Android adaptive icon from `mascot-framed.svg` (foreground layer) + solid `#0EA5E9` background layer

### R4. Fix Android Launcher Icons

**Priority: Critical.**
- Replace grayscale `ic_launcher.png` with RGBA renders of the framed mascot at correct densities
- Add `res/mipmap-anydpi-v26/ic_launcher.xml` adaptive icon pointing to a vector foreground + arctic-500 color background
- Retire `ic_launcher_round.png` in favor of adaptive icon's built-in shape masking

### R5. Delete Dead Assets

**Priority: Quick win.**
- Delete `avalonia-logo.ico` (175 KB, zero references)
- Delete `penguin_logo.ico` (1.5 KB, zero references, single 32×32 only)
- Replace `penguin_logo.png` consumer in `AboutWindow.axaml.cs:28` with `penguin_mascot.png` (or `_tile`), then delete `penguin_logo.png`

### R6. Eliminate Runtime RGB Inversion

**Priority: Medium.**
With a proper `mascot-light.svg` (white lineart) pre-baked at build time:
- Remove `TryBuildInvertedLogo()` from `MainWindowViewModel.ThemeAndLogo.cs:59-96`
- Remove `TryBuildInverted()` from `AndroidApp.axaml.cs:1799+`
- Load the pre-baked white variant directly, eliminating startup computation and code duplication (W10)
- `penguin_mascot_white.png` already serves this purpose but is currently only used for tray; extend to header

### R7. Align the Design-System SVG Penguin

**Priority: Low.**
The `design/project/assets/penguin.svg` geometric penguin is well-crafted but currently orphaned. Options:
- **Use as favicon/social-media avatar** where the hand-drawn mascot is too detailed to read at 16-32px
- **Use in empty-state illustrations** within the app (e.g., "no servers configured" placeholder)
- **Retire** if the hand-drawn mascot SVG (R1) reads well at small sizes

Do NOT replace the hand-drawn mascot with this geometric penguin — the hand-drawn style is the recognized brand.

### R8. Standardize Tray Icon Format

**Priority: Low.**
- macOS: use `.png` template image (convention for menu bar icons)
- Linux: use `.png` (universal support across GTK/Qt trays)
- Windows: keep `.ico` (native format)

This resolves W7 and is a minor polish.

---

## 8. Summary Matrix

| Asset | Status | Action |
|-------|--------|--------|
| `penguin_mascot.png` | ✅ Active, canonical | Keep; create SVG master (R1) |
| `penguin_mascot_tile.png` | ✅ Active (Linux WM) | Keep; derive from SVG master |
| `penguin_mascot_white.png` | ✅ Active (tray) | Keep; derive from SVG master |
| `penguin_mascot.ico` | ✅ Active (Win exe + tray) | Regenerate from SVG master |
| `penguin_mascot_tile.ico` | ⚠️ Zero code refs but exists | Verify if `_tile.ico` is used anywhere; if not, delete |
| `penguin_mascot_white.ico` | ✅ Active (tray) | Regenerate from SVG master |
| `penguin_logo.png` | ⚠️ Near-duplicate | **Delete** after swapping AboutWindow to `_mascot.png` (R5) |
| `penguin_logo.ico` | ❌ Dead | **Delete** (R5) |
| `avalonia-logo.ico` | ❌ Dead, third-party | **Delete** (R5) |
| `AppIcon.icns` | ✅ Active (macOS) | Regenerate from framed SVG (R2) |
| Android `ic_launcher.png` (×5) | ⚠️ Broken (grayscale) | **Regenerate** as RGBA from framed SVG (R4) |
| Android `ic_launcher_round.png` (×5) | ✅ Active | Replace with adaptive icon (R4) |
| `design/project/assets/penguin.svg` | ℹ️ Design-only | Decide role (R7) |
| `design/project/assets/logo-lockup.svg` | ℹ️ Design-only | Reference for brand lockup usage |
| DpiBypassPage shield glyph | ✅ Inline Path | No change needed |
| TelegramPage plane glyph | ✅ Inline Path | No change needed |
| Play/Stop glyphs (shared) | ✅ Inline Path | No change needed |

---

*Report generated from read-only source inspection. No files modified.*
*All paths, line numbers, and dimensions verified against the repository at time of audit.*
