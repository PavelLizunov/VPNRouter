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

## Outcome (filled after merge)

(TBD post-implementation)
