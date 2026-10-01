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
   API stay. New Lucide files copied unchanged from the pinned tag (`tools/icons/lucide/VERSION`).
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

(filled in at delivery)
