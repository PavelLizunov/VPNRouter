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
*(filled by agent after impl)*

**Note**: leaves `actions/*` (first-party, GitHub-owned) UNPINNED is acceptable per Microsoft's own guidance (they're trusted) — but pinning is still the safer default. Pin all uniformly.

**Follow-up**: add a `dependabot.yml` to keep SHAs current automatically. Defer to Phase 2 ops cleanup.
