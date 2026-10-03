# Current state

Canonical live-facts summary. For contract and architecture rules see `docs/agent-contract.md`
and `AGENTS.md`; for worker details see `docs/test-workers.md`; for history see `git show 6491be4c:plans/<file>`.
When release or platform facts change, update this file.

## Releases

- **Current stable:** v2.49.3.
- **Latest published candidate:** v2.50.0-r25 (prerelease, 2026-10-03), not
  promoted to stable. Its Windows binary passed the cold-cycle post-ship check
  on WINBRAT on the first attempt (`tools/post-ship-local.ps1`); the screenshot
  gate and the live-update gate were not run
  (see `plans/phase-r25-release-2.50.0-r25-2026-10-03.md`). r25 replaces the
  outlined "Simple" pill with a "Home" button (house icon and the word on a
  quiet tint) on desktop and Android, and fits the home screen into its window
  (tighter spacing, default height 820 px). r24 redid the Zapret and Telegram
  proxy pages in the look of the home screen (`Styles/HomeKit.axaml`,
  `StatusEmblem`) and put one segmented strip on the Tools, Servers, Public and
  Applications pages. Earlier candidates: r23, r22, r21 (connection fixes from
  the tester's report on r20), r20, r19, r18. The owner's next real update is
  the check of the driver-lock fix. Main declares `AppVersion` 2.50.0-r25.
- Release policy: rolling `-rN` candidates, stable cut on explicit maintainer
  command after verification and a live-update gate. See `docs/agent-contract.md`
  and the native release skills under `.dsh/skills/`.
- Publication order: accepted main commit -> immutable tag with verified SHA ->
  draft (`--verify-tag --latest=false`, also `--prerelease` for candidates) ->
  all platform staging and tag tests/update -> exact 18 assets/all hashes ->
  explicit tag-ref integrity dispatch with `auto_draft_on_failure=false` ->
  separately authorized publication. APT and full fixed-WINBRAT verification
  follow publication; dispatch missing integrity/APT runs at the release tag.
  Stable Homebrew notification is explicit after publication because draft
  staging suppresses it. Published corrections require a new version/tag.

## Platforms and how each is built

| Platform | Built by | Notes |
|---|---|---|
| Windows (x64 ZIP) | `build.ps1 -Upload` stages unsigned ZIPs; `sign-windows.yml` builds/signs from the exact tag when configured | existing immutable tag and draft required; any SignPath settings prohibit unsigned fallback; no overwrite or publication |
| macOS (DMG / ZIP) | GitHub Actions `build-mac.yml` on a `v*` tag | Apple Silicon; not Developer ID signed or notarized yet |
| Linux (.deb / AppImage / tar.gz) | GitHub Actions `build-linux.yml` on a `v*` tag | `.deb` postinst applies `setcap` for passwordless TUN |
| Android (ARM64 APK) | GitHub Actions `build-android.yml` on a `v*` tag | built and signed in CI; shipped to Releases and `vpn.ninitux.com/android` |

## Known limitations (current)

- **Fail-closed leak protection differs by platform.** Windows has a
  per-process firewall kill-switch and DNS hardening. macOS and Linux have
  default-off, full-tunnel-only global kill-switches (`pf` / `nftables`) plus
  best-effort DNS pinning. Their packet filters cannot implement the Windows
  per-process `block_on_vpn_fail` semantics in split mode.
- **Desktop binaries are unsigned** — no Windows Authenticode or macOS
  notarization. Integrity is verified with `.sha256` sidecars. The fail-closed
  SignPath workflow is prepared, but Windows signing remains owner-blocked on
  OSS enrollment, five repository secrets and the expected-signer variable; see
  `docs/code-signing-signpath-runbook.md`.
- **Android is ARM64-only** and distributed by direct APK download; there is no
  Play Store package yet. The in-app updater (`AndroidApp.AutoUpdate.cs`)
  delivers later signed APKs.

## One-liner install

- Linux: `curl -fsSL https://vpn.ninitux.com/install.sh | sudo sh`
- macOS: `brew install --cask pavellizunov/vpnrouter/vpnrouter`
- Windows: `iwr -useb https://vpn.ninitux.com/install.ps1 | iex`
- Android: download the APK from `https://vpn.ninitux.com/android`
