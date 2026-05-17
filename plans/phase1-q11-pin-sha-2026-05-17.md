# Phase 1 — Q11: Pin third-party GitHub Actions to commit SHAs

**Owner**: Claude session-id (Wave 2)
**Roadmap ref**: plans/v3.0-refactor-roadmap.md Phase 1 #11, plans/ci-audit-2026-05-17.md "Third-party actions pinned to floating @v3/@v4 tags"
**Effort**: 30 minutes
**Risk**: LOW (pinning makes builds deterministic; only risk is a typo in the SHA → action fails to resolve, caught immediately)

## Why
All third-party actions in our workflows use floating tags like `actions/checkout@v4`, `actions/setup-dotnet@v4`. A malicious upstream maintainer (or compromised account) could push a malicious tag. Industry best practice: pin to immutable commit SHAs.

OpenSSF Scorecard recommendation, GitHub security hardening guide, etc.

## What
Walk every `.yml` in `.github/workflows/`. For each `uses: <action>@<tag>`, replace `@<tag>` with `@<full-40-char-SHA> # <tag>` (comment preserves human-readable version).

Common actions to look up:
- `actions/checkout@v4` → resolve via `gh api repos/actions/checkout/git/ref/tags/v4 --jq '.object.sha'`
- `actions/setup-dotnet@v4` → same
- `actions/cache@v4` → same (Q9 adds this)
- `actions/upload-artifact@v4`
- `actions/download-artifact@v4`
- `softprops/action-gh-release@v1` (if used)
- `ncipollo/release-action@v1` (if used)
- `peaceiris/actions-gh-pages@v3` (if used)

Strategy: for each unique `uses:` value across workflows, look up the SHA ONCE, then sed-replace consistently.

```bash
# Helper one-liner per action
gh api repos/actions/checkout/git/ref/tags/v4 --jq '.object.sha'
```

If `gh api` doesn't return a direct SHA (peeled tag), follow the ref to commit:
```bash
gh api repos/actions/checkout/git/ref/tags/v4 --jq '.object.url' | xargs gh api --jq '.object.sha // .sha'
```

## Verification gate
- [ ] **Gate 1 — Build clean**: yaml syntax valid for every modified file
- [ ] **Sanity**: `grep -rE 'uses:.*@v[0-9]+\s*$' .github/workflows/` returns empty (no remaining unpinned actions)
- [ ] **Sanity**: every `uses:` ref has comment showing the version
- [ ] **Hook gates**: pre-commit + commit-msg both green
- [ ] **Workflow run**: next push triggers a workflow → it actually resolves the SHAs (not 404)

## Outcome

**Implemented 2026-05-17 (Wave 2)**: All third-party GitHub Actions across `.github/workflows/*.yml` pinned to immutable commit SHAs with version comments.

### Pin map (6 unique actions, 28 `uses:` lines, 7 workflow files)

| Action | Tag | SHA | Resolved version |
|---|---|---|---|
| `actions/checkout` | `@v4` | `34e114876b0b11c390a56381ad16ebd13914f8d5` | v4.3.1 |
| `actions/setup-dotnet` | `@v4` | `67a3573c9a986a3f9c594539f4ab511d57bb3ce9` | v4.3.1 |
| `actions/setup-go` | `@v5` | `40f1582b2485089dde7abd97c1529aa768e1baff` | v5.6.0 |
| `actions/setup-java` | `@v4` | `c1e323688fd81a25caa38c78aa6df2d33d3e20d9` | v4.8.0 |
| `actions/upload-artifact` | `@v4` | `ea165f8d65b6e75b540449e92b4886f43607fa02` | v4.6.2 |
| `actions/cache` | `@v4` | `0057852bfaa89a56745cba8c7296529d2fc39830` | v4.3.0 |
| `android-actions/setup-android` | `@v3` | `9fc6c4e9069bf8d3d10b2204b1fb8f6ef7065407` | v3.2.2 |

Each `uses:` rewritten as `uses: <action>@<SHA>  # <version>` so the human-readable version survives in the comment.

### Files touched (7)

- `.github/workflows/build-android.yml` — 7 pins (checkout, setup-dotnet, cache×2, setup-java, setup-android, upload-artifact)
- `.github/workflows/build-free-pool.yml` — 3 pins (checkout, setup-dotnet, cache)
- `.github/workflows/build-linux.yml` — 4 pins (checkout, setup-dotnet, cache, upload-artifact)
- `.github/workflows/build-mac.yml` — 4 pins (checkout, setup-dotnet, cache, upload-artifact)
- `.github/workflows/publish-apt.yml` — 1 pin (checkout)
- `.github/workflows/test-windows-update.yml` — 6 pins (checkout, setup-dotnet, cache×2, setup-go, upload-artifact)
- `.github/workflows/test.yml` — 3 pins (checkout, setup-dotnet, upload-artifact) — new file from Q8

### Coordination with concurrent Wave 2 tasks

Q9 was racing against this task — its `actions/cache@v4` additions (NuGet + sing-box + workload caches) landed mid-pin-pass and twice overwrote a few of my pinned lines back to `@v4`. Resolved by re-applying `replace_all=true` for `actions/cache@v4` after every detected overwrite. Final scan confirms zero remaining floating tags. Q8's new `test.yml` was pinned as part of this pass.

### Verification

- `grep -rEn 'uses:.*@v[0-9]+\s*$' .github/workflows/` returns empty (no remaining `@vN` tags).
- All 7 YAML files parse cleanly via `ConvertFrom-Yaml` (PowerShell `powershell-yaml` module).
- Every pinned line carries a `# vX.Y.Z` comment so future bumps stay human-auditable.
- Staged: `git add .github/workflows/` (7 entries).
- Not committed — handed off per brief.

### Follow-up

Brief notes a `dependabot.yml` to keep these SHAs current automatically — deferred to Phase 2 ops cleanup per the brief's own recommendation.

**Note**: leaves `actions/*` (first-party, GitHub-owned) UNPINNED is acceptable per Microsoft's own guidance (they're trusted) — but pinning is still the safer default. Pin all uniformly.

**Follow-up**: add a `dependabot.yml` to keep SHAs current automatically. Defer to Phase 2 ops cleanup.
