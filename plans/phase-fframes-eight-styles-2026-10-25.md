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
2. Tests: PASSED in implementation run 40174456482: 5 Rust tests, 5 Python pipeline tests, and all-frame inspection with empty findings over 1080 frames for each style. Implementation PR CI passed: `test` (4m15s), `characterization-windows` (7m21s), `go-test-windows` (43s), `grep` (6s). Documentation checkpoint 2bcc6c36cda9a9381273fcbf40d752cd56a9cfe6 also passed all four checks; its render job was intentionally skipped because renderer/workflow bytes did not change.
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
5. Visual samples: REVIEWED all eight six-scene contact sheets and representative 16-second frames. PNG/SVG captures exist at 3s, 9s, 16s, 22s, 28s and 34s. Native continuity reports cover 1080 frames per style with empty findings. This is not full subjective motion or listening acceptance; distinct-file browser playback remains NOT VERIFIED (see continuation evidence below).
6. Characterization/integration: PASSED. All 8 preview MP4s match exact 854x480 resolution, 30 fps, 1080 frames, 36.0s duration, AAC audio at -18 LUFS. Standalone source ZIP `out/styles/film-styles-source.zip` contains exactly 36 required files, valid CRC, and byte-identical media and licenses. Full decode test (`ffmpeg -v error -xerror -i film.mp4 -f null -`) passed for all 8 styles with unique decoded pictures. Checksums verified against `out/styles/preview/SHA256SUMS.txt`.

## Material unknowns

- Compilation and native frame/continuity inspection passed at the implementation SHA. Reduced-scale frame/strip clipping notices use upstream pixel dimensions against logical viewBox coordinates; they are not proof of actual clipping and are not called a clean warning stream.
- Full subjective motion/listening acceptance is not proven by samples or full decode. A browser probe repeated anime bytes under eight labels and is rejected as coverage of eight distinct files; the exact-file retry was denied by the browser MCP allowed roots. No file-access bypass was attempted.
- Source-built FFmpeg currently follows upstream release/9.0; the existing
  successful cache is reused, not claimed to be a fully pinned toolchain.

## Outcome

- Status: PREVIEWS DELIVERED; eight rendered previews and the source ZIP have native presentation receipts. Await owner style selection before any 1080p export; the overall preview-first task is not complete. Full distinct-file browser playback and subjective listening remain unverified.
- Baseline: local, remote and PR #623 head all 3a876d67cb779167e8a57ff3a598a818062c5938;
  test, characterization-windows, go-test-windows and grep SUCCESS; render-film
  intentionally SKIPPED on same-repository PR. Worktree was clean.
- Existing fixtures inspected: applications.png and home-connected.png contain
  sample UI, not the repository name. No original image modification is planned.
- Brief commit: 616eec1424b449d2723cd987d2a6ea8d81fffb4d, PR #626;
  Actions run 40171026077 test, characterization-windows, go-test-windows and
  grep run 40171026025 SUCCESS. Exact-head brief gate passed before edits.
- Implementation commit: fb7d729941e1a3690802a4f9812a013dfaeb5caa, PR #626;
  Actions run 40174459335 test (4m15s), characterization-windows (7m21s),
  go-test-windows (43s) and grep run 40174459338 (6s) all SUCCESS.
  Push run 40174456482 render-film SUCCESS (07:52:56-08:10:43 UTC, 17m47s wall time) on a disposable Ubuntu 24.04 runner.
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
- CI artifacts, not native delivery receipts:
  - `vpnrouter-film-preview-fb7d729941e1a3690802a4f9812a013dfaeb5caa` (50,328,407 bytes), downloaded for review.
  - `vpnrouter-film-lock-fb7d729941e1a3690802a4f9812a013dfaeb5caa` (16,289 bytes), published by CI; local download not evidenced.
- Native user delivery occurred during the continuation after 08:32 UTC via `present`, not at checkpoint 2bcc6c36. Successful call receipts: `call_SAN5yIEnie2xNZ4Dp01fpvb6` (anime, swiss, blueprint, editorial), `call_LssEcFrqZSkvsVneVdCdNfez` (terminal, paper, kinetic, cinema), and `call_1JlKZKf4Ti1EgSf4GnNT9G7P` (source ZIP). All paths use `out/review-40174456482/styles/`. The image-generation evidence reader confirms these presentation receipts but reports no image generation; this task uses CI-rendered video, not generated images.
- Original deliverables preserved before new rendering: master SHA256
  dce0f3d8bea755941f0e27fa3ee48f7881026cad3746b675c52625b7d1a00b3a;
  720p d7fbb41aeb7e9f3a9b03b12b8251e3a130b515a63578355882a1f4aa21800fc5;
  source 04c71a3f4bc05708f1a82fede433d7e0c1f4f2c9dbe003eebadbe0062416b068.

## Continuation evidence and remaining gate

- Reconciliation scope: correct evidence labels and the implementation SHA only; do not change renderer/workflow bytes, overwrite original exports, rerender previews or start unselected finals. Check exact local/remote/PR head, scoped diff, normal hooks and exact-head CI after the documentation push.
- Independent read-only reviewer `4e9e0192` found no new acceptance-blocking source defect. Screenshot and focus share their parent transform, opacity, entrance, drift and scale. Actual scene ranges are [0,150), [138,360), [348,570), [558,750), [738,900), [888,1080); overlaps do not shorten the 1080-frame film.
- Limits of that review: the regression samples one fixture/pose over 240 local frames; it is not an all-style pixel oracle. The verifier asserts continuity coverage, not absence of findings, although all downloaded continuity reports actually have empty findings.
- Browser request `preview-40174456482-playback-v1` ran 08:22:08-08:26:57 UTC and reached `ended` in each labelled slot, but all eight slots reported 3071925 bytes. Only anime has that downloaded size; the other seven sizes are distinct. Reject the run as proof of eight-style playback. Exact-file upload for v2 was denied outside the MCP allowed roots, cancelled without bypass, and the owned browser tab was closed. CI full decoding is independent and remains valid.
- Audio measurement reports -18.0 LUFS integrated loudness and -6.9 dBFS peak for all eight files. This is a measurement, not a subjective listening pass. Original CC0 music and all supplied image bytes are retained.
- Before reconciliation, local Git, the remote task branch and PR #626 all matched 2bcc6c36cda9a9381273fcbf40d752cd56a9cfe6. Run 40175985324 passed `test` (4m6s), `characterization-windows` (6m24s), `go-test-windows` (35s); guard 40175985321 passed (6s). Render 40175985328 was intentionally skipped. The collected watcher exited 0.
- Documentation delivery gate: PASS for evidence-backed numbers, scoped facts and explicit limits. Runtime/UI product tests are N/A for this evidence-only correction; source/render evidence remains tied to the implementation SHA.
- Owner amendment after delivery: defer style selection and optimize the existing pipeline, including research into ready-made solutions. No final-resolution run has been requested or launched. The earlier delivery and acceptance limits above remain unchanged.

## Performance amendment - 2026-10-25

### Why and scope

- Reduce actual build/export turnaround, not the verification surface. Reuse this
  task branch and PR #626 because the amendment depends on its unmerged renderer
  and branch-scoped Cargo cache. Accepted starting head: `314e006651a5b5fa61558744859a66cca0fd9213`.
- Own only the style exporter, renderer-local performance evaluator/documentation,
  render workflow and this task/defect record. Preserve Rust scene code, source
  images/fonts/music/licenses, original deliveries and delivered previews.
- Excluded: engine migration, product runtime/root README changes, SDK installation
  on the control plane, merge/release/deployment and unselected 1080p exports.
  Disposable 480p benchmark outputs are test fixtures, not replacement deliveries.
- Risk: MEDIUM, shell/process/CI changes. Rollback: revert each attributable
  candidate commit using its exact SHA; never reset published history or outputs.

### Method and fixed scope

1. Complete two bounded, primary-source research assignments: built-in animation
   APIs/examples and existing build/cache solutions. Record compatibility,
   licenses, uncertainty and an Adopt/Extend/Compose/Build verdict before adoption.
2. Add an immutable evaluator before performance edits. Fix one optimization part:
   `warm-export`, covering build/test invocation, all-eight 480p export, inspection,
   checksums, archive and full consumer verification. Cache/cold-build observations
   are separate diagnostics, not counted as accepted optimization attempts.
3. Use short mode: at most 10 candidate attempts and stop after 3 consecutive
   non-KEEP outcomes. Hypothesis lanes: shared soundtrack preparation, native
   renderer concurrency, and duplicate orchestration work only if fresh stage
   profiles justify them. One attributable implementation change per candidate.
4. Use disposable Ubuntu 24.04 Actions CPU runners at exact SHAs with recorded
   CPU/load/RAM/swap/disk/jobs/toolchain preflight. No build/render/full test on
   the control plane. Confirm the existing cache restoration rather than assume it.
5. Measure a fresh warm baseline and stage profile, then balanced AB/BA comparisons
   on the same runner. Search: three paired all-eight exports, at most one retest
   up to five pairs. Holdout confirmation: fresh all-eight exports with reversed
   pair order, independent warm-up and evidence. Primary metric: complete warm
   export wall seconds; report cold/cache/setup time separately.
6. Keep only quality-passing improvements above both 2% and 1 second, and above
   max(0.5 seconds, 2 * paired MAD). Confirmation also limits peak RSS growth to
   10%. Compare original-versus-combined implementation freshly before completion;
   with one fixed part its confirmation is also the overall E2E comparison.

### Quality and negative controls

- Keep all existing Rust/Python checks, 1080-frame inspection/continuity coverage,
  full H.264/AAC decode, dimensions/fps/duration/title checks, eight distinct
  decoded pictures, checksums and exact source/media/license ZIP fidelity.
- Lock the existing consumer checker separately from candidate code. Before its
  lock, run mutation controls for corrupt checksums, wrong dimensions/title and
  duplicate/invalid style selection. Archive mutations must be rejected too.
- Hash protected Rust/media/license files; no candidate may silently change them.
  Require full decoded video and PCM audio equality against contemporaneous
  control outputs, not just unique container hashes. Subjective browser motion
  and listening acceptance remain outside this performance claim.

### Six amendment gates

1. Build: PENDING exact-SHA locked release build on CI; product build N/A.
2. Tests: PENDING Rust/Python suite, evaluator negative controls and full exports.
3. Documentation: brief recorded before implementation; research/outcome pending.
4. Independent review: PENDING correctness, test/evidence and process/CI lenses.
5. Product UI/WINBRAT: N/A, no product/UI/installed-app changes.
6. Integration: PENDING paired raw measurements, quality/resource gates, fresh
   confirmation, native report delivery, exact remote head and green PR checks.

### Current observations and unknowns

- Historical run `40174456482`: cold release compilation 14m47s; export/validation
  after tests about 134s; per-style rendering 6.1-7.7s. These are not fresh pairs.
- Cache `9470884563` exists on this branch (965244878 bytes); its restore and the
  next build duration have not been measured. Do not claim a cache speedup yet.
- Export lines 51-56 repeat identical soundtrack filtering/AAC encoding per style.
  Reusing one prepared track is a hypothesis, not an accepted speed improvement.
- Existing FFmpeg source build avoids the proven x264 ABI mismatch; preserve it.
  Mutable upstream release/9.0 remains a reproducibility limitation.
- Research decision recorded in renderer `performance/solutions.md`: extend
  existing fframes primitives and actions/cache; no new animation/cache dependency.
  Crates registry corrected Animato 1.7.3 to 2026-10-21 and Keyframe 1.1.1 to
  2022-07-11. Native FFmpeg make uses num_cpus, not Cargo's two-job limit; reject
  the research worker's unmeasured 2x/7-8 minute extrapolation.
- Brief commit `6560d51d248e7e13c196614c872cf9a8ee020b13` passed exact-head
  test/characterization-windows/go-test-windows/grep; render intentionally skipped.
- Prelock review `a3282f60`: fixed explicit comparison control, current-head
  renderer equivalence, performance artifact labelling, preview-only interception
  and unconditional scoped temporary-worktree cleanup. Worktrees are outside the
  uploaded evidence tree. An arbitrary explicitly named accepted candidate is
  permitted only with source equivalence to current head, needed for independent
  confirmation without changing candidate identity.
- Rejected review claims: copied checker direct CLI is not a supported entrypoint;
  `benchmark.py` supplies its exact consumer root. Baseline samples intentionally
  use the search workload; warmup and instrumented profile are excluded and saved
  separately. No fake separate baseline workload is added.
- Correctness review `edc7b653` partly read the earlier working-tree snapshot:
  shared targets, in-output worktrees and missing pairs forwarding are superseded.
  Confirmed archive missing-input weakness is logged; evaluator checks required
  source entries before invoking the frozen checker. Failure collection now saves
  failure.json and keeps logs outside scratch. Worktree-root calculation has an
  explicit fixture test and deletion target assertions.
- Rejected PS4-freeze claim by running a three-command fixture: timestamps
  1792928887.556033/.556084/.556096 advance per line with the current PS4. Added
  dynamic timestamp regression. Do not escape the expression into literal text.
- Statistical decisions/failure budgets use the existing research_state.py helper
  on downloaded immutable receipts; do not build a duplicate comparator or call
  CI collection success a KEEP. All three independent reviews are collected.
- Evaluator implementation, campaign ledger and final outcome: PENDING CI.

### Baseline collection repair

- Owner explicitly approved temporary all-eight 480p CI benchmark fixtures in
  this continuation. Do not replace delivered previews or start 1080p finals.
- Actions `40190544592` at `9ca1dd6b67740924a8d4735f556133b60e046d1c`
  failed before Python, compilation or measurement: the YAML-extracted Bash
  retained two spaces before the Python heredoc body and closing `PY`.
  `bash -n` reproduces exit 2; YAML parsing alone missed the error.
- Cache restoration succeeded (965244878 bytes; 11 seconds). This is not an
  export/build speedup. All four product PR checks passed at that head.
- `warm-export-v1` is cancelled by an append-only status record with hashes of
  its Actions log/run receipt. Helper status is `concluded`, no baseline/current
  best and zero candidate attempts. Its evaluator files and Git lock remain
  unchanged; there are no invented measurement or passed-quality receipts.
- Repair workflow shell syntax, failed-collection receipt ordering and their
  lightweight regressions. Freeze a separate linked `warm-export-v2` evaluator root
  with the repaired workflow hash; establish a fresh lock and baseline before
  any exporter candidate. Keep the existing 10/3 budget and quality thresholds.
- Verification: isolated old-source red/new-source green `bash -n`, embedded
  Python parsing, evaluator contracts/negative controls, hashes, scoped diff,
  independent review and exact-head CI. No local build, render or media decode.
- Recovery changes are not optimization attempts. The only optimization part
  remains `warm-export`; replacing a pre-baseline broken evaluator does not reset
  any consumed candidate budget (zero so far).
- Local recovery checks: ten lightweight contracts pass, including eight real
  checker mutation controls. Old YAML-extracted shell reproduces exit 2; every
  repaired YAML-extracted run block passes `bash -n`, embedded Python parses,
  and mocked baseline/search/confirmation dispatch forwards exact SHAs and pair
  counts while rejecting a missing comparison control before execution.
- V1 evaluator bytes, Rust/media/licenses and exporter bytes match the old lock.
  V2 checker/protected inputs are byte-identical to v1; policy changes only its
  protected root. Runner receipt publication now follows successful output copy
  and worktree cleanup. Synthetic copy/cleanup failures publish no passed receipts;
  success-path coverage is not a real measurement. Seven-file manifest coverage and
  repaired workflow hash pass. No compiler, render or media decoding ran locally.
- Independent read-only review `a1198fd7` verified actual YAML extraction,
  old-source red/new-source green contracts, stubbed routing/failure propagation
  and exact workflow hash without build/render/decode. Review `d03cacb3` confirmed
  nested evaluator root/identity/cleanup and found premature passed receipts;
  source-confirmed and repaired only in unfrozen v2. Follow-up `4dfc643b` is clean:
  independently ran seven evaluator contracts, reproduced both failures against
  frozen v1 in memory, checked v1 cancellation evidence and exact v2 manifest.
  The unchanged three workflow contracts retain `a1198fd7` coverage. These reviews
  do not establish real CI export or performance results.
- Recovery commit/evaluator lock: `ac5ac03c4475c32dea13738e5941db00115c8da6`,
  exact local/remote/PR head. V2 initialized with `previous_campaign=warm-export-v1`;
  helper verifies seven locked files. Four product checks passed at that SHA.
- Actions `40192719205` ran 9m17s, passed workflow contracts and capacity/toolchain
  preflight, restored cache and completed warmup/profile/sample-0 exports. Each
  batch passed existing media/archive checks, but decoded output equality failed
  against warmup before a baseline measurement/quality receipt was published.
  Warmup/profile seconds are diagnostics, not a valid baseline or speedup.
- V2 remains frozen; a blocked collection status records original failure
  artifact hashes and zero candidate attempts. Failure logs lack per-style signatures,
  so differing stream/style and mechanism are still unknown.
- Next recovery scope: a task-owned diagnostic CI workflow checks out the exact
  V2 lock and runs two temporary all-eight 480p exports, saving each output set,
  per-stream hashes, per-frame video hashes, repeated decoder hashes and FFmpeg
  version. This distinguishes unstable rendering from unstable decoding without
  changing the locked evaluator or weakening strict decoded equality. The
  diagnostic job is not a baseline/candidate and creates no passed measurements.
  Reuse existing preflight/cache/build settings; never decode/render locally.
  Review `abe2a4a4` found failed-batch evidence could be lost before copy; fixed
  with finally-path preservation. Actual extracted-loop synthetic export/inspect/
  decode failures propagate while preserving available output; success retains
  both batches. YAML/Bash/Python parsing passes. Preserved failed batches may
  contain partial/stale files and must not be accepted as passed exports.
- Read-only diagnosis `13b46a1e` found dynamic frame stealing despite fixed
  encoded segments; worker-local render caches have timing-dependent histories.
  This is a hypothesis, not a proven source of the failed hash. Parallel font
  registration is another source path, but no fallback warning is observed.
  Changing Rayon threads alone would confound both paths and is not adopted.
- Diagnostic checkpoint `5cccb4688804b0788b7b1345b88f891ee525af38` is exact-head
  green (four product checks, diagnostic success, normal render intentionally
  skipped). Diagnostic Actions `40194340852` completed in 5m49s at exact source
  `ac5ac03c4475c32dea13738e5941db00115c8da6`. Downloaded container hashes match.
- Two all-eight batches show stable audio and stable same-file repeated decoding.
  Cross-export video differs for swiss (47/1080 frames), editorial (251/1080) and
  kinetic (24/1080). Their silent-video hashes differ too; encoded packet payloads
  differ while packet timing matches. This localizes the observed failure before
  final soundtrack mux, not to a proven rasterizer/cache defect.
- Next controlled diagnostic changes only `FFRAMES_NUM_THREADS=1`, keeping
  `RAYON_NUM_THREADS=2`, exact source, workload, media and checker unchanged.
  Published fframes `renderer.rs:342-350` reads that native override for CPU
  backend concurrency; no temporary Rust adapter or upstream patch is needed.
  Run the same two batches and strict hashes. This tests rendering concurrency
  without serializing parallel media/font loading. It is not a speedup candidate.
- Fresh baseline, paired optimization and confirmation remain pending.
