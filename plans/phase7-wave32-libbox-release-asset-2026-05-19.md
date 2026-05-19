# Phase 7 — Wave 32 LIBBOX_AAR provisioning via release-asset fetch

**Owner**: Claude session 0ecbd816-09bb-420b-89b3-996da5a420fe
**Branch**: main (small CI-only change, no v3.0-prep needed)
**Roadmap ref**: plans/phase6-completion-2026-05-19.md "Carry-over to Phase 7" + plans/phase6-nativeaot-readiness-2026-05-18.md
**Effort**: ~2-3 hours
**Risk**: LOW — CI workflow change only, gracefully skips on missing prerequisites, no production runtime impact
**Blast radius**: 2 files (`.github/workflows/build-android.yml`, `.github/SECRETS.md`) · ~40-60 LOC delta · zero binary changes
**Rollback**: `git revert <commit>`

## Why

Phase 6 Wave 26 (commit `d29c128`) wired Android APK build into CI by
provisioning the 11.7 MB `libbox.aar` sing-box gomobile binding from a
`LIBBOX_AAR_BASE64` GitHub Actions secret. The design is fundamentally
broken: GitHub Actions secrets are capped at **48 KB**, and `libbox.aar`
base64-encoded is **~15.6 MB** — two orders of magnitude over the limit.

Symptom observed during v2.35.0-r3 ship cycle (2026-05-19):
```
failed to set secret "LIBBOX_AAR_BASE64": HTTP 422: Value is too large.
```

Workflow currently `gracefully skips` Android build when the secret is
empty — which means CI never produces an APK. v2.35.0-r3's APK was
hand-built locally + uploaded via `gh release upload`. That doesn't
scale: every future tag push needs the same manual step, and the
`verify-release-integrity` workflow reports `Missing 2/14 expected
assets` (soft warning) on every tag.

Solution: host `libbox.aar` as an asset on a dedicated **internal
tooling release** on the same repo (no secrets involved), then in
`build-android.yml` fetch it via `gh release download` using the
ambient `GITHUB_TOKEN`. Release assets can be up to 2 GB — comfortably
fits 11.7 MB. No secret-size limit involved.

This restores end-to-end CI Android builds and closes the
`verify-release-integrity` warning. It also documents the libbox.aar
rotation/upgrade path (when sing-box bumps to 1.13.11+, bump the
tooling-release tag, no workflow change needed).

## What

### File 1: `.github/workflows/build-android.yml`

Replace the existing `Provision libbox.aar from secret` step
(currently using `LIBBOX_AAR_BASE64` env var) with a `gh release
download` call against the internal tooling release.

```diff
-      - name: Provision libbox.aar from secret
-        id: libbox_provision
-        env:
-          LIBBOX_AAR_B64: ${{ secrets.LIBBOX_AAR_BASE64 }}
-        run: |
-          if [ -z "${LIBBOX_AAR_B64:-}" ]; then
-            echo "::warning::LIBBOX_AAR_BASE64 secret is not set — skipping Android APK build. See .github/SECRETS.md for the one-time provisioning command."
-            echo "skip=true" >> "$GITHUB_OUTPUT"
-            exit 0
-          fi
-          echo "skip=false" >> "$GITHUB_OUTPUT"
-          mkdir -p VPNRouter.Android/Lib
-          echo "$LIBBOX_AAR_B64" | base64 -d > VPNRouter.Android/Lib/libbox.aar
-          test -s VPNRouter.Android/Lib/libbox.aar
-          echo "libbox.aar decoded: $(stat -c '%s bytes' VPNRouter.Android/Lib/libbox.aar)"
+      - name: Provision libbox.aar from tooling release
+        id: libbox_provision
+        env:
+          GH_TOKEN: ${{ secrets.GITHUB_TOKEN }}
+          # Pin to a specific tooling release to make sing-box upgrades
+          # explicit. To rotate: build a new libbox.aar, attach as asset
+          # to a new `tooling-libbox-singbox-X.Y.Z` release, bump this tag.
+          LIBBOX_RELEASE_TAG: "tooling-libbox-singbox-1.13.10"
+        run: |
+          mkdir -p VPNRouter.Android/Lib
+          # Use --clobber so re-runs work; --pattern restricts what gh
+          # downloads (the tooling release shouldn't have other assets
+          # but be defensive).
+          if ! gh release download "$LIBBOX_RELEASE_TAG" \
+               --repo "$GITHUB_REPOSITORY" \
+               --pattern "libbox.aar" \
+               --output VPNRouter.Android/Lib/libbox.aar; then
+            echo "::warning::Failed to fetch libbox.aar from tooling release $LIBBOX_RELEASE_TAG — skipping Android APK build. See .github/SECRETS.md for tooling-release provisioning."
+            echo "skip=true" >> "$GITHUB_OUTPUT"
+            exit 0
+          fi
+          test -s VPNRouter.Android/Lib/libbox.aar
+          echo "skip=false" >> "$GITHUB_OUTPUT"
+          echo "libbox.aar provisioned: $(stat -c '%s bytes' VPNRouter.Android/Lib/libbox.aar) from release $LIBBOX_RELEASE_TAG"
```

### File 2: `.github/SECRETS.md`

Remove the `LIBBOX_AAR_BASE64` documentation (it's no longer used) and
add a new section documenting the tooling-release pattern + the
one-time provisioning command for setting it up.

### Setup work (outside the diff)

Create the internal tooling release once:

```bash
gh release create tooling-libbox-singbox-1.13.10 \
  --repo PavelLizunov/VPNRouter \
  --title "Tooling: libbox.aar (sing-box 1.13.10 gomobile binding)" \
  --notes "Internal CI asset. Fetched by .github/workflows/build-android.yml via 'gh release download' using GITHUB_TOKEN. Not user-facing — see .github/SECRETS.md for rotation procedure when upgrading sing-box version." \
  VPNRouter.Android/Lib/libbox.aar
```

This release will appear in the repo's release list. It's not marked
prerelease, but it's tagged with `tooling-*` prefix so it's distinguishable
from product releases. We do NOT mark `--latest` so it doesn't override
the actual latest product release on the page.

## How

1. Verify libbox.aar matches expected sing-box version (1.13.10 per
   MEMORY.md) and is ready to upload.
2. Create `tooling-libbox-singbox-1.13.10` release via `gh release
   create` with the local `VPNRouter.Android/Lib/libbox.aar` as
   sole asset. Capture the resulting release URL.
3. Edit `.github/workflows/build-android.yml`:
   - Replace the `Provision libbox.aar from secret` step body
   - Update inline doc-comments referencing `LIBBOX_AAR_BASE64`
   - Keep the graceful-skip path intact (now triggers on
     `gh release download` failure instead of empty env var)
   - Verify all downstream `if: steps.libbox_provision.outputs.skip != 'true'`
     gates still wire up correctly
4. Edit `.github/SECRETS.md`:
   - Remove `LIBBOX_AAR_BASE64` row from the secrets table
   - Add new section: "Internal tooling releases" documenting:
     - Why this pattern (48 KB secret limit + 2 GB asset limit)
     - Rotation procedure (build new aar → `gh release create
       tooling-libbox-singbox-X.Y.Z` → bump `LIBBOX_RELEASE_TAG` env
       var in workflow → commit)
     - How to remove the old tooling release once the workflow has
       been updated and bedded in for a few cycles
5. Build clean: `dotnet build VPNRouter.sln -c Release` (no Android
   target — just verify nothing else broke).
6. Trigger `workflow_dispatch` on `build-android.yml` against the
   current `v2.35.0-r3` tag. Verify it completes end-to-end with
   the new provisioning step.
7. Commit + push.
8. Optionally: revoke the `LIBBOX_AAR_BASE64` secret if it was set
   (we have not set it because it's impossible). But also revoke any
   `ANDROID_KEYSTORE_BASE64` rotation — those secrets remain.

### Tests written

None — this is a CI workflow change with no production code impact.
The verification approach is end-to-end: trigger the workflow and
confirm an APK is produced.

### Verification approach

1. **Build clean** (gate 1): `dotnet build VPNRouter.sln -c Release`
   → 0 errors (sanity check — workflow change should not impact
   solution build).
2. **CI smoke**: trigger `workflow_dispatch` on `build-android.yml`
   for the v2.35.0-r3 tag. Workflow must:
   - successfully `gh release download libbox.aar`
   - produce a `libbox.aar` of size ~11.7 MB at expected path
   - complete the full `dotnet publish` + signing + upload sequence
   - upload the APK as a release asset (will overwrite the
     manually-uploaded one — that's fine, same content, same signing
     cert per Phase 6 Wave 32 setup)
3. **Asset count check**: `gh release view v2.35.0-r3 --json assets
   -q '.assets | length'` returns 14.

## Verification gate

- [ ] **Gate 1 — Build clean**: `dotnet build VPNRouter.sln -c Release` → 0 errors
- [ ] **Gate 2 — Tests green**: full suite passes (~1124/1128). No new tests required (workflow-only change).
- [ ] **Gate 3 — Docs**: this brief Outcome filled. `.github/SECRETS.md` updated. `.github/workflows/CLAUDE.md` may need a one-line note.
- [ ] **Gate 4 — Self-review**: N/A — diff is ~60 LOC of YAML + Markdown, no production code. No security-sensitive change (gh release download with `GITHUB_TOKEN` is the standard idiom, no privilege escalation).
- [ ] **Gate 5 — MCP verify**: N/A — no UI surface.
- [ ] **Gate 6 — Characterization diff**: N/A — not a god-file split.

Plus the CI-smoke gate from §"How" step 6: workflow_dispatch on v2.35.0-r3 must produce a fresh APK successfully.

## Outcome (filled 2026-05-19)

**Status**: **PASS** (with newly-surfaced follow-up — see below)

**Commits**:
- `e54e423` — brief
- `fdb715d` — implementation (workflow + SECRETS.md + CLAUDE.md)

**Pushed**: github + origin commit `fdb715d`

**Test deltas**: 0 (CI-only change, no production code)

**Files changed**: 3 · +125/-67 LOC

**Setup work** (outside diff): created internal release
`tooling-libbox-singbox-1.13.10`
(https://github.com/PavelLizunov/VPNRouter/releases/tag/tooling-libbox-singbox-1.13.10)
with `libbox.aar` (11.7 MB) as sole asset.

**Gate results**:
- [x] **Gate 1 — Build clean**: `dotnet build VPNRouter.sln -c Release` →
  0 errors, 0 warnings (clean after testhost lock cleared)
- [x] **Gate 2 — Tests green**: 1124 / 0 failed / 4 skipped / 1128 total
- [x] **Gate 3 — Docs**: brief Outcome filled, SECRETS.md "Internal
  tooling releases" section written with provisioning command +
  rotation procedure, CLAUDE.md secrets table updated
- [x] **Gate 4 — Self-review**: N/A. Diff is 125 LOC of YAML + Markdown,
  no production code. Standard `gh release download` idiom with the
  ambient `GITHUB_TOKEN` (default `contents:read` scope), no privilege
  escalation. Not security-sensitive.
- [-] **Gate 5 — MCP verify**: N/A — no UI surface.
- [-] **Gate 6 — Characterization diff**: N/A — not a god-file split.

**Extra gate from §"How" step 6 (CI smoke)**:
- [x] Workflow_dispatch on `main` (build-android.yml ref=main,
  version=2.35.0-r3) — run `26099691547`
- [x] **`libbox.aar provisioned: 11747194 bytes from release
  tooling-libbox-singbox-1.13.10`** logged at 13:17:51 UTC. Wave 32's
  specific change works end-to-end: the `gh release download` step
  successfully fetched the aar from the new tooling release.
- [-] **APK upload step did NOT run.** The build failed AFTER libbox
  provision succeeded, on the next step (`dotnet publish (android-arm64,
  signed)`) with:
    `error NU1102: Unable to find package
     Microsoft.NETCore.App.Runtime.Mono.linux-x64 with version
     (= 10.0.8). Nearest version: 9.0.0-preview.7.24405.7`

  Investigated: Microsoft has NOT published `.NET 10`-line Mono
  runtime packs for `linux-x64`, `win-x64`, OR `osx-x64` on nuget.org
  (last 10.x version: 9.0.0-preview.7 — they renamed/restructured
  between 9 and 10). Locally on the dev VM the build succeeds because
  the SDK install includes the runtime pack on disk; CI's clean restore
  hits nuget.org first and fails.

  This is an **ecosystem-level upstream issue**, latent since Phase 5
  Wave 23 (net8.0-android → net10.0-android36.0 bump in commit
  c33e372, 2026-05-18). Wave 26's `LIBBOX_AAR_BASE64` skip path
  masked this — the Android publish never ran in CI after Wave 23, so
  the NuGet restore failure never surfaced. Wave 32 unblocked the
  libbox step, which then surfaced the next blocker.

**Surprises encountered**:

1. **First workflow_dispatch picked the OLD workflow YAML**. I
   dispatched with `--ref v2.35.0-r3` thinking that would build
   v2.35.0-r3 — but `--ref` selects which COMMIT's workflow file to
   execute. v2.35.0-r3 is tagged before Wave 32's commit, so the OLD
   workflow YAML (using `LIBBOX_AAR_BASE64`) ran instead of my new
   tooling-release fetch. Fixed by re-dispatching with
   `--ref main -f version=2.35.0-r3`.
2. **Workflow expects `version` input WITHOUT `v` prefix** — the step
   "Resolve version from tag or input" line 122 builds `tag=v$VERSION`,
   so passing `v2.35.0-r3` produces `vv2.35.0-r3` (double v). Caught
   on the second dispatch attempt; corrected the third dispatch to
   `version=2.35.0-r3`.
3. **NU1102 on Mono.linux-x64 — Microsoft has not published .NET 10
   Mono runtime packs to NuGet yet** (see above). This is the root of
   the CI failure post-Wave-32, but it's a Wave 32b scope, not Wave
   32 scope.

**Follow-ups spawned**:

- **Wave 32b — NU1102 workaround for CI Android build**. Options:
  (a) Add local `dotnet` install dir as a NuGet source so the SDK-
      bundled Mono runtime pack is restorable (likely simplest);
  (b) Wait for Microsoft to publish 10.x Mono.linux-x64 packs to
      nuget.org (timeline unknown — could be weeks/months);
  (c) Pin Android target framework to `net9.0-android` until 10.x
      Mono packs ship (regression vs Wave 23 / Phase 5).
  Recommendation: try (a) first as a small workflow YAML patch.
  Effort estimate: 1-2h.
- **Until Wave 32b lands**: continue building APK locally on the
  dev VM via `build.ps1 -AndroidAlso -Upload` (the path used for
  v2.35.0-r3 manual APK upload).

**Rollback**:
- Workflow: `git revert fdb715d` (restores secret-based provisioning
  step, which silently skips anyway since `LIBBOX_AAR_BASE64` cannot
  be set).
- Tooling release: `gh release delete tooling-libbox-singbox-1.13.10
  --repo PavelLizunov/VPNRouter --yes` (no auto-update consumers, safe
  to delete).

**Lessons for methodology doc** (if any):

- `workflow_dispatch --ref <tag>` runs the workflow YAML from THAT
  tag, not from `main`. Always use `--ref main` for CI smoke tests
  of workflow-only changes. (Could add to the "CI smoke" section of
  the methodology doc.)
