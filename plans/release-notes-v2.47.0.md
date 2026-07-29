# VPNRouter v2.47.0

Builds on v2.46.0. This release makes automatic server selection honest,
hardens the local control and update paths, moves the desktop application to
.NET 10, and fixes two macOS failures found during the rolling r1-r13 cycle.

## Highlights

### Verified server health and safer automatic selection

Server tests now distinguish the phase that failed instead of reducing every
problem to ping or a generic timeout. Quick and deep checks cover TCP reachability,
the VPN protocol, ordinary HTTPS, blocked-target canaries, and the final bandwidth
probe. Results are stored with a bounded lifetime and shown as plain RU/EN verdicts
on server rows.

- Auto selection excludes servers that are reachable but cannot pass the required
  VPN or blocked-target checks.
- A missing optional bandwidth result no longer invalidates an otherwise successful
  deep verification.
- Slow machines get a bounded adaptive wait for concurrent sing-box probes.
- Cancelling a test is now neutral: it does not mark servers blocked or remove them
  from the Auto pool for 12 hours.
- The Stop button can actually interrupt all four batch-test flows.
- Probe-only sing-box processes no longer make the main window falsely claim
  "Connected via service".
- Custom configs that use a WireGuard/AmneziaWG endpoint as the proxy egress now
  resolve the real endpoint tag instead of failing at sing-box startup.

### macOS connectivity and kill-switch fixes

- macOS now uses the sing-box gVisor TUN stack. On macOS 26.5 the system stack could
  create the utun interface and routes while silently blackholing application TCP.
  In the live A/B test, the same config timed out with system and returned HTTP 204
  in 0.6 seconds with gVisor. Windows and Linux keep the system stack.
- The authenticated post-start Clash API probe now carries the per-install bearer
  token. A healthy connection is no longer torn down after 15 seconds because the
  probe received HTTP 401, and AutoFailover no longer cycles through every server.
- The macOS pf kill-switch now owns a dedicated `com.vpnrouter/killswitch` anchor.
  Disable and shutdown flush only VPNRouter's anchor instead of reloading the whole
  pf ruleset and wiping runtime rules installed by other software. Legacy crash
  recovery remains supported.

### .NET 10 and a safe cross-major update

All desktop projects (App, CLI, Core, Service, tests and tools) now target .NET 10,
with SDK 10.0.301 pinned by `global.json` and used by CI.

The in-app Windows update package now carries the complete .NET 10 runtime in its
bootstrap payload. Without this fix, an existing v2.46.0/.NET 8 installation would
receive .NET 10 application DLLs but keep the old runtime and fail to start. Both
the GUI trampoline and `helper.cmd` update paths were exercised against a real
.NET 8 installation.

### Security hardening

- The local sing-box Clash API is protected by a random per-install bearer secret;
  every desktop and Android consumer authenticates its HTTP and WebSocket requests.
- Linux and macOS kill-switches arm only from explicit full-tunnel intent, never
  from an accidentally empty process list after a scan failure.
- Telegram proxy setup verifies the python.org runtime and every PyPI wheel against
  SHA-256 before executing downloaded code; mismatches fail closed.
- True-split diagnostics verify the Mullvad driver against its shipped checksum
  before load and name integrity mismatches instead of reporting a later opaque
  kernel error.
- AWG endpoint validation rejects incomplete keys, peers, addresses and ports before
  sing-box starts.
- A SignPath Windows signing workflow and enrollment runbook are included. Signing
  is not enabled until the external OSS enrollment and repository secrets exist.

### Reliability and status correctness

- AutoFailover commits a replacement only after the new tunnel has started. A user
  disconnect or failed replacement preserves the last explicitly selected server.
- Subscription fallback keeps both active-server selectors synchronized.
- Startup adoption uses the same ownership-filtered runtime detector as the regular
  status poll, so an unrelated sing-box process cannot produce a false connected
  state.
- Concurrent configuration applies are serialized through the shared lifecycle
  gate, and the Connecting spinner is released on every terminal start outcome.
- Mandatory LAN, loopback, link-local and private IPv6 exclusions are pinned for
  generated and injected configs; user exclusions are preserved.
- `awg://` and `amneziawg://` links are recognised at intake, while official builds
  still reject unsupported fork-only constructs with an actionable message.

### Android

- Tunnel creation logs the applied MTU, address and route materialization, making
  device diagnostics useful for route failures.
- Hardware e2e gates cover LAN bypass, disconnect recovery, DNS/HTTPS recovery and
  unexpected auto-restarts.
- Removed per-tick `protect()` false-alarm log spam from connection statistics.
- Android build and signing workflows require the complete AppVersion/tag match,
  including prerelease suffixes, and pin the same .NET 10 SDK contract.
- Russian settings terminology and several Android/Desktop parity strings were
  corrected.

### UI, diagnostics and maintenance

- RU/EN server-health verdicts explain whether the host, protocol, blocked target
  or bandwidth phase failed.
- A broad localization review removed mixed-language labels, fixed terminology and
  pluralization, renamed Free configs to Public configs, and removed decorative
  emoji from product strings.
- Diagnostics now include antivirus/integrity state and the update log for the
  "application disappeared after reboot" and failed-upgrade classes.
- Removed unreachable Play Store update scaffolding and the hidden emergency-test
  CLI; shared deep-verify and executable-path plumbing replaced duplicate copies.

## Platform packages

- Windows: full ZIP and in-app update ZIP, each with SHA-256.
- macOS: DMG and ZIP, each with SHA-256.
- Linux: Debian package, AppImage and tar.gz, each with SHA-256.
- Android: signed APK and SHA-256 attached during the stable cut.

## Verification

- Candidate `v2.47.0-r13`: Windows tests/update workflow, macOS build and Linux
  build all green; 14 desktop assets verified.
- Stable preflight: .NET 10 Release build completed with 0 errors; mandatory
  v2.28 regression set passed 21/21.
- Live update gate on the isolated `windows-brat` VM: a clean v2.46.0/.NET 8
  install updated to v2.47.0-r13; `helper.cmd` copied 279 files with
  `xcopy exit=0`; CoreCLR changed from 8 to 10; `doctor` reported 2.47.0-r13;
  the install receipt was consumed on clean launch.
- Updated Windows application rendered normally, connected and disconnected using
  the saved test profile, and produced no ERR/Exception/FATAL log entries.
- macOS full-tunnel TCP and the authenticated post-start probe were verified on
  Apple Silicon hardware and then soaked on the final prerelease.

## Notes

- The gVisor TUN change is macOS-only; Windows and Linux retain the system stack.
- Windows code signing is prepared but not active in this release pending SignPath
  enrollment.
