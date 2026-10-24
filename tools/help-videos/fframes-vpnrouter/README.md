# VPNRouter film

A 36-second product film, written in Rust/SVG and rendered by [fframes](https://fframes.studio/). It explains application-based VPN routing with an animated split path and published VPNRouter UI fixtures. This is not a screen recording or a live connection test.

## Outputs

- `out/vpnrouter-film.mp4`: 1920x1080, 30 fps, H.264/AAC, 36 seconds.
- `out/vpnrouter-github.mp4`: 1280x720 distribution copy with the same timeline.
- `out/vpnrouter-film-source.zip`: complete standalone sources, lockfile, media and licenses.
- `out/contact-sheet.png`, `out/frames/`: visual review material.
- `out/inspection.json`, `out/continuity.json`, `out/ffprobe.json`, `out/audio-levels.txt`, `out/SHA256SUMS.txt`: executed export evidence.

The renderer does not install, run or change VPNRouter. No release is published by the rendering workflow.

## Render

Use a Linux build worker with a current stable Rust toolchain, a C/C++ linker, Clang/libclang, pkg-config, FFmpeg, nasm and zip. The CPU backend works without a GPU. The initial fframes build downloads its static FFmpeg dependencies; internet access is required for a clean build.

From this directory:

```bash
bash export.sh
```

For individual review commands:

```bash
cargo run --release -- timeline
cargo run --release -- inspect --all-frames --fail-on warning
cargo run --release -- strip -n 12 -o out/review.png
cargo run --release -- frame 9s,16s,34s -o out/frames
cargo run --release -- render -o out/vpnrouter-film-silent.mp4
```

Run commands from this directory because media paths are relative. The standalone source archive has the same directory layout. `export.sh` compiles the renderer, checks every frame, renders the visual master, adds music, validates both encoded files and packages the sources. `ffmpeg` does not generate or animate the graphics: it muxes music and encodes the smaller copy.

The repository's `Render fframes film` Actions workflow builds an exact checkout on a disposable GitHub-hosted runner. Its read-only preflight records CPU/load, available RAM/swap, disk, active compiler processes and toolchain before compiling. Build/render work is not performed on the DSH control plane.

## Edit

- `src/lib.rs`: scene durations and video-wide branding/progress.
- `src/shots.rs`: six scenes and English copy.
- `src/graphics.rs`: shared layout, native fframes easing and animated paths.
- `src/main.rs`: media loading and encoder settings.

The six durations sum to 36 seconds. Scenes 2-6 start 0.4 seconds before the previous scene ends and fade over it; fframes adds this overlap window to the scene duration, so it does not shorten the film. Two small Rust checks pin that duration/frame contract and keep the animated route geometry finite across the export timeline. Keep the final export duration and `verify_export.py` in sync if changing this contract. Text is intentionally large and the diagram is illustrative, not a claim about observed packets or latency.

## Asset provenance

Product facts and images are from [VPNRouter](https://github.com/PavelLizunov/VPNRouter) at `c3ebc46ee7a336bc689a893a00d2db8c9bf4a300`:

- `media/logo.png`: byte-identical copy of `VPNRouter.App/Assets/penguin_logo.png`.
- `media/mascot.png`: byte-identical copy of `VPNRouter.App/Assets/penguin_mascot_white.png`; the mascot is not redrawn.
- `media/applications.png`, `media/home-connected.png`, `media/home-light.png`: byte-identical copies of the public English README images under `docs/images/`. These were rendered from sample data, not captured from a live account.
- DM Sans Regular/Medium and JetBrains Mono Regular: copies of the font assets in the upstream fframes examples at `654ae86ffdff31202940d8cf05cdcdc30efe6228`. The associated SIL Open Font Licenses are in `licenses/`.
- `media/interchange.mp3`: "Interchange" by TRG Banks, Dreamland (2018), available under CC0 1.0. Source and license: [Wikimedia Commons file page](https://commons.wikimedia.org/wiki/File:TRG_Banks_-_02_-_Interchange.ogg), [original Free Music Archive entry](https://freemusicarchive.org/music/TRG_Banks/Dreamland/Interchange), [CC0 deed](https://creativecommons.org/publicdomain/zero/1.0/). The film uses the opening 36 seconds with loudness normalization and fades. Music is existing licensed material, not AI-generated. Attribution is retained voluntarily.

The film source and product assets follow VPNRouter's GPL-3.0-or-later license; see `LICENSE`. Fonts and music retain the licenses above. fframes is an MIT-licensed dependency. No private repository material, live settings, account information, subscription URL or credential is included.

## Verification limits

Frame inspection checks layout, media and fonts; visual review still needs the exported frames. Pop detection is reported for review, not treated as proof of smoothness. Encoded metadata and full decoding are checked by `verify_export.py`. A clean video export does not test VPNRouter's routing, cross-platform behavior or a real connection. No installed application is controlled by this project.
