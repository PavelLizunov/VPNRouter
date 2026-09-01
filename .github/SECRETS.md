# GitHub Actions secrets

CI secrets used by the workflows under `.github/workflows/`. Each
entry: what consumes it, why it exists, and the one-time provisioning
command. Real secret values live only in the repo's Actions secret
store — never in the repo, never in plain text logs.

Add / rotate via: **Repo Settings → Secrets and variables → Actions
→ New repository secret**.

## Secrets index

| Name | Used by | Required for | Type |
|---|---|---|---|
| `GITHUB_TOKEN` | every workflow | release uploads, gh CLI calls | auto-injected, no action needed |
| `HOMEBREW_TAP_DISPATCH_TOKEN` | `build-mac.yml` | cross-repo dispatch to `PavelLizunov/homebrew-vpnrouter` on stable cuts | PAT, classic, `repo` scope |
| `ANDROID_KEYSTORE_BASE64` | `build-android.yml` | signing the release APK so existing installs accept updates | base64-encoded JKS keystore |
| `ANDROID_KEYSTORE_PASSWORD` | `build-android.yml` | unlocks the keystore + key during signing | plain string |
| ~~`LIBBOX_AAR_BASE64`~~ | retired Wave 32 | replaced by tooling-release pattern below (48 KB secret cap × 15.6 MB aar = impossible) | — |

## Internal tooling releases (Phase 7 Wave 32)

### What it is

`libbox.aar` is the sing-box gomobile binding (Go cross-compiled to a
Java-callable `.aar`) consumed by `VpnRouterService.java`. It is the
Android equivalent of the desktop `sing-box.exe` binary — same
upstream codebase, different transport (in-process JNI vs spawned
process). Built locally via the SagerNet gomobile fork; the resulting
~11.7 MB aar is gitignored (`VPNRouter.Android/Lib/libbox*.aar` rule
in `.gitignore`) because (a) it's a build artifact, (b) it's a
private build not yet ready for upstream redistribution, (c) it's
large enough to bloat the git history if committed.

Reproducible build script: `tools/build-libbox-aar.sh` (tracked in
the repo). The script invokes gomobile against
`tools/sing-box-upstream/`; the upstream submodule is private as of
Phase 7, so CI cannot regenerate the aar yet — release-asset
provisioning is the bridge until that flips public.

### Why a release asset instead of a secret

Phase 6 Wave 26 (2026-05-18) wired secret-based provisioning via
`LIBBOX_AAR_BASE64`. The design was fundamentally broken: GitHub
Actions secrets are capped at **48 KB**, and `libbox.aar` base64-
encoded is **~15.6 MB** — about 325× over the limit. Every
`gh secret set LIBBOX_AAR_BASE64` attempt returns HTTP 422
("Value is too large"). Wave 32 (2026-05-19) replaced the design
with the release-asset pattern below.

GitHub release assets have a 2 GB per-file cap and are downloadable
via the ambient `GITHUB_TOKEN` (no additional secret needed for the
download). The tooling release lives on the same repo as the
product; it doesn't pollute the user-facing release list because
the `tooling-*` tag prefix + non-`--latest` flag keeps it out of the
default "Latest" filter.

### Active tooling release

| Tag | Asset | sing-box version | Created |
|---|---|---|---|
| `tooling-libbox-singbox-1.13.10` | `libbox.aar` (~11.7 MB) | 1.13.10 | 2026-05-19 |

Referenced from `.github/workflows/build-android.yml`:

```yaml
env:
  LIBBOX_RELEASE_TAG: "tooling-libbox-singbox-1.13.10"
```

### Provisioning command (one-time per sing-box version)

Run on the dev workstation that just rebuilt `libbox.aar`:

```bash
gh release create tooling-libbox-singbox-<NEW_VERSION> \
  --repo PavelLizunov/VPNRouter \
  --prerelease \
  --title "Tooling: libbox.aar (sing-box <NEW_VERSION> gomobile binding)" \
  --notes "Internal CI asset. Fetched by .github/workflows/build-android.yml via 'gh release download' using GITHUB_TOKEN. Not user-facing — see .github/SECRETS.md for rotation procedure when upgrading sing-box version." \
  VPNRouter.Android/Lib/libbox.aar
```

**CRITICAL — must include `--prerelease`** even though tooling releases
aren't user-facing prereleases per se. Without `--prerelease`, GitHub
auto-marks the newest non-prerelease release as `Latest`. The first
`tooling-libbox-singbox-1.13.10` release was created without this
flag on 2026-05-19, which broke:

- `packaging/windows/install.ps1` one-liner: its `?per_page=30` query +
  "first non-prerelease" filter picked the tooling release, then failed
  to find `VPNRouter-v*-win.zip` (tooling release only carries
  `libbox.aar`) — `iwr -useb vpn.ninitux.com/install.ps1 | iex` was
  broken for ~3 hours.
- `publish-apt.yml`: workflow fired on `release` event, attempted to
  index a non-existent `.deb` asset, exited 1 (no actual damage to
  the APT gh-pages tree but a red CI run sits in the history).
- GitHub Releases page: showed tooling release as Latest, confusing
  to anyone visiting `https://github.com/PavelLizunov/VPNRouter/releases`.

Safe paths (verified via post-incident audit):
- In-app `UpdateChecker.GitHubReleaseSource`: filters by
  `TryParseSemVer(tag.TrimStart('v'))` — `tooling-*` tags fail parsing
  and get skipped. Unaffected.
- `build-mac.yml`, `build-linux.yml`, `build-android.yml`: all use
  `on: push: tags: 'v*'`. Tooling tags start with `tooling-`, don't
  trigger. Homebrew Cask wasn't bumped.

Recovery procedure if this happens again:

```bash
gh release edit tooling-libbox-singbox-<VERSION> --prerelease
gh release edit v<latest-stable> --latest
```

Then bump `LIBBOX_RELEASE_TAG` in `.github/workflows/build-android.yml`
to `tooling-libbox-singbox-<NEW_VERSION>` and commit.

### How CI consumes it

`build-android.yml` step `Provision libbox.aar from tooling release`:

```yaml
env:
  GH_TOKEN: ${{ secrets.GITHUB_TOKEN }}
  LIBBOX_RELEASE_TAG: "tooling-libbox-singbox-1.13.10"
run: |
  mkdir -p VPNRouter.Android/Lib
  gh release download "$LIBBOX_RELEASE_TAG" \
    --repo "$GITHUB_REPOSITORY" \
    --pattern "libbox.aar" \
    --output VPNRouter.Android/Lib/libbox.aar
  test -s VPNRouter.Android/Lib/libbox.aar
```

Graceful skip: if `gh release download` fails (release missing,
asset missing, token scope wrong) the workflow warns + records the
run as green without an APK artifact. This matches the
`ANDROID_KEYSTORE_BASE64` pattern from v2.32.2-r2 — keeps the
release CI list visually clean while Android infra is bedded in.

### Rotation (when sing-box bumps)

`libbox.aar` is a build artifact, not a cryptographic secret. Rotate
when:

- The pinned sing-box upstream version bumps (typically every minor
  release of `VPNRouter.Core/Services/SingBoxManager.cs`'s declared
  version constant).
- A security advisory affects the bundled Go stdlib / sing-box deps.

To rotate:

1. Rebuild `libbox.aar` via `tools/build-libbox-aar.sh` against the
   new sing-box upstream tag.
2. Create a new tooling release with the new tag (see "Provisioning
   command" above).
3. Bump `LIBBOX_RELEASE_TAG` in `.github/workflows/build-android.yml`.
4. Commit the workflow change. Next tag push picks up the new aar
   automatically.
5. After one stable cycle, delete the OLD tooling release (it stays
   in git history via the tag — `git checkout tooling-libbox-singbox-X.Y.Z`).

### Migrating from the retired LIBBOX_AAR_BASE64 secret

If `LIBBOX_AAR_BASE64` was ever set (it cannot be, due to size cap,
but a partial value may exist from a failed `gh secret set`), revoke
it via `gh secret delete LIBBOX_AAR_BASE64 --repo PavelLizunov/VPNRouter`.
No production impact — the workflow no longer reads this secret.

## `ANDROID_KEYSTORE_BASE64` + `ANDROID_KEYSTORE_PASSWORD`

One-time keystore generation procedure: see
`plans/vpnrouter-android-platform-parity-roadmap.md` Phase A
"One-time keystore setup".

Loss of the keystore is catastrophic — Android refuses APK updates
signed with a different key on the same `applicationId`
(`com.ninitux.vpnrouter`). Backup encrypted to multiple offline
locations. NEVER rotate the keystore for the same package id; if
rotation is unavoidable, ship a new package id and a migration
helper.

## `HOMEBREW_TAP_DISPATCH_TOKEN`

PAT (classic), `repo` scope, owned by `PavelLizunov`. Authorizes
`build-mac.yml` to cross-repo dispatch the `homebrew-vpnrouter` tap
when a stable cut publishes. Rotate yearly (GitHub PAT auto-expire
default) or when team membership changes.

## Adding new secrets

When introducing a new workflow secret:

1. Add an entry to the **Secrets index** table above.
2. Add a dedicated section with: what it is, provisioning command,
   how CI consumes it, rotation policy.
3. Reference this file from the workflow comment block where the
   secret is first read.
4. Update `.github/workflows/AGENTS.md` when the workflow-zone secret contract or inventory changes.
