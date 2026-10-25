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

1. Build: BLOCKED pending exact-SHA Actions release build of the isolated crate.
   Product solution build N/A because no product code/project changes.
2. Tests: BLOCKED pending Rust tests, all-frame inspection for every style,
   screenshot-registration regression, no-authored-repository-text check,
   timeline/metadata/full-decode assertions and existing product PR checks.
3. Docs: BLOCKED pending tracked instructions, owning README and final Outcome.
4. Review: BLOCKED pending independent correctness, content/layout and export
   reviews, source verification and defect dispositions.
5. UI: BLOCKED pending exported frame/contact-sheet review for eight styles.
   Live product/WINBRAT scenario N/A; this task never ships or controls the app.
6. Characterization/integration: BLOCKED pending eight unique full exports,
   36 s / 1080-frame contract, original asset byte checks, source ZIP CRC and
   checksum comparison. Mechanical product split characterization N/A.

## Material unknowns

- Rust compilation, renderer warnings and visual composition remain unverified
  until the new exact-SHA export. A successful original export is only a baseline.
- Subjective listening/browser playback is not proven by ffprobe or full decode.
- Source-built FFmpeg currently follows upstream release/9.0; the existing
  successful cache is reused, not claimed to be a fully pinned toolchain.

## Outcome

- Status: IN PROGRESS; source implementation and static checks, no new export yet.
- Baseline: local, remote and PR #623 head all 3a876d67cb779167e8a57ff3a598a818062c5938;
  test, characterization-windows, go-test-windows and grep SUCCESS; render-film
  intentionally SKIPPED on same-repository PR. Worktree was clean.
- Existing fixtures inspected: applications.png and home-connected.png contain
  sample UI, not the repository name. No original image modification is planned.
- Brief commit: 616eec1424b449d2723cd987d2a6ea8d81fffb4d, PR #626;
  Actions run 40171026077 test, characterization-windows, go-test-windows and
  grep run 40171026025 SUCCESS. Exact-head brief gate passed before edits.
- Owner asked where rendering runs and proposed 480p/360p selection first.
  Adopted 854x480 preview-first on disposable Ubuntu 24.04 Actions CPU;
  explicit selected finals rerender the logical SVG canvas at 1920x1080.
- Added source dispatch for eight styles, shared screenshot group, two licensed
  display fonts, preview/final export split and tracked zone instructions.
- Static checks: bash syntax, Python AST, YAML parsing, ASCII documentation,
  local doc links and diff whitespace PASS. rustfmt absent; no local compile,
  SDK installation, rendering or full test suite was attempted.
- Measured primary text/background palette contrast: minimum 4.73:1 for small
  labels and minimum 5.26:1 for large accent text. Actual composite frame review
  remains pending; these ratios do not prove every overlaid pixel contrast.
- Original deliveries preserved before new rendering: master SHA256
  dce0f3d8bea755941f0e27fa3ee48f7881026cad3746b675c52625b7d1a00b3a;
  720p d7fbb41aeb7e9f3a9b03b12b8251e3a130b515a63578355882a1f4aa21800fc5;
  source 04c71a3f4bc05708f1a82fede433d7e0c1f4f2c9dbe003eebadbe0062416b068.
- Independent visual/content review 24b472df and export/security review 1e71104e
  returned. Source-confirmed spacing, duplicate SVG id, unused copy, test import
  and artifact-mode naming findings fixed and recorded in OPEN-DEFECTS.md.
  Duplicate stale title/contrast/README findings refuted against the current
  snapshot. Cyan/green focus annotations intentionally match fixture controls;
  cinema captions are outside-picture metadata. Correctness review still running.
- Source ZIP includes original license content with LF/trailing-space formatting
  normalized; display font bytes remain identical to upstream reference.
- CI build/tests/new exports/delivery pending.
