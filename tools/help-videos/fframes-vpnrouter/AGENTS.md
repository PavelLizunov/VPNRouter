# fframes video renderer zone guidelines

This zone contains the standalone product film renderer written in Rust and SVG, built with [fframes](https://fframes.studio/). It produces 36-second animated motion explainers illustrating VPNRouter application-based split tunneling.

Follow the canonical repository contract in [docs/agent-contract.md](../../../docs/agent-contract.md) and tool instructions in [tools/AGENTS.md](../../AGENTS.md).

## Scope and responsibilities

- Independent Rust crate (`vpnrouter-film`) with a 1920x1080 logical canvas, 30 fps H.264/AAC explainers using SVG compositing and native easing. Render all options as 854x480 previews first; rerender only owner-selected styles at 1920x1080 from source, never upscale the preview.
- Maintains the original default film and eight substantially different style variations: anime, swiss, blueprint, editorial, terminal, paper, kinetic, and cinema.
- Renders fixture UI graphics and animated routing paths. The renderer does not install, run, configure, or test the VPNRouter application or alter system network state.

## Film styles and design contract

The film uses a fixed six-scene timeline totaling 36 seconds (1080 frames at 30 fps):

1. Choose (5.0s)
2. Split (7.0s)
3. Applications (7.0s)
4. Connect (6.0s)
5. Platforms (5.0s)
6. Choice (6.0s)

Each style variation implements distinct composition, typography, graphical treatment, and motion, not simple palette swaps:

| Style | Slug | Typography | Theme | Visual mechanism and motion |
|---|---|---|---|---|
| Anime | anime | Anton / JetBrains Mono | Dynamic split | Cel panels, diagonal cuts, speed lines, unchanged mascot as two story paths |
| Swiss | swiss | DM Sans Medium | Red, ivory, black | Oversized left-aligned type, measured grid, lateral wipes for one rule at a time |
| Blueprint | blueprint | JetBrains Mono | Cyan on blueprint dark | Drafting guides, coordinate grid, mono labels, route construction |
| Editorial | editorial | Instrument Serif / DM Sans | Warm paper, terracotta | Serif headlines, narrow captions, asymmetric crops, magazine spread |
| Terminal | terminal | JetBrains Mono | Phosphor green/amber | Console commands, stepped reveals, split console app routing |
| Paper | paper | DM Sans Medium | Layered paper neutrals | Offset cut-paper shapes, layered labels, sliding strips grouping apps |
| Kinetic | kinetic | Anton / DM Sans | High-contrast bold | Giant words, directional masks, moving type settling before reading |
| Cinema | cinema | DM Sans / Instrument Serif | Letterboxed neutral | Negative space, widescreen framing, restrained warm accents on UI |

### Motion and content invariants

- Screenshot and highlight alignment (FFRAMES-HIGHLIGHT-DRIFT): screenshots and their focus rectangles or highlight rings must share the exact same transform in local source coordinates, including entrance animation, drift, and scale throughout the timeline.
- Authored video text: no repository names, owner handles, or GitHub URLs in video scenes, graphic overlays, or MP4 metadata titles. Text in video is not clickable; provide real clickable repository links beside delivery files and in documentation.
- Titles: repository names, owner credits and GitHub addresses stay outside the video graphics and metadata title. Product artwork is preserved. The original composition remains available alongside the eight new styles.
- Product fixtures: existing images (`media/logo.png`, `media/mascot.png`, `media/applications.png`, `media/home-connected.png`, `media/home-light.png`) are byte-identical to repository assets and remain unchanged. The mascot art is never redrawn.
- Fonts: DM Sans Regular/Medium and JetBrains Mono Regular are existing assets. Additional display faces are Anton-Regular.ttf and InstrumentSerif-Italic.ttf sourced byte-for-byte from upstream fframes examples at `654ae86ffdff31202940d8cf05cdcdc30efe6228`, with licenses from Google Fonts official `ofl/anton/OFL.txt` and `ofl/instrumentserif/OFL.txt` stored in `licenses/Anton-OFL.txt` and `licenses/Instrument-Serif-OFL.txt`.
- Soundtrack: licensed CC0 "Interchange" by TRG Banks with loudness normalization (-18 LUFS) and fades. Soundtrack is existing licensed audio, distinguished from AI-generated audio (no generative audio tools used).

## Export workflow and commands

The batch renderer reads the `FILM_STYLE` environment variable (`anime`, `swiss`, `blueprint`, `editorial`, `terminal`, `paper`, `kinetic`, `cinema`). Unset defaults to the original style.

- Single film export:
  ```bash
  bash export.sh
  ```
  Recreates the original-format deliverables in `out/`; it overwrites that set. Use the isolated batch command below for additional styles so delivered originals remain untouched.

- Preview-first batch export:
  ```bash
  bash export-styles.sh preview
  # Only after the owner selects these styles:
  bash export-styles.sh final anime swiss
  ```
  Default mode is preview; final mode requires an explicit nonempty style list.
  Outputs to `out/styles/preview/<slug>/` (854x480) or
  `out/styles/final/<slug>/` (1920x1080), without overwriting the original set:
  - `film.mp4`: 30 fps H.264/AAC, full 36-second timeline per style.
  - `contact-sheet.png`: six-cell contact sheet for visual review.
  - `frames/`: representative PNG/SVG captures (3s, 9s, 16s, 22s, 28s, 34s).
  - `timeline.json`, `inspection.json`, `continuity.json`, `ffprobe.json`: measured verification data.
  - Shared deliverable archive: `out/styles/film-styles-source.zip` (complete sources, media, licenses).
  - Verification checksums: `out/styles/<mode>/SHA256SUMS.txt`.
  Note: style batch export is specified; do not claim it ran until executed on an authorized runner with complete evidence.

- Individual style commands:
  ```bash
  FILM_STYLE=anime cargo run --release --locked -- timeline
  FILM_STYLE=swiss cargo run --release --locked -- inspect --all-frames --fail-on warning
  FILM_STYLE=blueprint cargo run --release --locked -- strip -n 6 -o out/styles/review.png
  FILM_STYLE=editorial cargo run --release --locked -- --scale 0.4444444444444444 frame 3s,9s,16s,22s,28s,34s -o out/styles/review-frames
  FILM_STYLE=terminal cargo run --release --locked -- --scale 0.4444444444444444 render -o out/styles/review-silent.mp4
  ```

## Build and worker safety rules

- Never compile or render on the DSH control plane or development workstation (`harness-test`). Heavy builds and renders run exclusively on exact-SHA GitHub Actions or disposable authorized build workers after recorded capacity and SDK preflight (CPU load, free RAM and swap, disk space, running compiler processes, Rust and Clang toolchains).
- Cargo build: always use `cargo build --release --locked` with committed Cargo.lock.
- Source-built FFmpeg: `export FFMPEG_FORCE_BUILD=1` forces fframes to compile static FFmpeg against the worker's x264 library, avoiding ABI mismatches with prebuilt binaries. Use a fresh source cache namespace when switching toolchains or ABIs. Note that upstream FFmpeg release/9.0 source is mutable and not fully pinned.

## Verification and quality gates

- Automated inspection over subjective claims:
  - Frame inspection: `cargo run --release --locked -- inspect --all-frames --fail-on warning` checks all 1080 frame layouts for reported media, font and clipping problems; it does not prove the final composition looks right.
  - Pop detection: `cargo run --release --locked -- inspect --pops` scans for unintended single-frame visual jumps.
  - Full decode test: `ffmpeg -v error -xerror -i <film.mp4> -f null -` validates every frame of the final encode.
- Consumer artifact verification:
  - Verify exact mode dimensions (854x480 preview or 1920x1080 selected final), framerate (30 fps), frame count (1080 frames), and duration (36.0s) via ffprobe.
  - Verify distinct full exports; reject empty, truncated, or tiny placeholder files.
  - Compare SHA256 checksums against `SHA256SUMS.txt`.
  - Validate source ZIP integrity via `unzip -t` and confirm byte-for-byte fidelity of sources, media, and licenses without build caches or Git credentials.
- Visual inspection: review contact sheets and full-size sample frames for all eight styles before marking visual review complete.
- Do not mark verification results as passed before actual evidence exists.
