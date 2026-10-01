# D-1: desktop icons from the Lucide set, design review captures

## Why

The owner asked to finish the design fixes and to carry them to the PC version. The desktop app (`VPNRouter.App`) has
its own icon control, `Controls/Pictogram.cs`, with path data drawn by hand on the 24 grid, while Android draws the
Lucide set from `VPNRouter.Core/Services/UiIcons.cs` (U5). Two icon styles and hand-drawn geometry are what the owner
complained about on Android.

## Inventory (script, 2026-10-01, `origin/main` at `a3a6bd78`)

A scan of `VPNRouter.App` (`.cs` and `.axaml`, comments skipped, escapes decoded) finds 179 symbol characters in 28
files; 42 of them are the symbol table of `PictogramText.cs` itself. Most of the others are already drawn as
`Pictogram` icons: `PictogramText` replaces the listed symbols in a text with an inline icon. Left as text on screen:

- `ServersPage.axaml`, the "VPN or proxy intercepts" warning: `⚠` prefix as text (to replace).
- Free configs status line: `(✓✓)` in the middle of the "wait for verification" sentence (to replace).
- Arrows inside sentences (`exe → routed via VPN`, `n rules → file`), list bullets, log lines (kept: text).
- Not bound anywhere (`StatusDot`, `VpnBadgeText`, `LblDpiWarning`, `DeepDisplay` is test-only): kept, they are
  public surface pinned by the ViewModel characterization hash and not visible.

## Scope

1. `Pictogram` draws the Lucide path data from `UiIcons` for every id that has a Lucide counterpart; the ids and the
   API stay. 14 Lucide files copied unchanged from the pinned tag 1.49.0 with curl (`ban`, `circle`, `circle-plus`,
   `circle-minus`, `circle-dashed`, `ellipsis`, `shield`, `arrow-down-up`, `arrow-left`, `arrow-right`, `toggle-right`,
   `send`, `timer`, `triangle-alert`). Mapping follows Android where it already chose: apply, refresh and retest use
   `refresh-cw`, delete uses `trash`, stop uses `square`. Kept as plain circles: the status dots and the filled centre
   of the selected radio mark. `triangle-alert` is 0.02 units off mirror symmetry in Lucide (and its dot is drawn
   from 12 to 12.01); it is snapped in `lucide-to-cs.py` like `power`. Strokes stay 1.8 grid units but never thinner
   than 1.2 px, because inline icons are drawn at 8 to 12 px where 1.8 units is under 1 px.
2. The two remaining text symbols above become icons.
3. `PageScreenshotDesignTests`: captures of every page, light and dark, 360 and 520 px, Settings sub-tabs, Russian and
   the whole window, for review. Excluded from CI by name, like the other page screenshot tests.

## Invariants

- Pictogram ids, `PictogramText` behaviour and accessible names unchanged (`PictogramTextTests`).
- No hex colours in XAML; tokens only. The mascot is not touched.
- No public ViewModel surface change (characterization hash not re-pinned).

## Verification

- Local: `python3 tools/icons/check-icons.py` (symmetry, grid, contact sheets).
- Windows worker, exact SHA: `PictogramTextTests`, `DesktopPictogramScreenshotTests`, `VisualDiffTests`,
  `PageScreenshotDesignTests`; the PNGs are copied back and looked at.
- PR CI: compile, test, characterization-windows.

## Outcome

Windows worker (`windows-worker`, private toolchain, preflight: CPU 1 %, 13.2 GB free RAM, 13.5 GB free disk, no
dotnet or java running), exact SHA `d8d10c97`: 80 tests, 76 passed; `PictogramTextTests` (4, one new), `UiIconsTests`,
`DesktopPictogramScreenshotTests` and all three `VisualDiffTests` baselines pass (the icon change stays under the 2 %
threshold, so no baseline was re-pinned). The 4 failures are the four ApplicationsPage captures of the new design test,
the known `PAGESCREENSHOT-RENDER-INVALIDATION` defect (same exception as the excluded `PageScreenshotTests`).
`check-icons.py`: all mirror and grid checks pass; contact sheets looked at. Before and after captures compared by eye
(DPI bypass hero and legend, Telegram, Free configs, Settings, window header).
