# macOS + Linux shipped-package smoke matrix — DESIGN BRIEF

**Status:** design only (2026-06-02). Implements product-gap-audit **#135**.
Written as a brief (not shipped) because a new CI workflow needs a few live
iterations on real runners to go green, and doing that unattended risks leaving
red checks (rule #15). Land it `workflow_dispatch`-only first, get it green, then
add the `release` trigger. For maintainer review.

## Why

The Windows shipped binary already has a post-build smoke:
`test-windows-update.yml` (the "Auto-Update Integration Test (Windows)" check)
installs the previous build and exercises the live-update path. The cut-stable
**live-update gate** also runs this manually. **macOS and Linux have no
equivalent** — once `build-mac.yml` / `build-linux.yml` attach the .dmg / .deb /
AppImage, nothing ever installs them and checks they actually run. A packaging
regression (broken `.deb` postinst `setcap`, a missing runtime dependency, a
DMG that won't mount, an AppImage missing FUSE glibc symbols) would ship
undetected. The v2.31.7 `helper.cmd` bug (broke 100% of Windows upgrades, caught
7 days late by user reports) is the cautionary tale — the Mac/Linux packaging
path has the same blind spot.

## The headless constraint

`VPNRouter.App` is an Avalonia GUI — on a headless CI runner it needs a virtual
display. But `VPNRouter.CLI` ships in the same package and has a **headless
health command**: `VPNRouter.CLI doctor` runs `HealthCheck.RunAll()` and exits
`0` (ok) / `1` (warnings) / `2` (errors) — no VPN, no root, no display. That is
the reliable smoke signal. A GUI-launch smoke is possible under `xvfb-run`
(Linux) but is secondary; the CLI check is the gate.

## Design — `.github/workflows/smoke-packages.yml`

```yaml
name: Smoke shipped packages
on:
  workflow_dispatch:
    inputs:
      tag:
        description: 'Release tag to smoke (blank = latest)'
        required: false
  # add once green:  release: { types: [published] }
permissions:
  contents: read
jobs:
  linux-deb:
    runs-on: ubuntu-latest
    env: { GH_TOKEN: '${{ secrets.GITHUB_TOKEN }}' }
    steps:
      - name: Download .deb
        run: |
          TAG="${{ inputs.tag }}"; [ -z "$TAG" ] && TAG=$(gh release view --repo ${{ github.repository }} --json tagName --jq .tagName)
          gh release download "$TAG" --repo ${{ github.repository }} --pattern '*-linux-amd64.deb' --dir pkg
      - name: Install + verify postinst
        run: |
          sudo apt-get update
          sudo apt-get install -y ./pkg/*.deb       # pulls deps; runs postinst (setcap)
          test -x /usr/libexec/vpnrouter-update-helper   # postinst artifact present
          command -v VPNRouter.sh || ls /usr/lib/vpnrouter/VPNRouter.App
      - name: Headless health check
        run: |
          # locate the bundled CLI and run `doctor` (exit 0/1 ok; 2 = errors)
          CLI=$(find /usr/lib/vpnrouter /opt -name 'VPNRouter.CLI' -type f 2>/dev/null | head -1)
          "$CLI" doctor; code=$?
          [ "$code" -le 1 ] || { echo "doctor reported errors ($code)"; exit 1; }
      - name: (optional) GUI smoke under xvfb
        run: xvfb-run -a timeout 20 VPNRouter.sh || true   # non-fatal: just catch hard crash

  linux-appimage:
    runs-on: ubuntu-latest
    steps:
      - run: |
          # download AppImage, chmod +x, --appimage-extract (no FUSE on CI),
          # run squashfs-root/.../VPNRouter.CLI doctor

  macos-dmg:
    runs-on: macos-latest
    env: { GH_TOKEN: '${{ secrets.GITHUB_TOKEN }}' }
    steps:
      - name: Download + mount .dmg
        run: |
          gh release download "$TAG" --pattern '*-mac.dmg' --dir pkg
          hdiutil attach pkg/*.dmg -nobrowse -mountpoint /Volumes/VPNRouter
      - name: Verify bundle + headless check
        run: |
          APP="/Volumes/VPNRouter/VPNRouter.app"
          test -d "$APP/Contents/MacOS"
          codesign -dv "$APP" 2>&1 | head -1 || echo "(unsigned — expected until notarization)"
          "$APP/Contents/MacOS/VPNRouter.CLI" doctor; [ $? -le 1 ]
          hdiutil detach /Volumes/VPNRouter
```

## Things to verify when implementing (the iteration risk)

1. **Where the CLI lands** in each package. The `.deb` payload path (`/usr/lib/vpnrouter/`?)
   and the `.app/Contents/MacOS/` name must be confirmed from `build-linux.yml` /
   `build-mac.yml`, and the `find` adjusted. (This is the main reason it needs a
   live iteration or two — don't guess the path.)
2. **`doctor` exit code on a fresh box with no config**: HealthCheck on a
   never-run install may legitimately WARN (no config yet). Treat `<=1` as pass;
   only `2` (hard errors) fails the smoke. Confirm `doctor` doesn't exit `2`
   purely because no VPN is configured.
3. **AppImage on CI**: GitHub runners lack FUSE → use `--appimage-extract` and
   run the extracted binary, not the AppImage directly.
4. **macOS Gatekeeper**: an unsigned/un-notarized .app may be quarantined when
   downloaded via the API; `xattr -dr com.apple.quarantine` before launch, and
   keep the check to bundle-structure + CLI `doctor` rather than a full GUI
   launch.

## Rollout (avoid red-X churn — rule #15)

1. Land `smoke-packages.yml` as **`workflow_dispatch`-only**.
2. `gh workflow run "Smoke shipped packages" -f tag=v2.38.2` and iterate the
   path/exit-code details until all three jobs are green.
3. Only then uncomment the `release: { types: [published] }` trigger so every
   stable cut is smoke-tested automatically. Fold a note into the cut-stable
   skill (it already has the Windows live-update gate at Step 6.5; this becomes
   the Mac/Linux parallel).

## Risk

LOW for the product (a CI-only check, touches no shipped code). The only real
risk is CI noise if wired to `release` before it's reliably green — hence the
`workflow_dispatch`-first rollout above.

## Cross-references

- `.github/workflows/test-windows-update.yml` (the Windows analogue),
  `build-linux.yml`, `build-mac.yml`, `packaging/linux/*`,
  `VPNRouter.CLI/Commands/DoctorCommand.cs`, `cut-stable` skill Step 6.5.
