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
| `LIBBOX_AAR_BASE64` | `build-android.yml` | provisions the gitignored `VPNRouter.Android/Lib/libbox.aar` so `dotnet publish` can resolve the `<AndroidLibrary>` reference | base64-encoded aar (~11.7 MB → ~15 MB b64) |

## `LIBBOX_AAR_BASE64`

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
Phase 6, so CI cannot regenerate the aar yet — secret-stored
provisioning is the bridge until that flips public in Phase 7.

### Provisioning command

Run once on the dev workstation that built the aar:

```bash
# Linux / WSL
base64 -w 0 VPNRouter.Android/Lib/libbox.aar > libbox.aar.b64

# macOS (no -w flag in BSD base64)
base64 -i VPNRouter.Android/Lib/libbox.aar | tr -d '\n' > libbox.aar.b64

# Windows PowerShell
[Convert]::ToBase64String([IO.File]::ReadAllBytes("VPNRouter.Android/Lib/libbox.aar")) `
  | Out-File -Encoding ASCII -NoNewline libbox.aar.b64
```

Then upload the file's contents (NOT a path) into the secret store:

1. Open https://github.com/PavelLizunov/VPNRouter/settings/secrets/actions
2. Click **New repository secret**
3. Name: `LIBBOX_AAR_BASE64`
4. Secret: paste the contents of `libbox.aar.b64` (single line, no
   trailing newline)
5. Click **Add secret**
6. **Delete `libbox.aar.b64` from disk** — never commit it. The file
   is not in `.gitignore` because we don't want to encourage the
   pattern.

### How CI consumes it

`build-android.yml` step `Provision libbox.aar from secret`:

```yaml
env:
  LIBBOX_AAR_B64: ${{ secrets.LIBBOX_AAR_BASE64 }}
run: |
  mkdir -p VPNRouter.Android/Lib
  echo "$LIBBOX_AAR_B64" | base64 -d > VPNRouter.Android/Lib/libbox.aar
  test -s VPNRouter.Android/Lib/libbox.aar   # fail if empty
```

Graceful skip: if the secret is unset (fresh fork, pre-provisioned
repo) the workflow warns + records the run as green without an APK
artifact. This matches the `ANDROID_KEYSTORE_BASE64` pattern from
v2.32.2-r2 — keeps the release CI list visually clean while Android
infra is still being stood up.

### Rotation

`libbox.aar` is a build artifact, not a cryptographic secret. Rotate
when:

- The pinned sing-box upstream version bumps (typically every minor
  release of `VPNRouter.Core/Services/SingBoxManager.cs`'s declared
  version constant).
- A security advisory affects the bundled Go stdlib / sing-box deps.

To rotate: rebuild via `tools/build-libbox-aar.sh`, re-run the
provisioning command above, paste into the same secret name (GitHub
overwrites by default). No code changes needed in CI — the next
build picks up the new bytes.

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
4. Update `.github/workflows/CLAUDE.md` "Secrets" table to mirror
   the index here.
