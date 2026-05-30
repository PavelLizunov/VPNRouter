# VPNRouter product-gap audit

**Date:** 2026-05-30  
**Status:** read-only reconnaissance; no implementation scheduled  
**Purpose:** keep a review backlog for a later joint pass with Claude Code.

## Trigger

VPNRouter is already a production VPN client with users on Windows, macOS,
Linux and Android. The codebase has strong regression coverage and a careful
rolling-release process, but a mature product can still accumulate blind spots
outside the feature currently being developed.

This document records visible product, operational and supply-chain gaps. It is
not a claim that every item must be implemented. Each item should first be
confirmed against current product requirements and actual user demand.

## Executive summary

Highest-value review order:

1. Non-Windows desktop fail-closed behavior.
2. Desktop artifact signing and release provenance.
3. Android `libbox.aar` checksum pinning.
4. One-click local diagnostics export with fail-safe redaction.
5. Shipped-package smoke tests for macOS and Linux.

## P0 - Non-Windows desktop fail-closed protection

### Observation

Windows has real firewall management for `block_on_vpn_fail`. Non-Windows
desktop builds receive `NullFirewallManager`:

- `VPNRouter.Core/Platform/PlatformServices.cs`
- `VPNRouter.Core/Platform/macOS/NullFirewallManager.cs`

`PlatformServices.CreateFirewallFactory()` uses `NullFirewallManager` for every
`!PLATFORM_WINDOWS` build, not only macOS. The stub explicitly logs that traffic
may leak if sing-box crashes.

The Unix process scanner and monitor are functional despite the historical
`Mac*` names. The missing part is the firewall backstop.

### Why it matters

This is a product-promise gap. A VPN client should make fail-open behavior
explicit, especially when the UI exposes a leak-protection setting.

### Review questions

- [ ] Is `block_on_vpn_fail` visible or selectable on macOS and Linux today?
- [ ] Does the UI clearly state that the setting is unavailable when the active
      platform uses `NullFirewallManager`?
- [ ] Should Linux use `nftables`, `iptables`, or a capability-aware helper?
- [ ] Should macOS use a dedicated `pfctl` anchor?
- [ ] What cleanup strategy guarantees that stale rules cannot strand the user
      offline after an app crash or uninstall?
- [ ] Should unsupported platforms hide the toggle or show a disabled card with
      a short explanation?

### Acceptance direction

- [ ] Linux has an idempotent fail-closed implementation with crash cleanup.
- [ ] macOS has an idempotent fail-closed implementation or an explicit product
      decision documenting why it remains unsupported.
- [ ] UI capability detection matches actual platform behavior.
- [ ] Package install, uninstall and abnormal-exit scenarios are tested.

### Risk

High. Firewall mistakes can either leak traffic or leave the user offline.

## P0 - Desktop signing and release provenance

### Observation

The project has good corruption detection:

- release artifacts have `.sha256` sidecars;
- the updater checks hashes;
- `verify-release-integrity.yml` recomputes hashes;
- APT metadata is GPG-signed;
- Android APKs are signed.

However, Windows desktop binaries are not Authenticode-signed and macOS builds
are not Developer ID signed or notarized. Existing evidence:

- `plans/v2.31.10-av-firewall-compat.md`
- `packaging/CLAUDE.md`
- `plans/ci-audit-2026-05-17.md`

A checksum downloaded from the same GitHub Release as the archive detects
corruption, but does not independently prove origin if the release account or
channel is compromised.

### Review questions

- [ ] Is a Windows Authenticode certificate financially justified at the
      current download volume?
- [ ] Is Azure Trusted Signing suitable, or is a hardware-backed certificate
      preferable?
- [ ] Is Apple Developer ID plus notarization worth adding now?
- [ ] Should AppImage, tar.gz and DMG assets get detached signatures?
- [ ] Should GitHub artifact attestations be generated for CI-built assets?
- [ ] Should release notes publish signing-key fingerprints and verification
      instructions?

### Acceptance direction

- [ ] Windows release EXEs and installer payloads have Authenticode signatures.
- [ ] macOS DMG/app is Developer ID signed and notarized.
- [ ] Linux portable artifacts have a documented origin-verification path.
- [ ] Release verification documentation distinguishes checksum verification
      from signature verification.

### Risk

Medium implementation risk, high trust value.

## P0/P1 - Android `libbox.aar` integrity pin

### Observation

`build-android.yml` downloads `libbox.aar` from an internal tooling release and
checks only that the file is non-empty:

- `.github/workflows/build-android.yml`
- `tools/build-libbox-aar.sh`

The reproducible build script already computes SHA256 and writes a fingerprint,
but Android CI does not compare the downloaded AAR against a pinned expected
hash.

### Review questions

- [ ] Where is the canonical `libbox.aar` hash stored today?
- [ ] Should the expected hash live next to the pinned sing-box version in the
      workflow, or in a committed manifest?
- [ ] Should the manifest also record build tags, Go version and gomobile fork
      revision?
- [ ] Is an SBOM practical for the Go/JNI payload?

### Acceptance direction

**STATUS 2026-05-30: IMPLEMENTED in commit 817a9ed (.github/workflows/build-android.yml).**

- [x] Android CI fails closed on AAR checksum mismatch. (hard `exit 1` on
      SHA256 mismatch; the graceful skip now applies only to an absent asset)
- [x] The expected hash is committed and reviewable. (`LIBBOX_AAR_SHA256`
      pinned next to `LIBBOX_RELEASE_TAG`, value
      `239c4101465edcc270de75182764fb7566efd5fd284fbce35720fe70fd69f1a6`)
- [x] Rebuilding the AAR has an explicit hash-bump procedure. (documented:
      bump `LIBBOX_AAR_SHA256` + `LIBBOX_RELEASE_TAG` together)
- [~] The manifest records enough inputs to reproduce the native runtime.
      (covered by the committed `tools/build-libbox-aar.sh` recipe —
      sing-box/Go/sagernet-gomobile/NDK pins; a separate per-artifact
      `tools/libbox-cache/version.json` manifest is not yet committed — minor
      follow-up.)

Verified live: manual `Build Android APK` dispatch on 817a9ed — provision step
green with "SHA256 verified" logged; the run failed later only at the known
NU1102 `dotnet publish` (unrelated), and that throwaway dispatch run was deleted
to keep the commit surface clean.

### Risk

Low implementation risk, high leverage.

## P1 - One-click local diagnostics export

### Observation

Support diagnosis still often starts with asking users for `config.yaml`,
`current.json` and log files. A design note already exists:

- `plans/diagnostics-export-button.md`

There is also reusable groundwork:

- `VPNRouter.Core/Services/CrashReporter.cs`
- `VPNRouter.Core/Services/HealthCheck.cs`
- `VPNRouter.CLI/Commands/DoctorCommand.cs`

The correct destination has already been selected: local ZIP written to the
desktop, then the user attaches it manually. No hosted backend and no telemetry.

### Critical constraint

Redaction must fail safe. Configs can contain subscription tokens, VLESS UUIDs,
passwords, Reality short IDs and arbitrary custom-config secrets. A denylist is
not sufficient for structured YAML/JSON export.

### Review questions

- [ ] Can the collector share code with `HealthCheck` and `CrashReporter`?
- [ ] Should structured YAML/JSON sanitization keep only an allowlist of safe
      keys?
- [ ] Should logs use the existing best-effort regex scrubber as an additional
      layer?
- [ ] Should the ZIP include file sizes and timestamps for geo `.srs` files
      rather than the files themselves?
- [ ] Should the UI offer a preview before opening the folder?
- [ ] Which parts are desktop-only, and what is the Android equivalent?

### Acceptance direction

- [ ] Export produces a local ZIP and performs no upload.
- [ ] Fixture tests prove that known secrets never appear in exported output.
- [ ] Unknown structured fields default to redacted.
- [ ] Export includes version, OS, channel, connected state, health report,
      redacted config, redacted active sing-box config and bounded log tails.
- [ ] UI explains that the user should review the ZIP before sharing it.

### Risk

Medium. A redaction bug is a credential leak.

## P1 - Shipped-package smoke matrix for macOS and Linux

### Observation

Windows has an end-to-end updater integration workflow:

- `.github/workflows/test-windows-update.yml`

macOS and Linux workflows build packages and perform useful checks, but there
is no equivalent install-launch-connect-disconnect-update smoke matrix against
the shipped package shape.

### Review questions

- [ ] Can GitHub-hosted runners perform a minimal non-TUN launch smoke?
- [ ] Which VPN lifecycle checks require a dedicated machine or nested
      virtualization?
- [ ] Can `vpnrouter doctor` run after package installation on Linux?
- [ ] Can Linux test `.deb` install, capabilities, launch and uninstall?
- [ ] Can macOS test app-bundle launch and update staging before notarization?
- [ ] Should previous-stable-to-candidate update checks run on a dedicated Mac
      host and a Linux VM as part of the pre-cut gate?

### Acceptance direction

- [ ] Linux `.deb` smoke verifies install, `setcap`, launch, `doctor`, stop and
      uninstall cleanup.
- [ ] Linux AppImage smoke verifies launch shape.
- [ ] macOS ZIP/DMG smoke verifies app-bundle shape and launch.
- [ ] Any checks that cannot run in GitHub Actions are documented in the
      stable-cut checklist.

### Risk

Medium. Packaging failures often bypass unit tests.

## P2 - Public trust documents

**STATUS 2026-05-30: DONE in commit 05adb26.** Added `SECURITY.md` (private
disclosure via GitHub Security Advisories, not public issues), `PRIVACY.md`
(no-telemetry + exact local storage / network egress), `CONTRIBUTING.md`, and
`NOTICE.md` (third-party attribution; sing-box/libbox GPL-3.0, Zapret,
tg-ws-proxy, NuGet licenses).

### Observation

README files state that VPNRouter has no telemetry and provide a brief security
contact hint. The repository does not currently contain:

- `SECURITY.md`
- `PRIVACY.md`
- `CONTRIBUTING.md`
- `NOTICE` or a third-party licenses inventory

### Review questions

- [ ] Should `SECURITY.md` define a private disclosure channel instead of
      recommending a public issue for security reports?
- [ ] Should `PRIVACY.md` document local config storage, logs, crash reports,
      update checks and Free Config requests?
- [ ] Which bundled or downloaded third-party binaries require attribution?
- [ ] Is a compact `NOTICE.md` sufficient?

### Acceptance direction

- [ ] Security disclosure instructions are explicit.
- [ ] Privacy behavior is documented in one stable place.
- [ ] Third-party runtime dependencies are listed with source URLs and
      licenses.

### Risk

Low implementation risk, meaningful trust value.

## P2 - Dependency update automation and supply-chain hygiene

**STATUS 2026-05-30: DONE in commit 05adb26.** Added `.github/dependabot.yml`
(weekly grouped NuGet + github-actions updates; minor/patch grouped, majors
individual) and `tools/native-deps.md` (native runtime inventory + bump
procedure, libbox.aar SHA256 hard-gated). SBOM (CycloneDX/SPDX) emission
deferred.

### Observation

The repository has pinned package versions and GitHub Actions pinned to commit
SHAs, which is good. There is no visible Dependabot or Renovate configuration.

### Review questions

- [ ] Should Dependabot monitor NuGet, GitHub Actions and Go modules?
- [ ] Should updates be grouped to avoid noisy PR volume?
- [ ] Should stable dependencies receive scheduled review even when automated
      PRs are deferred?
- [ ] Should releases emit CycloneDX or SPDX SBOM files?

### Acceptance direction

- [ ] Dependency review cadence is explicit.
- [ ] Automated updates, if enabled, are grouped and gated by existing tests.
- [ ] Native dependencies such as sing-box, Zapret, tg-ws-proxy and `libbox.aar`
      have a lightweight inventory.

### Risk

Low.

## P2 - README and state-document drift

**STATUS 2026-05-30: DONE in commit 05adb26.** Added `CURRENT_STATE.md` (one
canonical doc for live release/platform/limitations facts) and corrected the
README.md + README.ru.md "all platforms built automatically via GitHub Actions"
claim (Windows ZIP + Android APK are built locally; only Mac+Linux are CI).
Automated pre-release consistency check (version examples / artifact counts)
deferred.

### Observation

Some repository documentation has accumulated historical statements. Example:
README says all desktop platforms are built automatically through GitHub
Actions, while Windows release ZIPs are produced locally by `build.ps1`.

Related references:

- `README.md`
- `README.ru.md`
- `plans/ci-audit-2026-05-17.md`
- `.claude_handoff.md`

### Review questions

- [ ] Which document is the canonical current-state summary?
- [ ] Can stable-cut automation verify README version examples and artifact
      counts?
- [ ] Should generated historical plans remain untouched while a small
      `CURRENT_STATE.md` carries the live facts?
- [ ] Are README English and Russian variants checked for semantic parity?

### Acceptance direction

- [ ] One concise document owns current release and platform limitations.
- [ ] README build/install claims match the actual release process.
- [ ] Cheap consistency checks run before release.

### Risk

Low, but drift wastes support and development time.

## P2 - Platform naming cleanup

### Observation

Unix-compatible implementations are named `MacProcessScanner`,
`MacProcessMonitor` and live under `Platform/macOS`, but they are also used on
Linux.

### Review questions

- [ ] Are the implementations intentionally shared across Unix desktop
      platforms?
- [ ] Would `UnixProcessScanner`, `PollingProcessMonitor` and
      `NullFirewallManager` under `Platform/Unix` reduce confusion?
- [ ] Are there any macOS-specific assumptions in `/bin/ps` parsing or process
      naming that need Linux fixtures?

### Acceptance direction

- [ ] Naming matches actual platform scope, or comments explicitly explain the
      shared Unix implementation.
- [ ] Linux and macOS parser fixtures cover representative `ps` output.

### Risk

Low. This is maintainability work, not a release blocker.

## Deferred product questions

These are worth discussion but are not confirmed gaps:

- [ ] Should diagnostics export replace or complement the current health-check
      report?
- [ ] Should Windows release building move fully into CI after code-signing is
      available?
- [ ] Should the app expose a small capability matrix so users know which
      protections are platform-specific?
- [ ] Should Android return to automatic release publication once the public
      .NET Android runtime-pack issue is resolved?
- [ ] Is Winget publication worth completing after desktop signing work?
- [ ] Should stable releases include a machine-readable SBOM asset?

## Suggested review session order

1. Confirm P0 facts directly in code and shipped binaries.
2. Decide the product contract for non-Windows fail-closed behavior.
3. Pick a signing/provenance target appropriate to current user volume.
4. Add the cheap Android AAR checksum pin.
5. Schedule diagnostics export as the next support-UX feature.
6. Fold accepted tasks into versioned implementation plans.

## Cross-references

- `plans/diagnostics-export-button.md`
- `plans/architecture-hardening-v2.39.md`
- `plans/critical-audit-targets.md`
- `plans/ci-audit-2026-05-17.md`
- `plans/v2.31.10-av-firewall-compat.md`
- `plans/android-polish-pass-2026-05-16.md`
- `VPNRouter.Core/Platform/PlatformServices.cs`
- `VPNRouter.Core/Platform/macOS/NullFirewallManager.cs`
- `.github/workflows/build-android.yml`
- `.github/workflows/test-windows-update.yml`
