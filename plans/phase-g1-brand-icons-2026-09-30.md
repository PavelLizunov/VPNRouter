# G1: a readable mascot and a full icon set for every platform

## Why

The owner pointed out (2026-09-30) that the mascot is shown badly: black line art on a transparent background
disappears on dark surfaces (dark taskbar, dark launcher, dark theme), the "white" variant disappears on light ones, the
`logo` and `tile` images were hard white rectangles, and the graphics in general looked dated. A check of the assets
confirmed it: `penguin_mascot.png` is black 640 px line art (almost invisible on black or navy), the Android launcher had
only legacy square and round PNGs (no adaptive icon, no themed monochrome layer), the VPN notification used the stock
Android padlock as its status icon, `AppIcon.icns` and the `.ico` files were built from the same line art, and three
unreferenced leftovers (`avalonia-logo.ico`, `penguin_logo.ico`, `penguin_mascot_tile.ico`) sat in the assets.

## What

- Vector masters in `tools/icons/src`: `mark.svg` (a penguin with cyan headphones: navy body with a light rim so it reads
  on dark and light, white face and belly, amber beak and feet), `mark-mono.svg` (single-colour silhouette for status icons
  and themed icons), `tile.svg` (the brand-gradient rounded square). `tools/icons/make-icons.py` (needs rsvg-convert and
  ImageMagick, run by hand; the outputs are committed) renders everything:
  - Android: adaptive icon (`mipmap-anydpi-v26/ic_launcher*.xml`, gradient background drawable, foreground and monochrome
    layers per density), legacy square and round PNGs, status icon `drawable/ic_stat_vpn.xml` used by the VPN notification
    (falls back to the old one if the resource is missing).
  - Windows: `penguin_mascot.ico` (mark alone at 16 to 32 px, tile from 48 px) and `penguin_mascot_white.ico` (mark at all
    sizes, for the tray), same file names as before so the theme selection code and its tests are untouched.
  - macOS: `AppIcon.icns` with the 824 px tile on a 1024 px canvas and a soft shadow, the usual macOS icon margins.
  - In-app images: `penguin_mascot.png`, `penguin_mascot_white.png` (the mark), `penguin_mascot_tile.png`,
    `penguin_logo.png` (the tile).
- The three unused `.ico` leftovers are removed.

## Verification

- CI: Android compile (new resources, service change) and the existing tests (they check the ico names used per theme and
  the legacy mipmap sizes, which are unchanged).
- Emulator: launcher icon on the home screen and in the app drawer, the notification status icon and the app header.
- Not checked on a device: the Windows taskbar and tray and the macOS Dock (pictures of the generated frames only).
- The art is my redraw of the mascot (the old sketch is gone from the assets but stays in git history); the owner can
  replace `tools/icons/src/mark.svg` and rerun the script.

## Outcome

Merged after green exact-head CI.
