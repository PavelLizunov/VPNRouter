# Current state

Canonical live-facts summary. For the deep architecture tour see `CLAUDE.md`;
for history see `plans/`. When release or platform facts change, update this file.

## Releases

- **Current stable:** v2.38.0
- **In-flight candidate:** v2.38.2-r4 (rolling `-rN` prerelease; only one is
  visible at a time)
- Release policy: rolling `-rN` candidates, stable cut on explicit maintainer
  command after a verification + live-update gate. See `CLAUDE.local.md`.

## Platforms and how each is built

| Platform | Built by | Notes |
|---|---|---|
| Windows (x64 ZIP) | locally via `build.ps1 -Upload` | attached to the GitHub release; NOT built in CI |
| macOS (DMG) | GitHub Actions `build-mac.yml` on a `v*` tag | not Developer ID signed / notarized yet |
| Linux (.deb / AppImage / tar.gz) | GitHub Actions `build-linux.yml` on a `v*` tag | `.deb` postinst `setcap` for passwordless TUN |
| Android (arm64 APK) | locally (CI blocked on NU1102) | `build-android.yml` is `workflow_dispatch`-only |

## Known limitations (current)

- **Fail-closed leak protection is Windows-only.** macOS and Linux use a no-op
  firewall manager, so `block_on_vpn_fail` has no backstop off Windows
  (tracked: `plans/product-gap-audit-2026-05-30.md` P0).
- **Desktop binaries are unsigned** — no Windows Authenticode, no macOS
  notarization. Integrity is via `.sha256` sidecars only (tracked: audit P0).
- **Android CI is blocked** on a .NET preview runtime-pack issue (NU1102); APKs
  are built locally until the SDK ships GA.

## One-liner install

- Linux: `curl -fsSL https://vpn.ninitux.com/install.sh | sudo sh`
- macOS: `brew install --cask pavellizunov/vpnrouter/vpnrouter`
- Windows: `iwr -useb https://vpn.ninitux.com/install.ps1 | iex`
