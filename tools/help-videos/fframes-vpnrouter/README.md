# VPNRouter film

A 36-second product film, written in Rust/SVG and rendered by [fframes](https://fframes.studio/). It explains application-based VPN routing with an animated split path and published VPNRouter UI fixtures. This is not a screen recording or a live connection test. In addition to the original default film, the renderer supports eight distinct visual styles (anime, swiss, blueprint, editorial, terminal, paper, kinetic, and cinema). Clickable repository links are provided beside delivery files and in documentation rather than unclickable video text.

## Outputs

### Original deliveries

- `out/vpnrouter-film.mp4`: 1920x1080, 30 fps, H.264/AAC, 36 seconds.
- `out/vpnrouter-github.mp4`: 1280x720 distribution copy with the same timeline.
- `out/vpnrouter-film-source.zip`: complete standalone sources, lockfile, media and licenses.
- `out/contact-sheet.png`, `out/frames/`: visual review material.
- `out/inspection.json`, `out/continuity.json`, `out/ffprobe.json`, `out/audio-levels.txt`, `out/SHA256SUMS.txt`: executed export evidence.

Original deliveries are preserved.

### Style variations

Preview-first style exports via `export-styles.sh` output to `out/styles/preview/<slug>/`. After selection, rerender selected styles to `out/styles/final/<slug>/` from the same source, not by enlarging preview pixels.

- `film.mp4`: 854x480 preview or selected 1920x1080 final, 30 fps, H.264/AAC, full 36 seconds per style.
- `contact-sheet.png`: six-cell contact sheet for visual review.
- `frames/`: representative PNG/SVG frame captures (3s, 9s, 16s, 22s, 28s, 34s).
- `timeline.json`, `inspection.json`, `continuity.json`, `ffprobe.json`: measured frame and encode inspection data.
- `out/styles/film-styles-source.zip`: complete standalone source archive covering all styles.
- `out/styles/<mode>/SHA256SUMS.txt`: SHA-256 checksums for all exported style films and source archive.

| Style | Slug | Typography | Theme | Visual direction |
|---|---|---|---|---|
| Anime | `anime` | Anton / JetBrains Mono | Dynamic split | Cel panels, diagonal cuts, speed lines, unchanged mascot as two story paths |
| Swiss | `swiss` | DM Sans Medium | Red, ivory, black | Oversized left-aligned type, measured grid, lateral wipes for one rule at a time |
| Blueprint | `blueprint` | JetBrains Mono | Cyan on blueprint dark | Drafting guides, coordinate grid, mono labels, route construction |
| Editorial | `editorial` | Instrument Serif / DM Sans | Warm paper, terracotta | Serif headlines, narrow captions, asymmetric crops, magazine spread |
| Terminal | `terminal` | JetBrains Mono | Phosphor green/amber | Console commands, stepped reveals, split console app routing |
| Paper | `paper` | DM Sans Medium | Layered paper neutrals | Offset cut-paper shapes, layered labels, sliding strips grouping apps |
| Kinetic | `kinetic` | Anton / DM Sans | High-contrast bold | Giant words, directional masks, moving type settling before reading |
| Cinema | `cinema` | DM Sans / Instrument Serif | Letterboxed neutral | Negative space, widescreen framing, restrained warm accents on UI |

Style batch export is specified; outputs are generated when executed on an authorized worker and verified.

The renderer does not install, run or change VPNRouter. No release is published by the rendering workflow.

## Render

Use an Ubuntu 24.04 build worker with a current stable Rust toolchain, a C/C++ linker, Clang/libclang, pkg-config, FFmpeg, libx264-dev, nasm and zip. The CPU backend works without a GPU. `export.sh` uses the committed Cargo.lock and builds fframes' static FFmpeg from source against the worker's x264 rather than downloading an archive built for a different x264 ABI; internet access is required for a clean build. Start with an empty renderer target directory when switching from an older prebuilt-FFmpeg build; the CI cache uses a separate source-build namespace. Note that source-built FFmpeg follows mutable upstream release/9.0, which is not fully pinned.

From this directory, to render the original default film:

```bash
bash export.sh
```

To render all eight full-length 480p previews, then only selected 1080p finals:

```bash
bash export-styles.sh preview
# Only after the owner selects these styles:
bash export-styles.sh final anime swiss
```

The default mode is preview. Final mode requires an explicit nonempty style list. Preview and final files do not overwrite one another or the original delivery. The renderer selects a style through `FILM_STYLE`; the batch script sets it per export.

For individual review commands:

```bash
# Default or style-selected commands:
FILM_STYLE=swiss cargo run --release --locked -- timeline
FILM_STYLE=blueprint cargo run --release --locked -- inspect --all-frames --fail-on warning
FILM_STYLE=editorial cargo run --release --locked -- strip -n 6 -o out/styles/review.png
FILM_STYLE=terminal cargo run --release --locked -- --scale 0.4444444444444444 frame 9s,16s,34s -o out/styles/review-frames
FILM_STYLE=paper cargo run --release --locked -- --scale 0.4444444444444444 render -o out/styles/review-silent.mp4
```

Run commands from this directory because media paths are relative. The standalone source archive has the same directory layout. `export.sh` and `export-styles.sh` compile the renderer, check every frame, render the visual master, add music, validate encoded files and package the sources. `ffmpeg` does not generate or animate the graphics: it muxes music and encodes distribution copies.

The repository's Actions workflow builds an exact checkout on a disposable GitHub-hosted runner. Its read-only preflight records CPU/load, available RAM/swap, disk, active compiler processes and toolchain before compiling. Build/render work is not performed on the DSH control plane.

## Edit

- `src/lib.rs`: scene durations, style dispatch and video-wide progress.
- `src/shots.rs`: original six scenes and English copy.
- `src/styles.rs`, `src/variants.rs`, `src/elements.rs`: style treatments, distinct compositions and shared diagram elements.
- `src/graphics.rs`: shared layout, native fframes easing and animated paths.
- `src/main.rs`: media loading, environment parsing and encoder settings.

The six durations sum to 36 seconds. Scenes 2-6 start 0.4 seconds before the previous scene ends and fade over it; fframes adds this overlap window to the scene duration, so it does not shorten the film. Rust checks pin that duration/frame contract, keep route geometry finite, require screenshot annotations inside the shared motion group and check all style transcripts. Pipeline tests cover explicit final selection, mode dimensions, metadata titles and checksum rejection. Keep the final export duration and `verify_export.py` in sync if changing this contract. Text is intentionally large and the diagram is illustrative, not a claim about observed packets or latency.

### Coordinate registration and branding rules

- Screenshot and annotation registration (FFRAMES-HIGHLIGHT-DRIFT): screenshots and their focus rectangles or highlight rings share the exact same transform in local source coordinates, including entrance animation, drift and scale throughout the timeline.
- Authored text and titles: no repository names, owner names or GitHub URLs appear in authored video text, graphics or MP4 metadata titles. Clickable repository links belong beside delivered files and in documentation. Product artwork is preserved, while repository/owner credits and source links stay in the documentation. The original composition remains available alongside the eight styles.

## Asset provenance

Product facts and images are from [VPNRouter](https://github.com/PavelLizunov/VPNRouter) at `c3ebc46ee7a336bc689a893a00d2db8c9bf4a300`:

- `media/logo.png`: byte-identical copy of `VPNRouter.App/Assets/penguin_logo.png`.
- `media/mascot.png`: byte-identical copy of `VPNRouter.App/Assets/penguin_mascot_white.png`; the mascot is not redrawn.
- `media/applications.png`, `media/home-connected.png`, `media/home-light.png`: byte-identical copies of the public English README images under `docs/images/`. These were rendered from sample data, not captured from a live account.
- DM Sans Regular/Medium and JetBrains Mono Regular: copies of the font assets in the upstream fframes examples at `654ae86ffdff31202940d8cf05cdcdc30efe6228`. The associated SIL Open Font Licenses are in `licenses/`.
- Additional display fonts: `Anton-Regular.ttf` and `InstrumentSerif-Italic.ttf` sourced byte-for-byte from upstream fframes examples at `654ae86ffdff31202940d8cf05cdcdc30efe6228`, with licenses from Google Fonts official `ofl/anton/OFL.txt` and `ofl/instrumentserif/OFL.txt` stored in `licenses/Anton-OFL.txt` and `licenses/Instrument-Serif-OFL.txt`.
- `media/interchange.mp3`: "Interchange" by TRG Banks, Dreamland (2018), available under CC0 1.0. Source and license: [Wikimedia Commons file page](https://commons.wikimedia.org/wiki/File:TRG_Banks_-_02_-_Interchange.ogg), [original Free Music Archive entry](https://freemusicarchive.org/music/TRG_Banks/Dreamland/Interchange), [CC0 deed](https://creativecommons.org/publicdomain/zero/1.0/). The film uses the opening 36 seconds with loudness normalization and fades. Music is existing licensed material, not AI-generated. Attribution is retained voluntarily.

The film source and product assets follow VPNRouter's GPL-3.0-or-later license; see `LICENSE`. Fonts and music retain the licenses above. fframes is an MIT-licensed dependency. No private repository material, live settings, account information, subscription URL or credential is included.

## Verification limits

Frame inspection checks layout, media and fonts; visual review still needs the exported frames. Pop detection is reported for review, not treated as proof of smoothness. Encoded metadata and full decoding are checked by `verify_export.py`. Distinct full exports are validated against checksums, not tiny artifacts. Automated inspection is required across all styles rather than subjective acceptance. A clean video export does not test VPNRouter's routing, cross-platform behavior or a real connection. No installed application is controlled by this project.
