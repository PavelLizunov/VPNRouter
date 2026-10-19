# Current state

Canonical live-facts summary. For contract and architecture rules see `docs/agent-contract.md`
and `AGENTS.md`; for worker details see `docs/test-workers.md`; for history see `git show 6491be4c:plans/<file>`.
When release or platform facts change, update this file.

## Releases

- **Current candidate:** v2.50.5-r2 (2026-10-19, prerelease, tag on main `697d4ba7`; Latest stays v2.50.4).
  A coloured protocol icon for each server: in the Subscribe and Servers lists (r1) and in the compact home mode,
  the current-connection card and the Change picker, desktop and Android (r2; `ProtocolGroups` in Core).
  Evidence: `plans/phase-protocol-icons-2026-10-19.md`. Main declares `AppVersion` 2.50.5-r2.
- **Current stable:** v2.50.4 (2026-10-19), cut from the candidate v2.50.4-r2
  (same tree; `AppVersion` only loses the suffix) after the owner tested r2 and
  approved the release (tag on main `8ccffed4`, Latest). The tray icon is registered again: since v2.50.0 (Avalonia
  12.1.3 creates the icon only for a `TrayIcon.Icons` entry) there was no tray icon and a
  closed window was unreachable. On Windows a `TrayGuard` checks the icon before hiding
  and minimizes to the taskbar when it cannot be shown; a one-time balloon says where the
  icon is. Also after v2.50.3: the home problems card with a one-button fix, the MTU
  probe fix and video guides (v2.50.4-r1). Evidence of the candidates:
  `plans/phase-tray-icon-guard-2026-10-18.md` and
  `plans/phase-home-problems-fix-2026-10-17.md`; the cut:
  `plans/phase-stable-2.50.4-2026-10-19.md`.
  Not verified anywhere: the tray on Linux, macOS and Windows 11.
- **Previous stable:** v2.50.3 (2026-10-11), cut from the candidate v2.50.3-r1
  (same tree; `AppVersion` only loses the suffix) on the owner's order for a quick
  fix release. One fix after v2.50.2: a geo rule-set file that is damaged on disk
  but keeps its size (`geosite-ru.srs`, `geoip-ru.srs`; used by "Russian traffic
  direct") no longer makes the core exit on every connect. `RuleSetFile.IsValidBinary`
  checks the content, `GeoDataDownloader` downloads a fresh copy and never installs
  a damaged one; without a good copy the config is generated without the bypass.
  Reproduced on WINBRAT with v2.50.2 and verified there with the candidate,
  including the live update from v2.50.2 with a zeroed file. Evidence of the
  candidate: `plans/phase-geo-ruleset-selfheal-2026-10-11.md`; the cut:
  `plans/phase-stable-2.50.3-2026-10-11.md`.
  Not verified anywhere: macOS, Linux and Android on real devices for this fix.
- **Before that:** v2.50.2 (2026-10-06), cut from the candidate v2.50.2-r1
  (same tree; `AppVersion` only loses the suffix) on the owner's order to release
  without further checks: the live update from v2.50.1 and the live scenarios were
  NOT run for this stable. Android fixes after v2.50.1: the release APK is
  arm64-only (45.3 MB instead of 174.9 MB), AmneziaWG and XHTTP subscription
  entries are accepted on Android (`SingBoxFeatures.EmbeddedCore`), the keyboard no
  longer hides the Home field or collapses the page below Android 15, the
  Subscription tab is one column, an in-app About overlay shows the embedded core
  version, the kebab menu results are visible in the Advanced screens, Export
  diagnostics opens the share sheet. Evidence of the candidate:
  `plans/phase-android-abi-awg-keyboard-about-2026-10-06.md`; the cut:
  `plans/phase-stable-2.50.2-2026-10-06.md`. Not verified anywhere: a real Android
  phone (Android 15+), a real AWG or XHTTP tunnel on Android.
- **Before that:** v2.50.1 (2026-10-06), cut from the candidate v2.50.1-r1
  (same tree; `AppVersion` only loses the suffix). v2.50.1 moves the
  program to the core `sing-box-vpnctl` v1.14.2-vpnctl.2 on desktop and Android
  (Android on `libbox-legacy`, API 21 build without naive; the engine start code
  uses the libbox 1.14 `CommandServer` API in `LibboxRuntime.java`). The evidence
  of the cut, including the live update from v2.50.0, is in
  `plans/phase-stable-2.50.1-2026-10-06.md`; the core update itself in
  `plans/phase-core-1.14.2-vpnctl2-2026-10-05.md`.
- **Earlier:** v2.50.0 (2026-10-04), cut from the candidate v2.50.0-r29.
  The evidence of that cut is in `plans/phase-stable-2.50.0-2026-10-04.md`.
- **Last candidate of the v2.50.0 line:** v2.50.0-r29 (prerelease, 2026-10-04). Its Windows binary passed the cold-cycle post-ship check
  on WINBRAT on the first attempt (`tools/post-ship-local.ps1`), the live
  scenarios of `tools/live` (cycles, per-server connects, ping with the VPN off
  and on, mode switches, auto-select, Other versions, all tabs, 600 fast
  clicks), the full Windows test suite (3355 passed, 0 failed) and a headless
  audit of 468 screen cells; macOS, Linux and Android on real devices were not run (see
  `plans/phase-r29-release-2.50.0-r29-2026-10-04.md`). r29 changes no
  connection behavior: it hides `usersecret`, `userpassword`, `authsecret` and
  `authtoken` in logs and crash reports, scrubs the About window error log and
  refuses a malformed host, secret or port for the Telegram proxy link; the
  READMEs were rewritten. r28 fixes what the live runs on r27 found: no pre-connect probe for a selected
  Hysteria2/TUIC/AmneziaWG server (connect 3.7-4.1 s from the home screen),
  auto-select works for a selected Hysteria2/TUIC server (urltest group of the
  same-protocol servers), the Other versions list always offers the three newest
  stable releases next to the newest candidates. r27 is the first block of the night pool: connect 3.6-3.9 s to a chosen
  server and 7.7 s from the home screen (r26: 7-12 s), stop 4.3-4.5 s, honest
  pings with the VPN on, failover that picks a reachable server, IPv6-only and
  AmneziaWG servers reported as "not probed", the Other versions list with
  older candidates on the experimental channel. r26 only fixed the cut-off round badge under the status emblem of the Zapret and Telegram pages. r25 replaced the
  outlined "Simple" pill with a "Home" button (house icon and the word on a
  quiet tint) on desktop and Android, and fits the home screen into its window
  (tighter spacing, default height 820 px). r24 redid the Zapret and Telegram
  proxy pages in the look of the home screen (`Styles/HomeKit.axaml`,
  `StatusEmblem`) and put one segmented strip on the Tools, Servers, Public and
  Applications pages. Earlier candidates: r23, r22, r21 (connection fixes from
  the tester's report on r20), r20, r19, r18. The owner's next real update is
  the check of the driver-lock fix.
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
