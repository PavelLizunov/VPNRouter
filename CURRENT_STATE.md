# Current state

Canonical live-facts summary. For the deep architecture tour see `CLAUDE.md`;
for history see `plans/`. When release or platform facts change, update this file.

## Releases

- **Current stable:** v2.38.2
- **In-flight candidate:** v2.39.0-r2 (rolling `-rN` prerelease; only one is
  visible at a time) — one-click diagnostics export
- Release policy: rolling `-rN` candidates, stable cut on explicit maintainer
  command after a verification + live-update gate. See `CLAUDE.local.md`.

## Platforms and how each is built

| Platform | Built by | Notes |
|---|---|---|
| Windows (x64 ZIP) | locally via `build.ps1 -Upload` | attached to the GitHub release; NOT built in CI |
| macOS (DMG) | GitHub Actions `build-mac.yml` on a `v*` tag | not Developer ID signed / notarized yet |
| Linux (.deb / AppImage / tar.gz) | GitHub Actions `build-linux.yml` on a `v*` tag | `.deb` postinst `setcap` for passwordless TUN |
| Android (universal APK) | unsigned locally, **signed in CI** | `build-android.ps1` builds unsigned (NU1102 blocks a clean CI build); `sign-android.yml` signs it with the `ANDROID_KEYSTORE_BASE64` secret + uploads. Shipped to releases + `vpn.ninitux.com/android`. |

## Known limitations (current)

- **Fail-closed leak protection is Windows-only.** macOS and Linux use a no-op
  firewall manager, so `block_on_vpn_fail` has no backstop off Windows
  (tracked: `plans/product-gap-audit-2026-05-30.md` P0).
- **Desktop binaries are unsigned** — no Windows Authenticode, no macOS
  notarization. Integrity is via `.sha256` sidecars only (tracked: audit P0).
- **Android full build can't run on hosted CI** (NU1102: .NET 10 withdrew the
  host Mono runtime pack for every runner OS). Worked around: the APK is built
  unsigned locally and **signed in CI** (`sign-android.yml`) with the keystore
  secret, then shipped to the release + `vpn.ninitux.com/android`. An in-app
  updater (`AndroidApp.AutoUpdate.cs`) delivers future APKs. Revisit the full
  CI build when .NET 10 GA's its Android workload + host Mono pack on nuget.org.

## One-liner install

- Linux: `curl -fsSL https://vpn.ninitux.com/install.sh | sudo sh`
- macOS: `brew install --cask pavellizunov/vpnrouter/vpnrouter`
- Windows: `iwr -useb https://vpn.ninitux.com/install.ps1 | iex`
- Android: download the APK from `https://vpn.ninitux.com/android`
