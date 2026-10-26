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
- Controlled diagnostic `40195226933` at checkpoint
  `e1f9d5650c8b9a4701a64647a9399bf247f252ad` completed successfully on exact
  `ac5ac03` source with native `FFRAMES_NUM_THREADS=1` and unchanged Rayon=2.
  It still failed cross-export equality: anime 72/1080, swiss 3/1080,
  editorial 72/1080 and paper 24/1080 video frames differ. Other styles match;
  all audio and same-file redecodes match. Each differing style also differs
  before soundtrack mux. Collection success is not repeatability acceptance.
- Exact local/remote/PR checkpoint and all four product checks are green; normal
  render intentionally skipped. Diagnostic artifacts and watcher were collected.
  All seven frozen V2 files verify; helper remains blocked with no baseline,
  no current best and zero candidates. Delivered previews remain unchanged.
- Next recovery: inspect saved frame/packet ranges and the exact crate source,
  then choose one discriminating raw-input-versus-encoder diagnostic. Do not
  repeat thread-count tuning, change frozen inputs or relax decoded equality.
  Any required reproducibility/evaluator correction needs a reviewed linked
  replacement lock and fresh strict baseline before orchestration candidates.
- Recovery checkpoint `ab0c595d12aebd14e610c39623acea3b4c5e2a9e` is exact-head
  green: test 3m26s, characterization-windows 7m8s, go-test-windows 42s,
  grep 5s; normal render skipped. Current-run notes now match collected evidence.
- Saved text forensics show many differences ending at fixed 24-frame segment
  boundaries. All twelve sparse PNG/SVG captures per affected style match, which
  does not prove equality of stateful full-render input frames. Both delegated
  forensic jobs failed; the partial source response's x264-lookahead root-cause
  assertion is rejected: no raw inputs were compared, and x264 enables its
  deterministic mode by default. Cache correctness is not established either.
- Bounded next diagnostic: use a transparent CI-only LD_PRELOAD x264 input probe
  to hash visible YUV planes immediately before the real encoder call, retain
  actual encoder thread/determinism settings, and compare two unchanged-source
  all-eight 480p batches against their decoded video hashes. Production Rust,
  media, encoder options and frozen evaluators remain byte-identical. Require
  exact frame/PTS coverage and two complete probe traces; a pair with no video
  difference cannot localize the original failure. The probe is not a candidate
  and its timings cannot become baseline receipts. Keep compilation/decode on CI.
- Local diagnostic contracts: 4/4 pass; existing frozen evaluator/workflow
  contracts: 10/10 pass. Extracted shell syntax, embedded Python AST, YAML,
  protected source hashes and diff checks pass. CI-only C compile, transparent
  three-frame smoke and two complete real input traces remain pending.
- Independent reviews `0c76f81b` (ABI/correctness) and `7b02efc6`
  (security/isolation) found no remaining blocking source defect. Reviewer
  `121f584f` verified failure-path preservation and found two conclusion gaps:
  equal-video raw differences and unstable same-file decoding. Both are recorded
  and fixed with regressions. Follow-up `46c709ea` confirms both resolved:
  four contracts and 34 independent synthetic evidence cases pass. Observer
  timing perturbation and unobserved x264 metadata remain explicit limits.
- Diagnostic implementation `8ea38c0c121b3afa993fd23e236284a11d98294e`
  passed exact-head PR checks. Actions `40198389530` completed in 10m55s;
  both watchers and artifact collection exited 0. Artifact `12601525490`
  (`vpnrouter-repeatability-40198389530`, 107751176 bytes) was downloaded.
- Recomputed analysis from all 16 real traces: every style/batch contains exactly
  PTS 0..1079 and 45 encoder-open lifetimes of 24 frames. Pointer reuse between
  encoder lifetimes is valid. Visible Y/U/V input hashes, all decoded frame rows,
  audio, silent video and final/silent container bytes match between batches.
  Downloaded media hashes match the saved runner hashes. Actual segment settings
  are threads=1, lookahead_threads=1, sync_lookahead=0, deterministic=1.
- The original failure did not reproduce in this instrumented pair. This is not
  repeatability recovery, a baseline or evidence excluding the renderer/encoder.
  Per-frame EVP allocations, hashing and synchronous trace flushing perturb the
  process. Existing uninstrumented failures remain valid; V2 stays blocked with
  no baseline/current best and 0/10 candidates. Local verification only parsed
  saved traces and hashed file bytes; no local build/render/decode was run.
- Next bounded diagnostic: remove per-frame EVP heap allocation from the CI-only
  hook, retain exact SHA256 visible-plane coverage and byte-transparency smoke,
  and compare one uninstrumented pair with one reduced-allocation instrumented
  pair on the same runner/source. Record frame scalar metadata/strides if needed
  to separate identical visible pixels from hidden input state. A matching pair
  remains inconclusive. Do not alter production Rust/media/encoder options or
  either frozen evaluator. Test real extracted shell/Python, malformed traces,
  failure preservation and probe smoke; independent source review before push.
  Heavy work remains exact-SHA disposable CI. Revert only the exact diagnostic
  change if invalid; preserve all old evidence and delivered exports.
- Reduced-allocation diagnostic local checks: 6 diagnostic, 7 frozen evaluator
  and 3 frozen workflow contracts pass, including the actual extracted loop with
  plain/traced pairing, preload removal and export/inspect/decode error injection.
  YAML/Bash/Python parsing, scoped ASCII additions and diff checks pass. All seven
  frozen V2 files verify; helper still reports blocked, 0 candidates/3 records.
  All eight delivered preview MP4s and source ZIP retain their original hashes.
- Independent source review `5a30fc47` found no confirmed blocking defect and
  independently passed the 16 contracts plus 13 all-eight synthetic workflow
  scenarios, including every failure stage in every batch. Stack SHA256 lifetime,
  actual API calls, unchanged row bounds, pair identity and cleanup were checked.
  Real C compile/transparency smoke and the four-batch diagnostic remain CI-only;
  hashing/flush/stack-layout effects and unobserved padding/metadata remain limits.
- Security/test review `54a119af` independently passed all 16 contracts and a
  45-segment trace fixture, confirmed preload/path/permission isolation and no
  new acceptance receipts. No confirmed blocking defect was found. Failed batch
  artifacts can still contain stale prior-style files, as previously recorded;
  they are not accepted exports. No product or frozen source changed.
- Reduced-allocation head `2a97a1dda49e093f92feaf3d2a36fbcf625021eb` is exact
  local/remote/PR head with all five applicable checks green; normal render skipped.
  Actions `40201533838` completed in 12m55s. Watcher and artifact collection exited
  0. Artifact `12602696265` (213651298 bytes) has verified archive SHA256
  `9fed114cdd72e290e37b7dded5cd57d6e172e148a9702a8596211e64a2f7afe0`.
- Recomputed both reports from saved signatures and all 34560 decoded frame rows;
  verified 64 media byte hashes against runner records and the nested checksum
  manifests. All 16 traces have 45 open lifetimes with monotonic 24-frame ranges
  and exactly PTS 0..1079. The uploaded archive omits empty plain trace directories;
  the runner explicitly asserted them empty before upload. No local decode ran.
- Both pairs reproduce video differences. Traced batches 0/1: anime 168, editorial
  389, paper 24 frames. Plain batches 2/3: anime 120, blueprint 24, editorial 24.
  Other pairwise styles match. Audio and same-file repeated decodes are stable;
  silent video already differs. All 1080 visible Y/U/V input hash triples match
  across traced batches for every style, with actual settings 1/1/0/1 unchanged.
  This narrows the reproduced failure past observed visible samples, but does not
  exclude hidden input state, mutation after observation, encoder or downstream
  packet handling. It does not prove an x264, padding, cache or lookahead cause.
- Independent saved-byte audit `d7401b6a` verifies 142592 packet payload hashes
  against positions/sizes in all 32 final MP4s, with zero mismatches. Traced packet
  differences: anime 106, editorial 249, paper 24; plain: anime 70, blueprint 24,
  editorial 7. Pairwise packet timing/order/flags and saved extradata hashes match.
  This is real encoded-payload divergence, not merely changed container offsets.
  Editorial also has changes within segment 600..623 while keyframe 600 matches;
  do not generalize the first changed segment's keyframe alignment to all changes.
- Next bounded diagnostic: retain the current full input observer, exact source,
  encoder options and plain/traced/traced/plain order. Add SHA256 of returned NAL
  payload bytes immediately after each real encode call (including NULL flush)
  and encoder-header call, with returned byte count, NAL count and output PTS.
  Record encoder close and distinguish each open lifetime despite pointer reuse.
  Compare complete 1080-output-PTS maps and per-lifetime header/output structure.
  No further hot-path minimization is justified merely to reproduce: it reproduced.
- Allowed implementation scope: CI-only hook, analyzer, smoke/tests and diagnostic
  workflow plus this record and existing defect entry. Preserve source/media,
  padding, encoder arguments, frozen evaluators and delivered previews. Header and
  library version/hash evidence may be retained from the same disposable runner.
  No new dependency, production fix, baseline acceptance or optimization candidate.
- Next-step acceptance: real CI compile; encoded-byte transparency plus exact input
  and NAL-output smoke; reject missing/duplicate/out-of-range PTS, incomplete
  lifetimes, failed calls and missing close/flush/output evidence; exercise actual
  workflow success/failure paths. Run lightweight syntax/regression checks locally,
  independent correctness/security review, normal-hook commit/push and exact-head
  CI. Build/render/decode stay on the recorded disposable runner. If output and
  headers differ, inspect hidden inputs/encoder state; if equal while decoded video
  differs, inspect downstream handling without claiming an established root cause.
  A non-reproducing pair stays inconclusive. Roll back only this diagnostic commit
  with history preserved if invalid. Fresh strict baseline is still required.
- Output-boundary local checks: 7 diagnostic, 7 evaluator and 3 workflow contracts
  pass without native compilation/render/decode; source hashes, YAML, extracted
  Bash/Python and seven-file scope verify. Reviewer `c7070336` found lost relative
  header/encode order in the precommit analyzer. Lead reproduced it, recorded it
  in the defect ledger, retained combined per-lifetime event sequences and added
  an actual-analyzer moved-header regression. Independent correctness `63daa067`
  and security `fc91bbeb` reviews found no further confirmed blocker and reran all
  7 diagnostic contracts after the correction. Test review `c7070336` independently
  exercised 29 parser, 24 conclusion, 9 extracted-smoke and 4 correction scenarios;
  it ran a no-write 7-contract subset, not the full 17-contract suite. The lead's
  full 7+7+3 rerun passes. The minor malformed-setting domain gap is recorded and
  deferred until the exact CI header establishes legal auto sentinels. It is not
  baseline acceptance. Native ABI, smoke/transparency and real outputs remain
  unverified until the next disposable exact-head CI. All nine delivered preview/
  archive files retain their original checksums; no rollback or product change.
- Output-boundary checkpoint `44c74615aa4d89c0e6b01036415eeebeb61be92e` is
  exact-head green: test 3m18s, characterization-windows 7m12s,
  go-test-windows 35s, grep 4s, diagnose 12m12s; normal render skipped.
  Actions `40205357873` compiled the CI-only hook and passed encoded-byte
  transparency, complete-lifetime, visible-plane and native-NAL SHA256 smoke.
  Watcher `bash-5b5bf7198c244d4a` and collection `bash-7d5c0aaa43b24b58`
  completed with exit 0. Artifact `12603573609` (214572206 bytes, 793 entries)
  has verified SHA256
  `a4f1b434aa8be66a376d6bb03105bc3de95d47def1cdbc4d0d3cb8ce7aca346e`.
- Saved-evidence verification checked 64 media hashes and 34560 decoded rows.
  Traced video differences: anime 24, blueprint 48, editorial 261 frames;
  plain controls: anime 24, editorial 261, kinetic 144. Other styles match.
  All 16 traces have equal visible inputs and complete 45x24 closed lifetimes,
  matching headers, recorded settings 1/1/0/1 and output-PTS/flush sequences.
  Returned native NAL hashes differ at anime 7, blueprint 22, editorial 132 PTS.
  This establishes native-output divergence before MP4 assembly, not its cause.
- Independent audit `202a125e` parsed the raw TSVs without the analyzer: 720
  closed lifetimes, 17280 inputs/outputs, 35280 calls and 18000 NULL flush calls.
  It verified 26736 actual MP4 packet-byte hashes in the six changed-style films.
  Every one of 6480 video packets reproduces the native return/count/hash/lengths
  after separating SEI and converting 4-byte NAL lengths to Annex-B prefixes;
  zero mismatches and exactly equal native/packet divergence PTS sets. Report:
  `out/repeatability-analysis/native-result-audit-40205357873.json`, SHA256
  `a42ac8de3819d1d5f6355c7ff1390ea7bab3b6c46d9ed756c6a5615a2f5a27f8`.
- Equal visible hashes are not equal full picture controls, full effective params,
  unseen storage or encoder state. Exact retained CI header matches upstream
  `31e19f92f00c7003fa115047ce50978bc98c3a0d`; this is not proof of exact compiled
  source or absence of distribution patches. No API violation, padding cause,
  x264 defect or data race is established. No new local compile/render/decode.
- Next bounded scope, recorded before implementation: extend only the CI observer,
  analyzer, smoke/contracts and diagnostic workflow to fingerprint named scalar
  effective x264 parameters and CQM arrays before each real encode/flush call.
  Record source-supported picture inputs (type, forced QP, pic_struct, image
  descriptors/strides) and optional control-pointer presence/counts without
  dereferencing unknown pointer payloads. Record read-only x86 FP control modes,
  excluding sticky exception status. Never hash raw struct storage or addresses.
- Compare state by segment and call, retain full visible/native-output evidence and
  the plain/traced/traced/plain controls. Require complete new state coverage in
  this run while keeping historical trace parsing available. A differing semantic
  field identifies an omitted interface difference, not causality. Equal named
  state narrows only those observed fields; present optional payloads remain
  explicitly incomplete. A non-reproducing pair remains inconclusive.
- Saved x264 `common/frame.c:392-468` consumes picture controls and visible I420
  dimensions, followed by internal mod16 edge expansion (`encoder.c:3372-3377`,
  `common/frame.c:640-665`). This does not prove SIMD access footprints or caller
  padding consumption. Do not read unproven backing bounds, normalize/zero padding,
  replace input buffers, alter codec settings or change production/evaluator bytes.
- Acceptance: malformed/missing/duplicate/state-order controls fail the actual
  analyzer; optional state cannot masquerade as complete input equality; extracted
  workflow success/failure and transparency smoke remain covered. Run focused
  local parsing/contracts only, independent review, normal-hook commit/push,
  exact-SHA CI native compile/smoke and collect/recompute all real results. Roll
  back only this diagnostic checkpoint with a revert if invalid; retain evidence.
- Independent source analysis `2d93a7a3` found no concrete reachable uninitialized
  read or API violation. It recommends one 24-frame capture/replay as the next
  causal test once the full capture contract is known: exact binary/importer
  footprint, original striding/alignment, parameters and delayed auxiliary-pointee
  ownership. The named-state run is narrower observation, not that causal proof;
  its optional-payload presence and actual descriptors supply missing facts needed
  to bound a safe capture. Do not treat CPU-independent as disabling SIMD.
- Named-state local checks: 10 diagnostic, 7 evaluator and 3 workflow contracts
  pass; actual extracted smoke tests cover success, missing state, bad picture
  control and bad native hash. Historical raw-output deltas still recompute as
  7/22/132. Frozen source/exporter/assets/evaluator bytes verify unchanged.
  All 17 delivery-manifest hashes verify (including the nine delivered MP4/ZIP
  files). Test/evidence reviewer `e36ab6b8` found a new event-only conclusion
  incorrectly claiming native payload divergence. Lead reproduced it, recorded
  FFRAMES-NAMED-STATE-OUTPUT-CLAIM, split payload versus event/settings branches
  and added named-state ordering coverage alongside the legacy regression.
  The same reviewer found native scalar-width overflow acceptance; lead reproduced
  it, enforced 32/64-bit bounds with boundary tests and a C width assertion, and
  recorded FFRAMES-NAMED-STATE-NATIVE-WIDTH. These are diagnostic-only corrections,
  not semantic x264 sentinel validation or baseline acceptance. The lead's final
  11+7+3 suite passes; reviewer `e36ab6b8` independently confirms both reproducers
  fixed and 11 diagnostic contracts pass. Both final reviewers `4e07fbdf` (ABI,
  correctness/lifetime) and `e36ab6b8` (test/evidence/security) found no remaining
  confirmed blocker and matched all 161 parameter names/types to the retained
  ABI-164 header. The latter also exercised real CLI legacy/named success and
  strict missing/partial/duplicate/invalid/order failure paths without passed
  reports. Native compile/transparency and real-state CI remain pending.
- Review limits retained: inactive-field initialization in the actual FFmpeg caller
  is not proven by available source; equal or differing inactive fields are not
  causal evidence. The pre-call parameter query does not observe parameters applied
  later during encode or the contents/lifetimes of optional pointers. FP modes are
  from the calling thread at entry, not internal encoder workers or the whole call.
  Extra trace/query/hash overhead can perturb execution. No input normalization.
- Fresh baseline, paired optimization and confirmation remain pending; 0/10
  candidates, no current best, no accepted improvement and no rollback.
- Named-state checkpoint `600daaa2153bd1922878a84de1815db0d74d53fe` is exact
  local/remote/PR head and green: diagnose 21m03s, test 4m04s,
  characterization-windows 6m10s, go-test-windows 49s, grep 6s; normal render
  intentionally skipped. Actions `40209722318` completed successfully, including
  native compilation and encoded-byte-transparency smoke. Watcher
  `bash-86985b15d7d44b6f` and collection `bash-983f16646d704354` exited 0.
- Artifact `12605741457` is 216352367 bytes with 793 entries; archive SHA256 is
  `344f65837ce259c8a687cea115c71969d2e5bf192e95c70831689b79dc8eaec1`.
  Saved verification recomputes strict named-state/native analysis and decoded
  summaries, validating 64 media hashes and 34560 decoded-frame rows. Use
  `out/repeatability-40209722318/final-actions-run.json` for final status; retain
  the earlier in-progress receipt as history, not the current result.
- Traced decoded differences are anime 48, editorial 216 and kinetic 96 frames;
  plain controls differ for anime 188 and editorial 48. The other styles match.
  All visible YUV and recorded named state match, while returned native payloads
  differ for the three traced styles. Actual optional `rc.psz_stat_in` and
  `rc.psz_stat_out` pointers are present, with both stat-read/write flags zero;
  pointer contents were not dereferenced. Exact native differing PTS are in
  `out/repeatability-analysis/named-state-40209722318-verification.json`.
- Complete semantic input equality remains unproven. Inactive caller fields,
  delayed application/mutation, exact importer footprint and internal worker
  state remain limits; no x264 defect, padding cause, API violation or race is
  established. Native compilation passing is not repeatability or baseline
  acceptance. The mutable current-run record is reconciled to completed/collected.
- Next decision is bounded to establishing capture prerequisites from the saved
  actual caller, retained header and native source/package evidence. Two disjoint
  read-only assignments inspect caller ownership/initialization and native importer
  selection/footprint. Do not rerun the completed export or implement a 24-frame
  replay until capture bounds and semantic ownership are supported. Production,
  frozen evaluators, encoder settings, padding and delivered outputs stay fixed.
- Capture-prerequisite decision: reject both advisors' unsupported SIMD/root-cause
  and complete-input-equality claims. The actual render path uses `FramePool` and
  `RgbaFrameConverter` (`frame_export.rs`), not the older `EncoderFrame` path.
  There are 45 closed encoder lifetimes per style/batch, not two. Disabled stat
  read/write and absent NAL callbacks are already observed; changing
  `b_cpu_independent` or hashing unproven full strides is not selected.
- Next implementation scope: one provenance-only CI workflow and a dependency-free
  Python collector/contracts under `.github/native-provenance/`, plus this task
  record. Restore the existing exact-source Cargo cache read-only; do not build,
  execute the cached renderer, render, decode or rerun all-eight exports. Retain
  the actual cached FFmpeg wrapper/allocation sources, Git/source/build/object/
  library hashes, Cargo fingerprint/link evidence and runner x264 package/library
  identity. Preserve ambiguous cache entries as unverified, never active by fiat.
- Verification: bounded allowlisted files, path/symlink confinement, missing/ambiguous
  cache and subprocess failures, manifest/hash binding, actual extracted workflow
  shell/Python, independent correctness/evidence review and exact-head CI. Missing
  source/cache is a diagnostic blocker with retained evidence, not a reason to
  rebuild or accept a baseline. No new dependency or source/configuration fix.
  Risk: low, read-only diagnostic collection. Rollback only this checkpoint by
  revert; retain evidence. Runtime/UI/product gates N/A for this no-render step.
- Provenance-only local verification: 9 new collector, 11 unchanged diagnostic,
  7 frozen evaluator and 3 frozen workflow contracts pass without native work.
  Actual workflow YAML/extracted Bash and Python AST parse. Real-Git fixtures
  cover absent native metadata and same-size modified files with cached helpers;
  neither parent HEAD discovery nor helper execution is allowed. Command failures,
  timeouts, bounded partial output, input changes and incomplete sources/build/
  caller evidence fail closed while retaining a manifest.
- Review `6b1a3056` confirmed missing-source acceptance and parent Git discovery;
  both are corrected. Review `95a47274` reproduced cached clean-filter execution
  through native `git status`; status/diff were removed, leaving only explicit
  metadata revision lookup with worktree cleanliness unverified. It also found
  hidden checksum upload exclusion; scoped hidden-file inclusion now preserves
  the allowlisted `.cargo-checksum.json`. Lead checked the pinned action input.
  All four findings are recorded in the defect ledger before correction.
- Final correction review `38c732f7` verified collector fixes and 9 passing tests
  but repeated the hidden-file finding against the same workflow hash that
  already contains the input at line 94. Lead parsed that actual input as true;
  independent focused review `1bd2acb1` read the same files, reran all 9 tests
  and confirms the known exclusion mechanism closed. No remaining source-confirmed
  blocker; real cache/package/readelf/upload evidence is still pending exact CI.
  Cache entries remain explicitly unbound to the active historical executable;
  zero optimization attempts is separate from the number of cached candidates.
- Provenance run `40215299149` at `4e843ffcb03890ff60baa0757a1698e4d4d00484`
  failed its collection gate, not cache restore. Artifact `12607426070` is 1148212
  bytes, 52 entries, SHA256
  `4b288e9763792355ddf757ba1c913ed2dc8eccb77ec5c02f516c241f8030858c`.
  All 50 copied files match manifest hashes; 12 object/library/renderer entries
  are hash-only and were not uploaded. All seven recorded commands succeeded.
  Two native candidates have HEAD `6882969ce9fd2aa6aa1d0008b2bfe1c838aac11d`;
  only candidate 0 has the required object/build evidence. Neither is bound to
  the historical active binary or proven worktree-clean.
- The decisive missing caller evidence is `fframes-0/.cargo-checksum.json`, absent
  before upload; scoped hidden-file inclusion worked and did not cause this loss.
  The separately saved fframes 1.3.0 crate has the Cargo.lock SHA256
  `24719b199dd9da6c5d8aed3fa50bd4bc89af1b3dc9e096a6472cf774634ee0c0`, and
  all six copied caller/source files match its regular archive members exactly.
  Record FFRAMES-NATIVE-PROVENANCE-CRATE-IDENTITY before correction. Watcher
  `bash-233a4c1949374b1a` exited 1: inspect-native failed; test, characterization-
  windows, go-test-windows and grep passed; normal render was intentionally skipped.
- Bounded correction scope: use the existing cached fframes crate under the same
  registry cache, verify its compressed SHA256 against the exact checkout's lock,
  and compare each required allowlisted caller file to one unique regular tar
  member without extracting or executing it. Collect generated checksum metadata
  when present but do not fabricate it. Missing/mismatched archive or caller files
  must retain failure evidence and fail the acquisition gate. No network fallback,
  native compilation, renderer execution, render/decode, source changes or new
  dependency. This is provenance collection, not repeatability recovery.
- Verification for this correction: actual-collector red/green fixture with absent
  generated metadata and valid locked archive; wrong lock/archive/file bytes,
  missing/duplicate/nonregular/oversized members and path confinement negatives;
  focused existing Python contracts, workflow syntax/AST, independent review and
  one exact-head provenance CI. Build/UI/characterization runtime gates are N/A
  for this no-native-execution step; real collected archive hashes are required.
  Risk remains bounded read-only input parsing; rollback is a scoped revert,
  retaining failed evidence and both frozen evaluator locks. Baseline stays
  unaccepted, no current best or confirmation, and optimization attempts remain 0/10.
- Correction checks: the new absent-generated-checksum/valid-archive fixture fails
  against the original collector and passes after archive verification. All 13
  collector contracts pass, including main manifest success and wrong checksum,
  changed/missing/duplicate/nonregular/oversized member, missing archive/source,
  symlink, malformed lock and expanded-archive bound negatives. The unchanged
  11 diagnostic, 7 frozen evaluator and 3 frozen workflow contracts also pass.
  The actual new verifier accepts the six retained CI caller files against the
  separately retained locked crate; this is a saved-data test, not new CI evidence.
- Copied runner x264 library/header bytes also exactly match historical run
  `40209722318` smoke hashes. This establishes cross-run package-file identity,
  not the historical renderer executable, static FFmpeg binding or active importer.
  Candidate 0 enables libx264/GPL and has its wrapper object; candidate 1 explicitly
  disables libx264/GPL. Do not mistake its expected absent object for a build error
  or select candidate 0 as historically active solely from this consistency.
- Independent review `c664b3b5` reproduced a mixed-caller false-success edge case:
  `any(verified)` hid a second discovered caller with changed source or missing
  archive. Lead independently reproduced both before fixing the gate to require
  a nonempty all-verified caller set. The actual main/manifest regression now
  requires exit 1 and retained failed-candidate errors for both cases; all 13
  collector tests pass. Record FFRAMES-NATIVE-PROVENANCE-MIXED-CALLERS. The first
  review is bound only to its original hashes because the lead applied the fix
  before its final snapshot assertion. Preserve that failed hash assertion rather
  than counting it as corrected-source review. A separate frozen review
  `ebdd6f7e` checks the corrected hashes; no active-binary or baseline claim follows.
- Final review `ebdd6f7e` confirms the all-caller correction at stable hashes
  `2c6104cbdad861be7a72f60aea3ede41d3ec0be30c8743accba7d2415e215f14`
  (collector) and `5b8d4d97ba65e2fb4727855eda1a92d8aec78ace3e8b4a8ecf55582c25bb9fd2`
  (tests), with no remaining substantive finding. It passed 11 Python tests
  (explicitly excluding existing Git/Bash tests), additional two-valid/mixed-invalid
  caller and corrupt-archive fixtures, and detected the previous gate via in-memory
  mutation. Lead ran all 13 collector tests, including the two omitted by review.
  Both reviews remain source/synthetic evidence; real cache/archive upload awaits CI.
  The frozen evaluator helper verifies all seven files and reports blocked with
  baseline/current-best null and zero experiments. Production/exporter/media bytes
  remain unchanged from the frozen baseline source. No rollback performed.
- Implemented and pushed correction `94e9eb6b49c72aff6a948e6651cf9ac0e13bc1dc`.
  Exact-head provenance run `40217925329` passed in 42s without native execution.
  Artifact `12608078778` is 1358515 bytes with 53 entries, SHA256
  `3520393dc5a4ca358030978f42280813b50f517c7e91eb7100590265d7188418`.
  Downloaded verification checks all 51 copied-file hashes/sizes, 12 hash-only
  records, seven successful command receipts, the uploaded crate's locked SHA256
  and all six caller member hashes. Prior collected-file hashes are unchanged.
  Generated checksum remains honestly missing; native candidates remain unbound.
- Exact local/remote/PR implementation head is green: inspect-native 42s, test
  4m02s, characterization-windows 7m56s, go-test-windows 37s and grep 5s. Normal
  render intentionally skipped. Watcher `bash-9b5b5fca3f02447a` and collection
  `bash-ebc671ff63064ea6` exited 0. Final checks and verification are retained under
  `out/native-provenance-40217925329/`. Failed run `40215299149` remains preserved.
- Further read-only provenance: downloaded the exact Ubuntu x264 source descriptor,
  orig/debian archives and amd64 binary package from the
  [Ubuntu archive](https://archive.ubuntu.com/ubuntu/pool/universe/x/x264/), version
  `2:0.164.3108+git31e19f9-1`. Both source archive SHA256/size values match the
  descriptor; its PGP signature was not independently verified. Read the binary
  package as data, without installation or package scripts: its libx264.so.164
  SHA256 equals both the collected runner library and historical smoke record.
  Five retained upstream source members also match the distro orig archive.
  Patch series has only properly_detect_x32.patch touching common/common.h and
  configure; matched frame/importer/encoder files are not directly patched.
  Compilation effects, runtime importer selection and reproducible source-to-binary
  build are not proven. Evidence is under `out/repeatability-analysis/x264-ubuntu-3108-1/`.
- Current decision: provenance acquisition is verified, but historical renderer-to-
  static-FFmpeg binding and safe complete capture/importer bounds remain unresolved.
  Do not rerun completed diagnostics or infer a padding/heap/FP/x264 fix from this
  collection. Next work is bounded source/binary/caller provenance only; no native
  execution or configuration variant was added. Fresh strict-repeatability baseline,
  performance candidates and confirmation remain pending, with 0/10 attempts.

### Multi-angle source and saved-data investigation

- Intent: answer the owner's request to inspect the failure from different angles
  without repeating completed diagnostics. Scope is caller ownership, importer
  footprint, observer coverage and evaluator/data integrity. Production, both
  evaluator locks, codec/FP/thread/padding settings, media and deliveries are fixed.
  Only source/data inspection and lightweight Python checks ran; no new native
  execution, compile, render, pixel/audio decode, installation or external search.
- Snapshot is `a369043715868b426a47429a2de8b641d96a60ee`. Three Sol fallbacks
  completed their assigned reports; earlier failed Opus/Grok dispatches contribute
  no findings. Reports and the lead's verification are retained under
  [multi-angle evidence](<tools/help-videos/fframes-vpnrouter/out/repeatability-analysis/multiangle-20261025-a369/>).
  Unrelated music documentation and its existing defect-ledger hunk are preserved
  and excluded from this checkpoint's commit.
- Caller review reverified crate/member identity and the real FramePool path:
  fresh owned AVFrames, refcounted pooled pixels, pending-map ownership and the
  per-segment mutex contradict simple in-flight reuse, stale AVFrame metadata,
  unwritten visible YUV and missing drain. All 16 traces have 45x24 inputs; all
  nonempty outputs occur on flush. Inspected x264 imports synchronously before
  queue/early return, so delayed output does not require source pixels until flush.
- Importer review and lead source-check: recorded CPU mask `1176575` conditionally
  selects AVX512 Y copy and SSE2 U/V interleave in the 8-bit HAVE_MMX build. Y has
  a masked visible-only tail; U/V rounded copies read five extra bytes on nonfinal
  rows. Immediate mod16 expansion overwrites every copied source-padding byte
  inside the coded 864-byte rectangle before AQ/lowres. Runtime dispatch and any
  downstream dependency on farther-right internal slack remain unproven. No
  padding normalization, full-stride capture or CPU/codec-setting fix is selected.
- Evaluator review independently checked 34560 decoded rows and joined all 6480
  native video tuples to 26736 actual packet-byte hashes in the six changed-style
  films. Lead reproduced the complete packet bridge: native changed PTS are
  anime/editorial/kinetic 48/83/73, decoded 48/216/96; affected lifetime sets agree.
  Wrong row grouping, B-frame order, metadata-only differences and a simple
  trace/output swap do not explain this retained witness.
- Confirmed evidence gap: the original failed baseline `40192719205` did not retain
  either comparison-signature operand or compared media before assertion/cleanup
  (`benchmark.py:226-261`). Record FFRAMES-EVALUATOR-BASELINE-FAILURE-RETENTION as
  deferred; do not mutate the frozen evaluator. Later native localization cannot
  retroactively identify the exact differing style/kind in that original run.
- Observer limits: effective parameters omit queued reconfiguration; the getter
  forces returned opaque null. No differing reconfig trigger was found, and equal
  later snapshots argue against a persistent update. Pre-open FP/derived state is
  not captured; later equal controls do not prove equal initialization. Neither
  gap establishes a cause. Recorded 1/1/0 threading also contradicts assuming an
  active unobserved x264 lookahead worker from that setting alone.
- Selected and completed one additional saved-data check: bounded SPS/PPS/IDR
  header parsing plus a separately written prefix parser checked 270 headers
  (135 pairs), with verified packet/native byte binding. All 15 affected lifetimes
  already differ in their first IDR, while parsed headers and base slice QP agree
  (QP 15 or 16). First unequal RBSP bytes are 11549-18374, after the CABAC body
  starts at byte 5. Previous-segment references, a different initial slice type or
  base QP are not sole explanations. Per-MB QP/AQ, prediction/residuals, imported
  internal pixels and internal state remain unobserved; no CABAC/pixel decode ran.
- IDR result SHA256 is
  `6918518f315767d368d6ff29446e4ac85df5d09f5bc54061c978cb99d1d022fd`;
  `idr-header-crosscheck.json` retains 18 exact input hashes. Bit/Exp-Golomb/escape
  negatives, both header parsers and the lead's all-PTS packet bridge exited 0.
- Next discriminating boundary, not a launched job: one affected 24-frame lifetime
  with unchanged settings, prospectively bound native identity and source-bounded
  visible importer-entry/post-mod16 coded-rectangle hashes. A difference on entry
  places divergence before import; equal entry/unequal exit points to import or
  omitted consumed input; equal coded exit/unequal IDR body points downstream or
  to a demonstrated internal-slack dependency. Historical static FFmpeg/runtime
  dispatch is still unresolved; no replay or native capture is implemented here.
- Gates: source/data checks PASS; scoped documentation/link/diff checks and exact-
  head PR CI are required for this evidence checkpoint. Runtime build/UI/new native
  diagnostic gates N/A because no behavior changed. Risk is evidence overclaiming,
  addressed by conditional source conclusions and retained counterevidence. Rollback
  is a scoped history-preserving revert of this record update only, keeping evidence
  and unrelated work. Strict baseline remains unaccepted, best null, attempts 0/10;
  there is no measured acceleration, accepted candidate or confirmation.

### Post-mod16 follow-up and missing-CI recovery

- The source/data follow-up at `6b2e006020267f204f917f01b0c39903648cc1d3`
  checked AQ and lowres, with two independent read-only reviews and ten passing
  Python model checks. Conditional AVX512 AQ original-frame loads fit the coded
  rectangle; extra scratch lanes are discarded. AVX2 lowres reads farther-right
  slack, but only overwritten extra outputs depend on it. Actual native dispatch
  and root cause remain unproven. Evidence: `out/repeatability-analysis/postmod16-6b2e/`.
- Standard CI did not register at that head; manual source guard `40330938748`
  passed. Exact remote main is `fbae13f558837e847c467904e5e1d9ebfb3cb55b`;
  read-only merge simulation confirms one conflict in OPEN-DEFECTS.md because
  both branches insert unrelated sections after Open. The PR reports dirty and
  its saved merge ref still names previous head `a3690437`; REST base metadata is
  stale relative to the actual remote main. This is not a failing test result.
- Recovery intent: relocate only committed fframes ledger sections to the end of
  Open, preserving their exact text and all statuses. Do not merge/rebase main,
  alter product/workflow/evaluator/media bytes, include music work or rerender.
  Verify byte-preserved sections, unrelated files, empty prior index, synthetic
  conflict-free merge, scoped diff and actual new-head PR checks. No manual build
  or native diagnostic dispatch. Missing CI recovery is not a performance attempt.
- Risk: incorrect ledger placement or accidental inclusion of unrelated additions.
  Independent source review checks scope/preservation before commit. Rollback is
  a scoped history-preserving revert of this documentation-only recovery; keep
  evidence and evaluator locks. Build/UI/media gates N/A for changed behavior;
  ordinary exact-head PR CI remains required. Baseline/best null, attempts 0/10.
- Recovery checks: synthetic merge against exact current main is conflict-free;
  moved text/line multiset and the release gate's open P0/P1 set are unchanged.
  Both evaluator manifests verify (13 files), unrelated file hashes match and
  only the two task records are intended for commit. Independent review and new-
  head CI are pending; no runtime or acceleration claim follows.
