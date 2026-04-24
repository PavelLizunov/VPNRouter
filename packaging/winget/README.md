# VPNRouter winget manifests

This directory holds the WinGet package manifests for
`PavelLizunov.VPNRouter`. Once merged into Microsoft's
[winget-pkgs](https://github.com/microsoft/winget-pkgs) repository,
Windows users can install VPNRouter with:

```powershell
winget install PavelLizunov.VPNRouter
```

Updates ship automatically via `winget upgrade` once each new stable
release gets a bumped manifest (see [Automation](#automation) below).

## Layout

```
manifests/p/PavelLizunov/VPNRouter/<version>/
├── PavelLizunov.VPNRouter.yaml                (version manifest)
├── PavelLizunov.VPNRouter.installer.yaml      (installer metadata)
└── PavelLizunov.VPNRouter.locale.en-US.yaml   (description + locale)
```

Exact directory structure mirrors Microsoft's
[winget-pkgs repo layout](https://github.com/microsoft/winget-pkgs/tree/master/manifests/p)
so a PR adds one copy-paste:

```
# From the root of a clone of microsoft/winget-pkgs:
cp -R <VPNRouter-repo>/packaging/winget/manifests/p/PavelLizunov \
      manifests/p/
git add manifests/p/PavelLizunov
git commit -m "New version: PavelLizunov.VPNRouter 2.27.2"
git push fork HEAD:PavelLizunov.VPNRouter-2.27.2
# Then open a PR on github.com/microsoft/winget-pkgs
```

## Validation

Before opening a PR, validate locally:

```powershell
winget validate --manifest packaging\winget\manifests\p\PavelLizunov\VPNRouter\2.27.2
```

Expected output: `Manifest validation succeeded.`

For a real install-dry-run on the current machine (without actually
installing), use `winget show` pointed at the local manifest (requires
Windows Terminal + winget 1.6+):

```powershell
winget install --manifest packaging\winget\manifests\p\PavelLizunov\VPNRouter\2.27.2\
```

## Automation (future)

Microsoft provides [`wingetcreate`](https://github.com/microsoft/winget-create)
to automate manifest bumps:

```powershell
# Install once:
winget install Microsoft.WingetCreate

# Each new stable release:
wingetcreate update PavelLizunov.VPNRouter `
    --version 2.28.0 `
    --urls https://github.com/PavelLizunov/VPNRouter/releases/download/v2.28.0/VPNRouter-v2.28.0-win.zip `
    --submit `
    --token $env:GITHUB_TOKEN
```

`--submit` forks microsoft/winget-pkgs under the authenticated user,
commits the new manifest, opens a PR automatically. 1-3 day review
cycle typical (humans + CI validators).

### CI automation (TODO)

Ideal state: a GitHub Actions workflow in this repo that runs on every
stable release, invokes `wingetcreate update --submit`, and PRs
automatically. Blocked on:

  1. Creating a dedicated GitHub account for the winget-pkgs fork
     (Microsoft's CI prefers not to have bot-account PRs)
  2. PAT with `contents:write` on that account

Once automated, the same rolling-release pattern we have for the APT
repo + Homebrew Cask tap applies here: push a stable tag, manifest
gets submitted within 5 minutes, Microsoft merges after review.

## Why a PR and not our own tap?

WinGet has no per-user tap concept (like Homebrew). The only install
source most users have configured is `winget` (Microsoft's central
repo). Submitting there is the canonical path.

For bleeding-edge / prerelease use, users can always grab the ZIP
directly from GitHub Releases or use the one-liner
`iwr -useb https://vpn.ninitux.com/install.ps1 | iex`.

## Why `NestedInstallerType: portable` and not `msi` or `burn`?

Our current Windows build ships a ZIP layout, not a proper MSI. That's
a larger packaging project (WiX authoring, MSI upgrade codes, proper
ARP registration via msiexec). WinGet's `zip` + `portable` installer
type handles our case: it extracts the ZIP, symlinks
`VPNRouter.App.exe` to a `vpnrouter` shim under
`%LocalAppData%\Microsoft\WinGet\Links\`, and registers an ARP entry
for uninstall tracking.

The in-app auto-updater and the one-liner `install.ps1` cover the
richer install flow (Start Menu shortcut, proper Program Files
install, service registration). WinGet is the "just let me try it
once" path.
