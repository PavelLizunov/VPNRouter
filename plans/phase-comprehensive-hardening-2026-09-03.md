# Phase — Comprehensive Platform Hardening, Protocols & Release Integrity

**Owner**: DSH session `session-b7bb95fc-bbc1-4f52-8dfd-3eea0fae24de`
**Branch**: `dsh/fix-comprehensive-hardening`
**Accepted base**: `origin/main` head `ec1434fb`
**Roadmap ref**: matrix audit master plan / `plans/repository-matrix-audit-2026-09-02.md`
**Effort**: 1 day
**Risk**: MEDIUM (multi-subsystem platform, protocol, and release gate improvements)
**Blast radius**: Core protocols (`NaivePairing`, `FreeConfigs`, `ServerUriParser`), platform firewall managers (`LinuxFirewallManager`, `MacFirewallManager`, `MacDnsHardening`), Android platform (`VpnRouterService.java`, `MainActivity.cs`), and release scripts (`verify-release-integrity.yml`, `post-ship-verify.ps1`); unit tests in `VPNRouter.Tests`
**Rollback**: revert branch commits; restore prior implementations

## Why

Following the comprehensive 7-category repository matrix audit, several critical edge cases remain open:
1. `SEC-01` (NaivePairing): Step 3 UDP fallback routes traffic to any alive UDP server in the global subscription pool when paired server is down, risking privacy/cross-country deanonymization.
2. `FCP-01` & `FCP-02` (FreeConfigs): Pre-testing free configs uses direct unproxied TCP/DNS pings over the host NIC; proxies reflecting the host's real public IP are marked `Verified`.
3. `URI-01` (ServerUriParser): IPv6 bracket literals (`[2001:db8::1]`) can lead to double-bracketing in sing-box outbounds (`[[2001:db8::1]]`); port bounds are not strictly capped at 65535.
4. `FW-02` & `FW-03` (Unix Firewalls): Static paths in world-writable `/tmp` are vulnerable to symlink exploitation; non-zero exits from packet filter CLI commands are ignored, creating silent fail-open states.
5. `FW-04` (Mac DNS): Abrupt termination leaves macOS primary network service DNS set to `172.19.0.1`.
6. `AND-V61-01` & `AND-CRASH-01` (Android): Startup failure after `builder.establish()` leaks kernel TUN fd; mixing allowed/disallowed apps crashes `VpnService.Builder`.
7. `AND-PERF-01` (Android SAF): Synchronous SAF I/O on Android UI main thread causes ANR.
8. `FIND-01` & `FIND-02` (Release Gates): Missing expected platform assets only append to warnings, allowing incomplete releases; post-ship verification skips checking non-Windows checksums.

## What

- **Protocols & Free Configs**:
  - `NaivePairing.cs`: Restrict fallback to siblings sharing the same host/server IP, or fall back to TCP-only if no same-host UDP sibling is alive.
  - `FreeConfigTester.cs` & `TcpTlsProbe.cs`: Use DoH resolution to avoid DNS leaks on the host NIC during pre-testing.
  - `DeepVerifyProbe.cs`: Add host public IP reflection check to reject transparent proxies.
  - `ServerUriParser.cs` & `VlessUriParser.cs`: Strip surrounding brackets from IPv6 host strings and enforce port range `1..65535`.
- **Platform Hardening**:
  - `LinuxFirewallManager.cs` & `MacFirewallManager.cs`: Use secure temporary file creation (or unique randomly generated temporary paths with 0600 permissions) and check exit codes, throwing `InvalidOperationException` if packet filtering commands fail.
  - `MacDnsHardening.cs`: Persist pre-existing DNS servers in a local recovery state file so an abrupt crash can restore them.
- **Android Platform**:
  - `VpnRouterService.java`: Ensure `teardownTunnelResources()` is called in `catch (Exception e)` inside `startTunnel()`.
  - `VpnRouterService.java`: Ensure mutually exclusive app list population on `VpnService.Builder`.
  - `MainActivity.cs`: Offload SAF file reading/writing to background `Task.Run()`.
- **Release Integrity**:
  - `verify-release-integrity.yml`: Increment `ERRORS` and set `FAILED=1` if any required platform asset (Windows, macOS, Linux, Android) is missing.
  - `tools/post-ship-verify.ps1`: Verify SHA256 sidecars for all release assets.
- **Tests**:
  - Add comprehensive unit tests in `VPNRouter.Tests` covering all updated areas.

## How

1. Commit phase brief and verify baseline CI on `origin/main`.
2. Implement fixes in Core, Platform, Android, and CI tools.
3. Add covering unit tests.
4. Execute 3 verification iterations:
   - Iteration 1: Build & local code analysis.
   - Iteration 2: Adversarial swarm review (`opus-swarm`).
   - Iteration 3: Exact-head CI in GitHub Actions.
5. Record outcome and open PR.

### Tests written

- `NaivePairing_DeadSibling_FallsBackOnlyToSameHostOrTcpOnly`
- `DeepVerifyProbe_ReflectsHostPublicIp_RejectsVerification`
- `ServerUriParser_IPv6Literal_StripsBracketsAndValidatesPort`
- `LinuxFirewallManager_NonZeroExit_ThrowsInvalidOperationException`
- `MacFirewallManager_NonZeroExit_ThrowsInvalidOperationException`
- `PostShipVerifier_AllPlatformAssets_ChecksumVerified`

### Verification approach

Run unit tests and full GitHub Actions test suites (Ubuntu, Windows characterization, Go, grep).

## Verification gate

- [ ] **Gate 1 — Build clean**: Release solution build and Windows CLI publish complete with zero errors.
- [ ] **Gate 2 — Tests green**: all unit and characterization tests pass with zero failures.
- [ ] **Gate 3 — Docs**: outcome recorded with commit SHAs and test counts; `plans/` updated.
- [ ] **Gate 4 — Self-review**: multi-iteration adversarial review verifies all changes without regressions.
- [ ] **Gate 5 — UI verify**: N/A (headless UI and Android unit tests pass).
- [ ] **Gate 6 — Characterization diff**: existing characterizations pass.

## Outcome

**Status**: IN PROGRESS
**Commits**: brief commit pending
**Pushed**: pending
**Test deltas**: pending
**Files changed**: pending

**Gate results**: pending.
**Surprises encountered**: pending.
**Follow-ups spawned**: pending.
**Lessons for methodology doc**: pending.
