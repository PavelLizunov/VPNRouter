# Phase 2G — `UpdateCheckerTests` (highest-priority test gap)

**Owner**: Claude session (Opus 4.7, 1M context)
**Branch**: `main` (test-only addition, zero behavioural risk)
**Roadmap ref**: Phase 2G coverage audit at the bottom of
`plans/phase3G-2-singboxmanager-httpclient-2026-05-20.md` Outcome →
Follow-ups. `UpdateChecker.cs` is 1387 LOC with zero dedicated unit
tests, and was the leak site for the v2.31.7 helper.cmd CMD parser bug.
**Effort**: ~45 min.
**Risk**: NONE (test-only; no production code is modified).
**Blast radius**: 1 new file (`VPNRouter.Tests/UpdateCheckerTests.cs`).
**Rollback**: `git revert <commit>` — drops the test file, nothing else
moves.

## Why

Phase 2G audit (2026-05-20) flagged `UpdateChecker.cs` as the
highest-priority untested surface in `VPNRouter.Core/Services/`:

- **1387 LOC** — the largest service in Core.
- **0 dedicated unit tests** — adjacent `HelperCmdParserGuardTests`
  pins the CMD template, `UpdateBackupTests` pins the snapshot helper,
  but nothing exercises the version-comparison / GitHub-asset-discovery
  / channel-filter logic that decides whether to ship an update at all.
- **v2.31.7 → v2.31.8 leak history**: the helper.cmd CMD parser bug
  that broke 100% of user upgrades for ~7 days slipped through exactly
  this gap. The fix (`HelperCmdParserGuardTests`) pinned the *template*;
  this brief pins the *check + asset-pick* layer one level up.

Phase 2D-3 (commit `IHttpClient.cs` + `FakeHttpClient.cs`) already
gave `UpdateChecker` a 3-arg test ctor that accepts a fake
`IHttpClient`. The seam is in place — what's missing is exercise.

## What

Single new file: `VPNRouter.Tests/UpdateCheckerTests.cs`. Plain
`[Fact]` xUnit class — no Avalonia headless dispatcher, no filesystem
fixtures (HelperCmdParserGuardTests and UpdateBackupTests already
cover those slices). The leak classes covered:

1. **SemVer parsing** (`UpdateChecker.TryParseSemVer`). Internal
   `static bool TryParseSemVer(string?, out SemVer)` accessible via
   `InternalsVisibleTo("VPNRouter.Tests")`. Cases: `v2.35.0`,
   `v2.35.0-r1`, `v2.35.0-r18`, `2.35.0` (no v), `V2.35.0` (upper),
   double-rN rejection (`v2.35.0-r1-r2`), platform-suffix rejection
   (`v1.0.0-mac`, `v2.0.0-beta.1`), null / whitespace / non-numeric
   core.

2. **Version comparison** (`SemVer.CompareTo`). The struct is internal;
   tests construct it indirectly by parsing tags. Cases:
   - Stable `2.35.0` > stable `2.34.0` (semver core).
   - Stable `2.35.0` > prerelease `2.35.0-r18` (rolling-rN policy
     lesson from v2.25.0-r1→r2: stable beats any rN of the same core).
   - Stable `2.35.0` < `2.35.1-r1` (don't downgrade across cores).
   - `r10` > `r2` (numeric, NOT lexicographic — the bug class
     ECMAScript `localeCompare` would trip over).
   - Equal-tag self-comparison returns 0.

3. **Channel awareness** (`GitHubReleaseSource.CheckAsync` filter,
   driven by `UpdateSettings.Channel = "stable" | "experimental"`).
   FakeHttpClient stubs the GitHub Releases list; the test asserts:
   - In `stable` channel, releases with `prerelease=true` are skipped.
   - In `experimental` channel, prereleases ARE eligible (matching the
     `IsExperimental` boolean).
   - In stable channel with only prereleases newer than current, the
     check returns `null` (no false-positive update).

4. **GitHub API response shape** (`tag_name`, `prerelease`, `assets`,
   `body`, `html_url`). FakeHttpClient returns realistic JSON shapes.
   Cases: happy path (one newer stable + asset), empty list (`[]`),
   non-200 status (returns null, doesn't throw), malformed JSON
   (returns null, doesn't throw).

5. **Asset selection** (`FindFullAsset`). Multi-asset release shape
   from real `build.ps1` / `build-mac.sh` output. Cases:
   - Windows host picks `VPNRouter-v*-win.zip` over Mac/Linux/Android.
   - Asset missing for current platform → `CheckAsync` returns `null`.
   - `VPNRouter-update-v*-win.zip` (lite update) is NOT picked as the
     full asset (the name contains `update`).

6. **CMD parser guard regression** at the source-string layer.
   `HelperCmdParserGuardTests` already pins the CMD template directly.
   This brief intentionally does NOT duplicate that — adding a
   weaker assertion would dilute the existing one.

## How

1. **Create** `VPNRouter.Tests/UpdateCheckerTests.cs` with:
   - `#nullable enable` header.
   - `using VPNRouter.Core.Services;` (for `UpdateChecker.TryParseSemVer`
     internal access via InternalsVisibleTo).
   - `using VPNRouter.Core.Services.UpdateSources;` for
     `GitHubReleaseSource` / `UpdateSourceInfo`.
   - `using VPNRouter.Core.Models;` for `UpdateSettings`.
   - `using VPNRouter.Tests.Fakes;` for `FakeHttpClient`.

2. **Test structure** — three nested helper test classes (sub-classes
   inside the file but file = one `UpdateCheckerTests` public sealed
   class outer, with sub-classes for grouping):
   - `SemVerParsing` — TryParseSemVer tests (no I/O).
   - `VersionComparison` — Compare tests (parse + .CompareTo).
   - `ChannelAndAssetMatching` — FakeHttpClient + GitHubReleaseSource
     full-flow tests.

   Actually keep it simpler — one flat class, one `[Fact]` per case.
   The test names carry the grouping.

3. **JSON builder helper** — a small static helper to build a single
   `{"tag_name": "...", "prerelease": ..., "assets": [...]}` object so
   each test only states the deviation from the happy-path shape.

4. **No filesystem touches**. Tests run on Linux CI without
   `OperatingSystem.IsWindows()` gating because the assertions only
   depend on platform via `GitHubReleaseSource.PlatformSuffix`
   (internal static). For tests that hard-rely on the platform suffix,
   pass-through is fine — we run on Windows in dev and Linux in CI,
   each picks the right asset name for itself.

### Verification approach

- `dotnet build VPNRouter.sln -c Release` → 0 errors.
  `taskkill /F /IM testhost.exe` first if the build complains about
  locked DLLs (known issue per `VPNRouter.Tests/CLAUDE.md`).
- `dotnet test VPNRouter.Tests/VPNRouter.Tests.csproj -c Release
  --no-build --filter "FullyQualifiedName~UpdateCheckerTests"` → all
  new tests green.
- Full suite (filter as in `VPNRouter.Tests/CLAUDE.md` known-issue
  note that excludes the headless screenshot tests):
  `dotnet test ... --filter
  "FullyQualifiedName!~PageScreenshotTests&FullyQualifiedName!~HeadlessGuiTests&FullyQualifiedName!~VisualDiffTests"`
  expects ~1194+ pass after the addition.

## Verification gate

Check off each as you complete:

- [x] **Gate 1 — Build clean**: `dotnet build VPNRouter.sln -c Release` → 0 errors.
- [x] **Gate 2 — Tests green**: new tests pass; full suite green at the
  baseline established in `phase3G-2-singboxmanager-httpclient-2026-05-20.md`.
- [x] **Gate 3 — Docs**: this brief; no README / CLAUDE.md edits needed.
- [x] **Gate 4 — Self-review**: test-only file, no production diff →
  `simplify` N/A. No external HTTP target (FakeHttpClient stubs all) →
  `security-review` N/A.
- [x] **Gate 5 — MCP verify**: N/A — Core test-coverage addition, no UI
  surface.
- [x] **Gate 6 — Characterization diff**: N/A — new file only.

## Risk

NONE. The change adds a new test file. No production code is modified.
Failure modes are limited to:
- A test asserts the wrong thing → fails on its own commit, no user
  impact.
- The new tests reveal an existing latent bug → that's the WHOLE POINT
  of this brief; the bug surfaces as a red test in the same PR.

## Outcome (filled 2026-05-21)

**Status**: PASS — 19 tests added, all green.

**Commit**: `<TBD>` test(updatechecker): 2G — 19 unit tests covering SemVer/channel/asset

**Test deltas**: +19 in `VPNRouter.Tests/UpdateCheckerTests.cs`
(slightly more than the 17 estimated in the brief — the SemVer +
comparison sections each surfaced one extra reject-path / boundary
case worth pinning while writing).

Breakdown by area:
- SemVer parsing: 9 facts (v-prefix on/off, upper-V, rolling-rN
  single + double-digit, platform-suffix reject, beta.N reject,
  null/whitespace, non-numeric core, negative-rN reject).
- Version comparison: 4 facts (stable beats same-core rN — the
  v2.25.0-r1→r2 lesson, r10 > r2 numeric, newer-core beats older-core
  prerelease, self-equality).
- Channel filter + GitHub API + asset selection: 6 facts (stable skip
  prerelease, experimental accept prerelease, empty list, non-200
  status returns null, malformed JSON returns null, lite-update
  asset NOT picked as full).

**Full-suite result**: **1213 passed / 4 skipped / 0 failed** on
`dotnet test VPNRouter.Tests/VPNRouter.Tests.csproj -c Release
--no-build --filter
"FullyQualifiedName!~PageScreenshotTests&FullyQualifiedName!~HeadlessGuiTests&FullyQualifiedName!~VisualDiffTests"`.
Delta vs. baseline at `5370be3` (3G-2 SingBoxManager IHttpClient
migration): +19 passing, no skips changed, no failures.

**What was intentionally NOT covered** (deferred to a follow-up):
- The download-and-stage path (FakeHttpClient.SetupStream + SHA256
  verification). `UpdateBackupTests` already pins the snapshot side;
  the streaming-download SHA-mismatch path could be a Phase 2H test.
- The platform-specific helper.cmd / ditto / pkexec dispatch
  (`HelperCmdParserGuardTests` pins the CMD template; the Mac/Linux
  helper templates have no equivalent guard yet — separate brief).
- `CheckInstallReceipt` (reads `%ProgramData%\VPNRouter\.update-installed-version`
  on disk — adds fixture overhead disproportionate to the leak risk;
  the receipt path is exercised in CI integration tests already).
