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

## Enforcement

Documented in `CLAUDE.local.md` so every new Claude session picks
this up without needing to re-read this file first.
