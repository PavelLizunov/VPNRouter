# Current state

Canonical live-facts summary. For the deep architecture tour see `CLAUDE.md`;
for history see `plans/`. When release or platform facts change, update this file.

## Releases

- **Current stable:** v2.42.0 (DNS-tunnel / Slipstream transport, YouTube/HTTP-2
  jumbo-MTU fix, Android reliability batch, DNS-leak fail-open, macOS + Windows
  kill-switch hardening).
- **In-flight candidate:** none published.
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

- **Fail-closed leak protection differs by platform.** Windows has the full
  per-process firewall kill-switch + DNS hardening. macOS has a full-tunnel-only
  pf kill-switch (default OFF) + DNS-to-TUN pinning (`MacDnsHardening`) — it can
  NOT do per-process `block_on_vpn_fail`. Linux (shipped) still uses a no-op
  firewall manager and no DNS hardening, so `block_on_vpn_fail` has no backstop
  there; a best-effort systemd-resolved DNS hardening is implemented but not yet
  released, and a Linux firewall kill-switch is still pending (tracked:
  `plans/macos-linux-functional-parity-plan-2026-06-15.md`).
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
