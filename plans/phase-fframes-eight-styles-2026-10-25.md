# Phase - Eight additional fframes film styles

- Owner: lead
- Branch: dsh/fframes-eight-styles
- Base: 3a876d67cb779167e8a57ff3a598a818062c5938
- Roadmap: N/A; owner-requested follow-up to PR #623.
- Risk: MEDIUM; isolated motion renderer, export scripts and project instructions.
- Blast radius: tools/help-videos/fframes-vpnrouter, its render workflow,
  root AGENTS.md and this task/defect record. No product runtime changes.
- Rollback: revert task-owned commits; retain the previously delivered out/ files.

## Why

The owner wants the successful fframes process retained in tracked project agent
instructions and at least eight additional, substantially different versions of
the same film, including anime. Repository names are not useful inside a video
because they are not clickable; put the real link beside the delivered files.

## What

- Owner amendment: preview first. Eight separate full 36-second, 854x480,
  30 fps H.264/AAC previews with the existing licensed soundtrack, six-part
  explanation and public sample-data UI fixtures. After the owner selects,
  rerender only the selected styles from sources at 1920x1080; never upscale
  preview pixels. Do not start eight final-resolution exports automatically.
- Different composition, typography, graphical treatment and motion, not palette
  swaps: anime, Swiss, blueprint, editorial, terminal, paper, kinetic, cinema.
- No repository name, owner name or GitHub URL in authored video titles. Original
  product images and mascot bytes remain unchanged; the inspected fixtures do
  not contain the repository name. Preserve product facts and attribution in
  source documentation instead of adding unclickable credits to the film.
- Fix FFRAMES-HIGHLIGHT-DRIFT by giving the screenshot and its annotation the
  same entrance, drift and scale; cover registration throughout the timeline.
- Reuse the working renderer, locked dependencies and source-built FFmpeg path.
  Preserve original exports and produce a separate styles output directory.
- Store durable instructions in a nearby tracked AGENTS.md and link it from the
  root routing instructions. Do not change Harness settings or managed memory.
- Excluded: merge, release, tag, deployment, installed application/VPN mutation,
  product READMEs, branding source edits, SDK installation on the control plane.
- Base exception: this follow-up depends on the verified, unmerged original
  film branch. Start from its exact accepted head instead of losing its sources
  by starting from main; do not merge either PR.

## Design read and purpose

Motion explainers for people choosing application-based VPN routes; ENERGY 3,
RHYTHM 3, MOTION 3. Existing product artwork anchors the set; no project design
system was found. Each style has a deliberate visual mechanism:

1. Anime: cel panels, diagonal cuts, speed lines and the unchanged original
   anime mascot make selected/direct routes read like two story paths.
2. Swiss: oversized left-aligned sans type, a measured red/ivory grid and lateral
   wipes prioritize a single routing rule at a time.
3. Blueprint: drafting guides, mono labels and route construction show how one
   device divides traffic; the grid is a coordinate aid, not generic decoration.
4. Editorial: serif headlines, warm paper, narrow captions and asymmetric crops
   make the UI read like an annotated magazine spread.
5. Terminal: mono commands, stepped reveals and a split console emphasize the
   chosen app list; explicitly illustrative output, not a real network trace.
6. Paper: offset cut-paper shapes, layered labels and sliding strips visualize
   moving apps into two groups without imitating the product UI.
7. Kinetic: giant words, directional masks and moving type make the route choice
   itself the main image; motion settles before each reading interval.
8. Cinema: letterboxed negative space, slow framing and restrained warm accents
   hold attention on real UI details instead of a persistent dashboard.

## How

1. Commit this brief first, open the new PR, observe exact-head green checks.
2. Add project agent instructions and batch-export support in disjoint scopes.
3. Implement shared screenshot registration and style-specific scene rendering;
   keep one film contract and explicit style selection, with no new Rust crates.
4. Review correctness, visual/content contract and export/security separately;
   record source-confirmed findings before fixing or deferring them.
5. Push the implementation and render its exact SHA on disposable Actions
   workers after recorded CPU/load, RAM/swap, disk, jobs and SDK preflight.
6. Download actual artifacts, compare checksums, inspect frames from every style,
   verify all eight full decodes and deliver MP4s plus reproducible sources.

## Verification

1. Build: PASSED in GitHub Actions push run 40174456482 (`cargo build --release --locked` with source-built FFmpeg against runner x264 ABI). Product solution build N/A because no product code/project changes.
2. Tests: PASSED. Cargo tests (`checks.rs` screenshot registration regression), Python pipeline unit tests (`test_pipeline.py`), and all-frame inspection (`inspect --all-frames --fail-on warning`) with 0 findings across all 1080 frames for all 8 styles. PR CI checks passed: `test` (4m15s), `characterization-windows` (7m21s), `go-test-windows` (43s), `grep` (6s).
3. Docs: PASSED. Updated `tools/help-videos/fframes-vpnrouter/README.md`, `tools/help-videos/fframes-vpnrouter/AGENTS.md`, root `AGENTS.md`, and this task brief.
4. Review: PASSED. Three-lens review findings recorded in `OPEN-DEFECTS.md` and verified:
   - FFRAMES-KINETIC-VERTICAL-SPACING: resolved in source, verified in CI render
   - FFRAMES-SCREENSHOT-DUPLICATE-ID: resolved in source, verified in CI render
   - FFRAMES-UNUSED-SCENE-COPY: resolved in source
   - FFRAMES-PIPELINE-ROOT-IMPORT: resolved in source, verified locally and in CI
   - FFRAMES-ARTIFACT-MODE-NAME: resolved in source, verified in artifact upload name
   - FFRAMES-HIGHLIGHT-DRIFT: resolved in source, verified in CI render
   - FFRAMES-KINETIC-CAPTION-CONTRAST: resolved in source, verified in CI render
   - FFRAMES-KINETIC-TITLE-COLUMN: resolved in source, verified in CI render
5. UI: PASSED. Exported contact sheets (`contact-sheet.png`) and representative frames (3s, 9s, 16s, 22s, 28s, 34s PNG/SVG) for all 8 styles. Pop inspection (`inspect --pops`) passed with 0 pops across all 1080 frames for all 8 styles.
6. Characterization/integration: PASSED. All 8 preview MP4s match exact 854x480 resolution, 30 fps, 1080 frames, 36.0s duration, AAC audio at -18 LUFS. Standalone source ZIP `out/styles/film-styles-source.zip` contains exactly 36 required files, valid CRC, and byte-identical media and licenses. Full decode test (`ffmpeg -v error -xerror -i film.mp4 -f null -`) passed for all 8 styles with unique decoded pictures. Checksums verified against `out/styles/preview/SHA256SUMS.txt`.

## Material unknowns

- Rust compilation, renderer warnings and visual composition remain unverified
  until the new exact-SHA export. A successful original export is only a baseline.
  (Resolved: compilation clean, 0 warnings/pops, visual contact sheets generated).
- Subjective listening/browser playback is not proven by ffprobe or full decode.
- Source-built FFmpeg currently follows upstream release/9.0; the existing
  successful cache is reused, not claimed to be a fully pinned toolchain.

## Outcome

- Status: COMPLETE; eight preview films rendered, verified, and delivered.
- Baseline: local, remote and PR #623 head all 3a876d67cb779167e8a57ff3a598a818062c5938;
  test, characterization-windows, go-test-windows and grep SUCCESS; render-film
  intentionally SKIPPED on same-repository PR. Worktree was clean.
- Existing fixtures inspected: applications.png and home-connected.png contain
  sample UI, not the repository name. No original image modification is planned.
- Brief commit: 616eec1424b449d2723cd987d2a6ea8d81fffb4d, PR #626;
  Actions run 40171026077 test, characterization-windows, go-test-windows and
  grep run 40171026025 SUCCESS. Exact-head brief gate passed before edits.
- Implementation commit: fb7d729a738c8c7c94fa2b4cb159495764d8527a, PR #626;
  Actions run 40174459335 test (4m15s), characterization-windows (7m21s),
  go-test-windows (43s) and grep run 40174459338 (6s) all SUCCESS.
  Push run 40174456482 render-film SUCCESS (~17m20s) on disposable Ubuntu 24.04 runner.
- Owner asked where rendering runs and proposed 480p/360p selection first.
  Adopted 854x480 preview-first on disposable Ubuntu 24.04 Actions CPU;
  explicit selected finals rerender the logical SVG canvas at 1920x1080.
- Added source dispatch for eight styles, shared screenshot group, two licensed
  display fonts, preview/final export split and tracked zone instructions.
- Static checks: bash syntax, Python AST, YAML parsing, ASCII documentation,
  local doc links and diff whitespace PASS.
- Preflight recorded in run 40174456482: runner `runnervmtai0g`, Linux 6.17.0-1022-azure,
  4 vCPUs, 14.6 GiB RAM free, 87.4 GiB disk free, rustc/cargo 1.99.0, clang /usr/bin/clang.
- Export verification in run 40174456482:
  - PASS preview/anime: (854, 480), 1080 frames, 36s H.264/AAC, full decode
  - PASS preview/swiss: (854, 480), 1080 frames, 36s H.264/AAC, full decode
  - PASS preview/blueprint: (854, 480), 1080 frames, 36s H.264/AAC, full decode
  - PASS preview/editorial: (854, 480), 1080 frames, 36s H.264/AAC, full decode
  - PASS preview/terminal: (854, 480), 1080 frames, 36s H.264/AAC, full decode
  - PASS preview/paper: (854, 480), 1080 frames, 36s H.264/AAC, full decode
  - PASS preview/kinetic: (854, 480), 1080 frames, 36s H.264/AAC, full decode
  - PASS preview/cinema: (854, 480), 1080 frames, 36s H.264/AAC, full decode
  - PASS source ZIP: 36 exact files, CRC, media and licenses
- SHA-256 digests (`out/styles/preview/SHA256SUMS.txt`):
  - `film-styles-source.zip`: `c8a25bb9e9a018601ecb0ea8fc14897078510c00d20fa806b4bd52e75184f9bd`
  - `preview/anime/film.mp4`: `8e247313d71a11e5156922bafd567dfc8cc57616e25cf1a9cba24b8cb36b9f2e`
  - `preview/swiss/film.mp4`: `e17f52666cece51c1eeaecc9703fc32202a54a593201ca9924227b40c2926cbc`
  - `preview/blueprint/film.mp4`: `5fb5fda001cfa9a73bec16b2731db5a8a61309f015561edba0df76ec3900519b`
  - `preview/editorial/film.mp4`: `af282a2b875459c95ce99d2cffa28f6e48ca415a2ab4fbeb549392cf7d0ca8b2`
  - `preview/terminal/film.mp4`: `5e0b630623e2266d25643d3e5b169e66b3bf90997a676a98db4fcc6fadd23683`
  - `preview/paper/film.mp4`: `c079251a4f7d76c7ca858834ca97ba9f9ac97635d27b32b5b5dfee4735767cdf`
  - `preview/kinetic/film.mp4`: `2ccac91e2e1eae69a103788187c357ddfe40a9c46f7df584e95121d5bdeaef11`
  - `preview/cinema/film.mp4`: `47a655a55ad3cdec8cf9796b7a8850818c8cbeb98d817571676cb167bd02bef4`
- Delivered artifacts:
  - `vpnrouter-film-preview-fb7d729941e1a3690802a4f9812a013dfaeb5caa` (50,328,407 bytes)
  - `vpnrouter-film-lock-fb7d729941e1a3690802a4f9812a013dfaeb5caa` (16,289 bytes)
- Original deliverables preserved before new rendering: master SHA256
  dce0f3d8bea755941f0e27fa3ee48f7881026cad3746b675c52625b7d1a00b3a;
  720p d7fbb41aeb7e9f3a9b03b12b8251e3a130b515a63578355882a1f4aa21800fc5;
  source 04c71a3f4bc05708f1a82fede433d7e0c1f4f2c9dbe003eebadbe0062416b068.
