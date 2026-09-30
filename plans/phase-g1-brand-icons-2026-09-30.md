# G1: a readable mascot on every surface (mascot art unchanged)

## Why

The owner pointed out (2026-09-30) that the mascot is shown badly: black line art on a transparent background disappears on
dark surfaces (dark taskbar, dark launcher, dark theme), the "white" variant disappears on light ones, the `logo` and `tile`
images were hard white rectangles, the Android launcher had only legacy square and round PNGs (no adaptive icon, no themed
monochrome layer) and the VPN notification used the stock Android padlock. The owner's rule, given after a first
version of this step had redrawn the mascot: **the mascot must not be changed**; an SVG redraw would have to be 1:1 with
the original, which is not possible without a trace tool, so the drawing stays as it is.

## What

- The original art is kept byte-identical (`VPNRouter.App/Assets/penguin_mascot.png`, `penguin_mascot_white.png`) and
  copied as the source in `tools/icons/src`. The first version of G1 (PR #413) replaced it with a new penguin drawing; this
  step restores the originals and removes that drawing.
- `tools/icons/make-icons.py` (needs ImageMagick; the outputs are committed) only scales the original art and places it on a
  rounded tile: a light tile for the black line art, a dark tile for the white variant, so nothing vanishes:
  - Windows: `penguin_mascot.ico` (light tile, black art) and `penguin_mascot_white.ico` (dark tile, white art), all
    sizes 16 to 256; same file names, so the theme selection code and its tests are unchanged.
  - macOS: `AppIcon.icns` with the 824 px light tile on a 1024 px canvas and a soft shadow.
  - In-app: `penguin_mascot_tile.png` and `penguin_logo.png` are the art on the light rounded tile.
  - Android: adaptive icon (`mipmap-anydpi-v26`, light gradient background drawable, foreground and monochrome layers
    made from the original art), legacy square and round PNGs, and the notification status icon is the plain shield of the
    quick-settings tile (not the mascot).
- `.gitignore` re-includes `tools/icons/` (the first version of this step did not commit the generator because `tools/*`
  is ignored).

## Verification

- CI: Android compile, existing tests (icon names per theme, legacy mipmap sizes).
- Emulator: launcher icon and app header with the original art.
- Not checked on a device: Windows taskbar and tray, macOS Dock.

## Outcome

Merged after green exact-head CI.
