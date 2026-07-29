# Phase 1 Audit Remediation — P08 AppImageTool Pin

**Owner**: Qwen Code (implementation engine); orchestrator handles Git
**Branch**: `codex/qwen-audit-p08-appimagetool-pin-2026-07-29` (off current `origin/main`)
**Audit source**: `plans/qwen-full-app-audit-2026-07-28/RESULTS.md` (PR #48)
**Adjudication**: `plans/qwen-audit-independent-verification-2026-07-28.md` (P00, commit `b39a28c3`)
**Effort**: ~1 h
**Risk**: LOW (CI-only change; no product code; fail-closed on digest mismatch)
**Blast radius**: 1 CI workflow file (`build-linux.yml`)
**Rollback**: `git revert <commit>` / branch delete

## Findings in scope

| ID | Orig | P00 Verdict | Final | Confidence |
|---|---|---|---|---|
| SUP-1 | P1 | CONFIRMED | **P1** | High |

ONLY SUP-1. Explicitly NOT in scope: SUP-2 (libcronet digest, P2), SUP-3
(signing action pins, P3), SUP-4 (wgturn source pin, P2), PKG-1 (macOS ARCH, P2)
— separate packages.

## Execution constraint (overrides methodology gates)

All implementation is performed through Qwen Code. Qwen may read/search/edit code
and write tests, but MUST NOT run local builds, tests, applications, binaries,
services, installers, package restore, VM/WinRM/ADB/MCP/live checks, downloads,
or platform mutations. Validation happens ONLY in remote GitHub CI after the
orchestrator pushes the branch. **Qwen MUST NOT commit or push** — the orchestrator
reviews the diff and handles Git.

## Why

The Linux release build downloads a mutable `continuous` (rolling) appimagetool
ELF from the AppImageKit releases, makes it executable, and runs it — with NO
version pin and NO digest verification. A compromised AppImageKit release channel
or CDN MITM yields arbitrary code execution inside the release build, enabling
tampering of all Linux artifacts (AppImage, .deb, tar.gz). This is a supply-chain
takeover vector.

## Current root cause (verified against current code)

- [FACT] `.github/workflows/build-linux.yml:169` —
  `wget -q -O appimagetool "https://github.com/AppImage/AppImageKit/releases/download/continuous/appimagetool-x86_64.AppImage"`
  The URL path `continuous` is a rolling tag — the asset behind it changes on
  every AppImageKit CI build. No version pin.
- [FACT] `:170` — `chmod +x appimagetool`
- [FACT] `:172` — `ARCH=x86_64 ./appimagetool --appimage-extract-and-run VPNRouter.AppDir ...`
  Executes the downloaded ELF with write access to the release pipeline.
- [FACT] `:283` — `sha256sum` step only emits sidecars for FINAL artifacts
  (AppImage, .deb, tar.gz), NOT for input verification.
- [FACT] The sing-box/libcronet download (`:107`) also lacks a digest check
  (that is SUP-2, P2, out of scope here).
- [FACT] All `uses:` actions in the workflow are SHA-pinned (e.g.,
  `actions/checkout@df4cb1c069e1874edd31b4311f1884172cec0e10`). The appimagetool
  download is the ONLY mutable external binary in the workflow.
- [FACT] The `continuous` URL points at the RETIRED `AppImage/AppImageKit`
  repository. The maintained successor is the standalone
  **`AppImage/appimagetool`** repository, which publishes immutable
  per-version release tags alongside its own `continuous` build. Verified
  read-only via `gh api repos/AppImage/appimagetool/releases/tags/1.9.1`:
  the current immutable release is **tag `1.9.1`** ("Release 1.9.1",
  `prerelease:false`, `draft:false`), and its x86_64 asset is
  **`appimagetool-x86_64.AppImage`** (size `15092216` bytes) served from
  `https://github.com/AppImage/appimagetool/releases/download/1.9.1/appimagetool-x86_64.AppImage`.
  (Older immutable tag `1.9.0` also exists; `1.9.1` is the latest.) Do NOT pin
  the retired AppImageKit numeric tags (e.g. `13`) — that release line is the
  old path and is what the `continuous` ref tracks.

## What

### Minimal expected file list
- `.github/workflows/build-linux.yml` — pin appimagetool to an immutable version
  + verify SHA256 before execution.

### Explicit non-goals
- Do NOT fix SUP-2 (libcronet digest) — separate P2 package.
- Do NOT fix SUP-3 (signing action pins) — separate P3 package.
- Do NOT fix SUP-4 (wgturn source pin) or PKG-1 (macOS ARCH) — separate P2 packages.
- Do NOT change the AppImage build logic, AppDir layout, or output naming.
- Do NOT add a new CI job or workflow.
- Do NOT download or execute appimagetool locally during implementation.

## How (ordered; fix the shared root cause once)

1. Replace the retired `AppImage/AppImageKit` `continuous` URL with the
   immutable successor-repo release asset. Use the verified tag `1.9.1`
   x86_64 asset (see root cause [FACT]):
   ```
   https://github.com/AppImage/appimagetool/releases/download/1.9.1/appimagetool-x86_64.AppImage
   ```
   Document the repository + tag + asset name in a comment. Do NOT use any
   `AppImage/AppImageKit` URL (retired) or its `continuous`/numeric tags.

2. Obtain the expected SHA256 FROM AUTHORITATIVE RELEASE METADATA — do NOT
   invent or hand-write a digest, and do NOT download or execute the binary
   locally during implementation (the execution constraint forbids it). The
   GitHub Releases API publishes the asset `name`, `size`, and
   `browser_download_url` but NOT a SHA256, so the digest is seeded once from
   the official release asset and then pinned:
   - Authoritative metadata (verified read-only, no download):
     `gh api repos/AppImage/appimagetool/releases/tags/1.9.1` →
     asset `appimagetool-x86_64.AppImage`, `size = 15092216`,
     `browser_download_url` as in step 1. Pin the size as a secondary
     metadata-only integrity signal.
   - Authoritative digest capture (one-time, performed in CI or by the owner —
     NOT by the implementation agent locally): fetch the asset ONCE from the
     official `browser_download_url` over github.com's verified TLS channel and
     compute `sha256sum appimagetool-x86_64.AppImage`. Record that value as
     `APPIMAGETOOL_SHA256`. The digest's trust root is the same github.com TLS
     channel the build already trusts for `actions/checkout`; pinning it converts
     a mutable-ref RCE vector into a fail-closed integrity check.
   - This brief deliberately leaves the digest as a `<pinned-sha256>` placeholder;
     the implementing CI step / owner fills it from the capture above. Recording
     a fabricated value is a defect.

3. Add a SHA256 digest verification step BEFORE `chmod +x`:
   ```yaml
   - name: Build AppImage
     run: |
       set -euo pipefail
       APPIMAGETOOL_URL="https://github.com/AppImage/appimagetool/releases/download/1.9.1/appimagetool-x86_64.AppImage"
       APPIMAGETOOL_SHA256="<pinned-sha256>"   # captured from the official release asset (see step 2); never invented
       APPIMAGETOOL_SIZE=15092216              # from gh api repos/AppImage/appimagetool/releases/tags/1.9.1
       wget -q -O appimagetool "$APPIMAGETOOL_URL"
       [ "$(stat -c %s appimagetool)" = "$APPIMAGETOOL_SIZE" ]   # metadata-only size gate
       echo "${APPIMAGETOOL_SHA256}  appimagetool" | sha256sum -c -
       chmod +x appimagetool
       ARCH=x86_64 ./appimagetool --appimage-extract-and-run \
         VPNRouter.AppDir \
         "VPNRouter-v${{ steps.version.outputs.version }}-linux-x86_64.AppImage"
   ```
   `sha256sum -c -` exits non-zero on mismatch → `set -e` aborts the build
   (fail-closed). The size gate is a cheap first trip-wire from release metadata.

4. Add a comment documenting the update procedure:
   ```
   # To update appimagetool: bump the tag in APPIMAGETOOL_URL to the next
   # immutable AppImage/appimagetool release, re-read the asset size from
   # `gh api repos/AppImage/appimagetool/releases/tags/<tag>` into
   # APPIMAGETOOL_SIZE, re-capture the SHA256 from the official release asset
   # (one-time CI/owner download over github TLS) into APPIMAGETOOL_SHA256.
   # Fail-closed: any size/digest mismatch aborts the build before chmod/exec.
   ```

## Callers / consumers to preserve

- `build-linux.yml` "Build AppImage" step — the fixed step. Output artifact
  name (`VPNRouter-v*-linux-x86_64.AppImage`) unchanged.
- Downstream steps: "Compute SHA256 checksums" (`:283`), "Upload as workflow
  artifacts", "Upload to GitHub Release" — all consume the AppImage output;
  unchanged.
- `build-mac.yml` — separate workflow; unchanged.
- `publish-apt.yml` — consumes .deb only; unchanged.

## Regression tests (exact)

No executable test file (CI workflow change). Verification is static:

- **Grep check**: after the change, `grep -c "continuous" .github/workflows/build-linux.yml`
  returns 0 (no mutable `continuous` URL remains).
- **Grep check**: `grep -c "AppImage/AppImageKit" .github/workflows/build-linux.yml`
  returns 0 (retired repository fully removed).
- **Grep check**: `grep -c "AppImage/appimagetool/releases/download/1.9.1/appimagetool-x86_64.AppImage" .github/workflows/build-linux.yml`
  returns >= 1 (successor repo, immutable tag, correct asset present).
- **Grep check**: `grep -c "sha256sum -c" .github/workflows/build-linux.yml`
  returns >= 1 (digest verification present).
- **Grep check**: `APPIMAGETOOL_SHA256` is NOT the literal placeholder `<pinned-sha256>`
  in the committed workflow — it holds the digest captured from the official
  release asset (see How step 2). A committed placeholder is a defect.
- **CI green**: the next tag-push or `workflow_dispatch` run of `build-linux.yml`
  produces the AppImage successfully with the pinned version.
- **Negative**: if the digest is intentionally wrong, the build MUST fail at the
  `sha256sum -c` step (verified by CI on the first run; not a permanent test).

Must stay green: all existing `build-linux.yml` output artifacts (AppImage, .deb,
tar.gz, 3 sha256 sidecars).

## Risks

- **Security**: pins an immutable tag (`1.9.1`) + digest → eliminates the
  mutable-ref supply-chain vector. Fail-closed on size/digest mismatch.
- **Compatibility**: the pinned appimagetool version must produce a compatible
  AppImage. `AppImage/appimagetool` `1.9.1` is the current stable immutable
  release of the maintained successor tool (the retired `AppImage/AppImageKit`
  line is what the old `continuous` ref tracked); the AppImage format is stable.
  If a future Ubuntu runner update breaks compatibility, bump the tag pin.
- **Cross-platform**: CI-only (ubuntu-22.04 runner). No product code change.
- **Rollback**: revert the workflow change; the old retired `continuous` URL may
  still resolve (but is insecure and on a retired repo). No product impact.
- **Digest capture**: the SHA256 MUST be captured from the authoritative source
  (the official `AppImage/appimagetool` release asset served by github.com over
  its verified TLS channel), not from a mirror or local build, and never invented.
  The implementation agent does NOT download/execute the binary locally; the
  capture is a one-time CI/owner step (see How step 2). Document the capture
  source in the commit message.

## Dependencies and file overlap with the other seven packages

- **P01 (UPD-1/UPD-2)**: P01 may add a `go test` step to `test.yml`. P08 edits
  `build-linux.yml`. Different files; no overlap.
- **P02-P07, P09-P10**: no overlap (product code / different CI files).
- **SUP-2 (P2, future)**: the sing-box/libcronet digest fix will also edit
  `build-linux.yml`. Sequence P08 before SUP-2 to avoid merge conflict.
- No blocking dependency on any other package.

## Zone CLAUDE.md constraints (`.github/workflows/CLAUDE.md`)

- Actions pinned to full SHAs (Dependabot); the appimagetool download is NOT
  an action but a `wget` step — the same pinning discipline applies.
- Job id `build` is the Linux build job; do NOT rename.
- `GH_TOKEN` required for release upload steps; unchanged.
- No emoji (AGENTS.md #9).

## Verification gate (remote-only, tailored)

- [ ] **Gate 1 — Build (remote CI only)**: orchestrator pushes branch; the next `build-linux.yml` run (tag or dispatch) compiles and produces all artifacts. Qwen does NOT build locally.
- [ ] **Gate 2 — Tests (remote CI only)**: N/A for CI workflow change; the `test` job is unaffected. The `build-linux.yml` run itself IS the verification.
- [ ] **Gate 3 — Docs**: brief Outcome filled after CI; commit message documents the digest capture source.
- [ ] **Gate 4 — Self-review**: Qwen static self-review of the workflow diff (supply-chain change → review URL immutability + digest correctness).
- [ ] **Gate 5 — UI/live**: N/A (CI-only change).
- [ ] **Gate 6 — Characterization**: N/A.

## Outcome

**Status**: IMPLEMENTED / REMOTE CI GREEN
**Commits**: `63a4856b` (ci: pin and verify appimagetool release)
**Pushed**: draft PR #61, branch `codex/qwen-audit-p08-appimagetool-pin-v2-2026-07-29`
**Test deltas**: +57 / -0 (1 new test file: `BuildLinuxAppImageToolPinTests.cs` +57)
**Files changed**: 2 · +73 / -2

**Gate results:**
- [x] Gate 1 build (remote CI — build-linux.yml run): PASS — Linux packaging run 30447028180 SUCCESS including the fail-closed digest gate
- [x] Gate 2 tests (remote CI): PASS — dotnet test run 30447026030 SUCCESS; new `BuildLinuxAppImageToolPinTests` green; full existing suite stayed green
- [x] Gate 3 docs: PASS — Outcome filled; digest provenance: capture run 30446330695 SUCCESS, size 15092216, SHA256 `ed4ce84f0d9caff66f50bcca6ff6f35aae54ce8135408b3fa33abfc3cb384eb0`
- [x] Gate 4 self-review / supply-chain review: PASS — static self-review performed during implementation; URL immutability (retired AppImageKit `continuous` replaced with `AppImage/appimagetool` tag `1.9.1`) and digest correctness reviewed
- [-] Gate 5 UI/live: N/A (CI-only change)
- [-] Gate 6 characterization: N/A

**Local build/test**: NOT run. The mandatory git hook attempted SDK resolution and found SDK 10.0.301 absent; this is not a pass.
**Surprises encountered**: none
**Follow-ups spawned**: none
**Rollback**: `git revert 63a4856b` / branch delete
