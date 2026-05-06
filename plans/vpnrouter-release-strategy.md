# VPNRouter Release Strategy (from 2026-04-20)

## Problem

Between v2.17.9 and v2.21.9 we shipped **~30 prereleases** inside roughly
two days. Each iteration-in-flight (bug → fix → user tests → another
bug) minted a new patch version. Result:

- GitHub Releases page cluttered with in-progress iterations.
- Auto-updater (with `Experimental` channel on) pings every
  prerelease, causing user-side update-nag loop.
- Storage footprint (each release ~30–50 MB × many) approached the
  5 GiB public-repo soft limit (had to prune 88 v1.x-v2.9.x releases
  on 2026-04-20).

## New scheme — rolling release candidates

Work on version `X.Y.Z` lands as successive **release candidates**
tagged `vX.Y.Z-r1`, `vX.Y.Z-r2`, etc. When a new rN+1 is ready, the
previous rN is **deleted** so only one in-flight prerelease is ever
visible. Once the user confirms it actually works, we cut **final
`vX.Y.Z`** (no suffix) as stable Latest.

### Flow

```
Start work on v2.22.0
  ↓
Ship fix A  →  publish v2.22.0-r1  (prerelease)
  ↓                    User tests → finds bug B
Ship fix B  →  publish v2.22.0-r2  (prerelease)
                        gh release delete v2.22.0-r1 --yes
                                  ↓ one visible prerelease at a time
Ship fix C  →  publish v2.22.0-r3
                        gh release delete v2.22.0-r2 --yes
                                  ↓
User: "works"
  ↓
Cut stable   →  publish v2.22.0   (non-prerelease, Latest)
                        gh release delete v2.22.0-r3 --yes
                                  ↓
                 Only v2.22.0 remains on the Releases page.
```

### Why this works

- **One in-flight prerelease at a time.** Users looking at Releases
  see: current stable Latest + optionally one active -rN.
- **Stable changelog.** Every stable release represents a real
  user-confirmed shippable state.
- **Same tag semantics.** Users on `Experimental` channel get the
  prerelease candidate; users on `Stable` only see the final.
- **Storage-friendly.** One vX.Y.Z artifact set per version,
  occasionally a transient -rN during active work.

### When to use `-rN` vs. bumping patch

- **`-rN`** — still working on the same logical release: user
  reported bug in the -rN, we're iterating on it. rN → rN+1.
- **Bump patch (`vX.Y.Z+1`)** — previous `vX.Y.Z` was already
  **cut as stable** and working for a while; a new issue surfaced
  that's independent.

If we've cut stable and the user comes back with "it broke again
in a different way" — new patch cycle starts as `vX.Y.(Z+1)-r1`.

### Hotfix emergency path

If current stable (`vX.Y.Z`) is broken for all users and we need
to ship a fix RIGHT NOW without a testing cycle: cut
`vX.Y.(Z+1)` directly as stable, skip the `-rN` phase. Accept the
risk, because the current stable is worse.

---

## Implementation checklist for Claude

When starting new work on a bug batch:

1. Open the relevant plan file or create one.
2. Pick the next `vX.Y.Z` number (usually current+1 patch, or
   current+1 minor if it's a new feature batch).
3. Ship the first iteration as **`vX.Y.Z-r1`** prerelease.
4. User tests → reports feedback.
5. Ship fix as **`vX.Y.Z-r2`**, then:
   ```bash
   gh release delete "vX.Y.Z-r1" --yes --repo PavelLizunov/VPNRouter
   git push origin :refs/tags/vX.Y.Z-r1  # delete remote tag
   ```
   (Git tag can stay if we want history — optional.)
6. Repeat steps 4-5 until user says "works".
7. Cut stable:
   ```bash
   # Same artifacts as last rN, re-tagged as vX.Y.Z
   # Either retag the commit or just copy the zip/tar/deb/AppImage
   # under the stable tag via build.ps1 -Version "X.Y.Z" -Upload
   gh release edit "vX.Y.Z" --prerelease=false --latest
   gh release delete "vX.Y.Z-rN" --yes --repo PavelLizunov/VPNRouter
   ```

### UpdateChecker compatibility

Our `UpdateChecker` parses tags via `Version.TryParse(tag.TrimStart('v'))`.
`2.22.0-r1` **does not parse** as `System.Version` (which expects
1–4 dot-separated integers). That's actually desirable: prerelease
candidates with `-rN` suffixes are silently ignored by the updater
unless we intentionally opt in.

For the experimental channel to still pick up -rN, we'd need to
parse `-rN` as pre-release metadata. **Out of scope for this doc**;
current behaviour is "experimental sees prereleases, -rN tags are
de-facto experimental too as long as we mark them prerelease — but
the internal UpdateChecker skips them because the version string
doesn't parse". For now that means -rN releases are visible on the
Releases page but not auto-offered — user downloads manually. This
is a feature, not a bug: it reduces update spam while we iterate.

---

## Verification gate (before promoting -rN to stable)

A `-rN` candidate is **READY for stable cut** only when ALL of these
are green. Cut itself is not autonomous (see `CLAUDE.local.md` lesson
v2.31.2).

1. `dotnet build VPNRouter.sln -c Release` — 0 errors.
2. Regression test suite green (xUnit + headless Avalonia).
3. Mac + Linux CI workflows green on the `-rN` tag.
4. `gh release view vX.Y.Z-rN` shows 12 assets (Win + Mac + Linux + sha256 sidecars).
5. **`test-windows-update.yml` green on the `-rN` tag** (runs
   automatically via `release: published` + `push: tags: v*-r*`
   triggers; see `plans/v2.31.10-update-integration-test.md`). Catches
   helper.cmd parser bugs of the v2.31.7-r10 class before they reach
   users.
6. MCP+UIA verification PASS where testable, or explicit
   `Core-only / not UI-testable` label.

If (5) is RED, **don't** promote to stable. Fix the helper.cmd
generation or whatever surfaced, ship `-r(N+1)`, re-run.

---

## Enforcement

Documented in `CLAUDE.local.md` so every new Claude session picks
this up without needing to re-read this file first.

---

## Release integrity gate (added after v2.29.0 fake-tag fiasco)

**Two-layer defense** against the bug class where a release tag says
one version but the bundled binary reports another:

### Layer 1 — local pre-build (`build.ps1` line ~54-83)

Before publishing anything, `build.ps1` reads `VPNRouter.Core/AppVersion.cs`
off disk and compares the `public const string Version` literal with the
`-Version` argument passed to the build script. Mismatch → hard abort
with remediation hint. Catches the original v2.29.0 cause: build script
run from a stale worktree whose AppVersion.cs hadn't been bumped.

### Layer 2 — post-publish CI (`.github/workflows/verify-release-integrity.yml`)

Triggered on every `release: published` AND `release: edited` event
(the latter catches `gh release upload --clobber` invocations after
publish, including manual operator uploads). Runs on `ubuntu-latest`
(all checks are file-inspection — no platform-specific tooling needed).

What it checks:

1. **Embedded AppVersion** — for each non-sha256 binary asset (Win zip×2,
   Mac dmg+zip, Linux deb+AppImage+tar.gz), extracts `VPNRouter.Core.dll`
   and scans its UTF-16 LE byte stream for the AppVersion.Version
   literal. Asserts the release tag's version (with leading `v` stripped)
   appears among the embedded matches.

2. **SHA256 sidecars** — recomputes sha256 of every binary, compares
   to the bundled `.sha256` sidecar. Two formats supported:
   `<hex>  <filename>\n` (Linux CI sha256sum) and bare `<hex>` (Windows
   build.ps1 PowerShell-native).

3. **Asset count** — current release-strategy convention is 12 assets
   per release (4 Win + 2 Mac + 6 Linux). Missing assets are a SOFT
   warning, not a failure: parallel CI may still be uploading at the
   time of the `release: published` event. Each subsequent
   `release: edited` re-runs the workflow until all 12 are present.

On failure (version mismatch or sha256 mismatch — these are catastrophic):
- Marks the release as a **draft** (hides from users — they can't
  download or browse to it).
- Prepends a `<!-- verify-release-integrity: FAILED -->` banner with
  the specific mismatch detail to release notes.
- Posts the same detail to the workflow run's step summary so
  maintainers see it without hunting.

### Loop prevention

The failure handler edits release notes, which fires `release: edited`,
which would re-trigger the workflow. The order of operations prevents
loops:

1. `gh release edit --draft=true` fires `release: unpublished` (NOT in
   our listener) — does not re-trigger.
2. `gh release edit --notes-file <banner>` fires `release: edited`. The
   workflow's "echo-loop guard" at job start checks both:
   - is the release currently a draft? (step 1 made it so)
   - do the notes already carry the FAILED marker? (step 2 added it)

   If both true → skip. The guard only short-circuits the echo path —
   if a maintainer un-drafts the release for re-verification, the
   "is draft?" check evaluates false and verification runs fresh.

`workflow_dispatch` always runs (operator override), and accepts an
`auto_draft_on_failure` boolean input to dry-run without modifying
the release (default `true`).

### Adding to the rolling-rN policy

Update step 5 in the rolling-rN flow (above) to include the post-publish
gate:

> 5. Repeat steps 4 [user testing] until either the user confirms or
>    the verification gate flags an issue. **`verify-release-integrity`
>    runs on every publish/edit and will draft the release if the
>    embedded AppVersion or sha256 doesn't match the tag.** A drafted
>    release means users won't see the broken candidate; fix and ship
>    `-r(N+1)` instead of unflagging.

### Hotfix emergency path

If the integrity gate flags a release that is genuinely correct (e.g.
the workflow has a regression, false-positive on a new asset format),
the manual escape hatch is:

```bash
# Remove the FAILED banner
gh release edit vX.Y.Z-rN --notes "<original notes>" --repo PavelLizunov/VPNRouter
# Un-draft
gh release edit vX.Y.Z-rN --draft=false --repo PavelLizunov/VPNRouter
```

Re-running `verify-release-integrity` via `workflow_dispatch` is the
preferred path (forces a re-check after asset corrections).
