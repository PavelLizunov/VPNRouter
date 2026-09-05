# Security Review: sing-box-vpnctl Migration & Packaging Hardening

**Scope**: `diff b7ce0e4f..e161371e` (`origin/main` to `dsh/upgrade-singbox-vpnctl-1.14`)
**Reviewed Subsystems**: Desktop packaging (`build.ps1`, `build-mac.sh`), CI workflows (`sign-windows.yml`, `build-linux.yml`, `build-mac.yml`, `test-windows-update.yml`), Windows installer (`packaging/windows/install.ps1`), core DNS (`ConfigGenerator.Dns.cs`, `VPNConfig.cs`), characterization tests (`VpnctlPackagingCharacterizationTests.cs`, `AntiCensorshipDnsTests.cs`), Android legacy restoration.
**Overall Security Status**: No additional security finding confirmed within scoped review; packaging acceptance pending VPNCTL05; finding-free does not prove security. Prior packaging supply-chain defects VPNCTL-01, VPNCTL-02, VPNCTL-03 successfully mitigated.

---

## 1. Supply-Chain & Build Hardening (`build.ps1`)
- **Early Input & Traversal Validation (`build.ps1:122-137`)**:
  - Blocks `-SingBoxPath` when `-Upload` is active, preventing accidental release of untracked local binaries.
  - Strict regex enforcement for `-SingBoxVersion` (`^[0-9]+\.[0-9]+\.[0-9]+-vpnctl\.[0-9]+$`) rejects directory traversal (e.g. `../escape`).
  - Strict hex check for `-SingBoxSha256` (`^[0-9a-fA-F]{64}$`).
- **Cache Integrity & Autoselect Removal (`build.ps1:330-382`)**:
  - Removed silent fallback to `publish\sing-box-lx.exe` (VPNCTL-02).
  - SHA256 integrity verification (`Get-FileHash`) executes unconditionally before archive expansion (`build.ps1:363-367`), eliminating cache-tampering vulnerabilities (VPNCTL-03).
  - Destination extraction directory is wiped prior to unpacking (`build.ps1:369-370`).

## 2. Signing Workflow Controls (`.github/workflows/sign-windows.yml`)
- **Elimination of Unpinned Core (`sign-windows.yml:101-106`)**: Removed invocation of `build-singbox-lx.ps1` and override `-SingBoxPath` (VPNCTL-01). Builds default verified `sing-box-vpnctl` 1.14.0-vpnctl.3.
- **Controlled Draft Release Promotion**: Workflow invokes `build.ps1` without `-Upload` (`sign-windows.yml:104`); `build.ps1` has no `upload_to_release=false` parameter. Authenticode verification (`sign-windows.yml:138-175`) gates only explicitly enumerated binaries in `$required` (core executables and specific managed DLLs, not unverified supplemental libraries like `libcronet.dll`). Separate SignPath submission and release staging steps were never executed as verification within this scoped review.

## 3. Cross-Platform Core Pins (`build-linux.yml`, `build-mac.sh`)
- **Linux (`build-linux.yml:85-94`)**: Bundles official Linux AMD64 release pinned to SHA256 `3d7fdbbf68f75b74f2bb4451eb2a1ed3421ee3ab6bccfea93f16c0d3eca91e8e` and upstream SagerNet sing-box 1.13.14 archive SHA256 `f48703461a15476951ac4967cdad339d986f4b8096b4eb3ff0829a500502d697` (clarification: this digest verifies the upstream archive from which `libcronet.so` is extracted, not the standalone DLL/SO). Removed arbitrary Go build steps.
- **macOS (`build-mac.sh:50-63`)**: Fetches official Darwin Universal release with verified SHA256 `c71bf2fab29a00d70f8706eb2f71643e35438769cbbacdd566d7c0e6058be3b1`.

## 4. Anti-Censorship DNS Query Handling
- **ECH Suppression (`ConfigGenerator.Dns.cs:76-85`, `VPNConfig.cs:274-278`)**:
  - Emits local DNS rule matching `query_type: ["HTTPS", "SVCB"]` with `action: "reject"`.
  - Config locally rejects HTTPS/SVCB DNS queries for intended ECH suppression (forcing standard TLS 1.3 fallback); no empirical network proof of TSPU RST prevention.
  - Handled entirely inside sing-box DNS router without egress or DNS leak.

## 5. Android Legacy Restoration (Net-Zero Diff)
- Across commit range `b7ce0e4f..e161371e`, Android vpnctl migration was deferred (VPNCTL-04) and 4 core files were restored byte-exact to `b7ce0e4f` at commit `9fac7b58`. Preserves stable API 23 and legacy core 1.13.10.

## 6. Test Evidence & Known Benign Runs
- **Characterization Baseline Witness (Run 33992362512)**:
  - Ran `VpnctlPackagingCharacterizationTests.cs` against unpatched baseline `0f85cbfe`.
  - Confirmed 3 expected packaging RED failures (tampered zip cache accepted, corrupt zip tolerated, implicit LX auto-selected).
- **Fixed Packaging & Integration (Run 33992837242)**:
  - Windows packaging and contract suite clean GREEN (46 passed, 0 skipped).
- **Temporary Benign Test Harness**:
  - `VpnctlPackagingCharacterizationTests.cs:77-246` verifies hash validation, traversal rejection, and cache integrity using temporary directories without network downloads.

## 7. Review Boundaries & Untested Surfaces
- **No Local SDK / PowerShell Execution**: Per environment constraints, pwsh behavioral tests and dotnet SDK builds were not executed locally; evaluated via CI telemetry and static reachability.
- **Unexecuted Paths**: SignPath live API submission, physical HSM signing, and live network VPN handshakes were not executed during this review.

## 8. Audit Trail & Provenance History (Observed via `git log -S` and `git blame`)
- **`sign-windows.yml:104`**: Commit `f88b7889` (2026-09-05) removed legacy `tools/build-singbox-lx.ps1` and `-SingBoxPath publish/sing-box-lx.exe`, invoking `./build.ps1 -Version $env:VERSION -BundleSplitDriver` without `-Upload`. Authenticode gating loop (`sign-windows.yml:138-175`) dates to commit `aba58b78` (2026-08-13) and checks only the 9 explicitly enumerated paths in `$required`.
- **`build.ps1:122-137, 347-381`**: Commit `f88b7889` (2026-09-05) added `-SingBoxVersion` regex/traversal guards, strict hex `-SingBoxSha256` checks, and unconditional archive SHA256 verification before extraction. Upstream core transitioned from SagerNet to `PavelLizunov/sing-box-vpnctl` in `493e68fb` (2026-09-04) and bumped to `1.14.0-vpnctl.3` in `be0cf6f7` (2026-09-04).
- **`build-linux.yml:85-104`**: `libcronet.so` download block preserved upstream SagerNet sing-box 1.13.14 archive check (archive SHA256 `f4870346...` from commit `31e0cf68`, 2026-07-29); reorganized under `sing-box-vpnctl` bundling in commit `493e68fb` (2026-09-04).
- **`ConfigGenerator.Dns.cs:76-85`**: Anti-censorship `HTTPS`/`SVCB` rejection rule introduced in commit `493e68fb` (2026-09-04).
- **Android Restoration**: Commit `9fac7b58` (2026-09-05) restored 4 files byte-exact to `b7ce0e4f` (`origin/main`), retaining stable API 23 and 1.13.10 core while deferring Android vpnctl migration (VPNCTL-04).

---

## 9. Scoped Addendum: VPNCTL-05 Cronet Packaging Verification (Commit `99e113f0`)

- **Target Head**: `99e113f0338c9ff1c920224b77ddd8d0d5fd41c9` (PR #233; all 5 PR checks green).
- **Scope Note**: This section is an explicit, scoped addendum covering the remediation and verification of packaging defect VPNCTL-05; it does not represent an exhaustive security re-audit of the entire repository codebase.
- **Remediation Details (`f76b5ff4` + fixture `99e113f0`)**:
  - `build.ps1:381..435`: Implements isolated, verified retrieval of the upstream SagerNet sing-box 1.13.14 Windows amd64 archive pinned to SHA256 `f580782c6dd10f7691c66cea1d7c421813c5fbf7e305d1ee7ce0c3a40d196341`.
  - Verifies extracted `libcronet.dll` against strict SHA256 pin `c7434cfa93c3041321dd19111c4de6c52b8a9531a65661ba45425d3c51ec69e2`.
  - Copies *only* `libcronet.dll` and `LICENSE.libcronet` into the distribution directory; retains the official release executable `sing-box.exe` from `PavelLizunov/sing-box-vpnctl` v1.14.0-vpnctl.3.
- **Native CI Verification Evidence (Run 33994337036 / `/tmp/vpnctl-cronet-valid-native-success.log`)**:
  - Exact DLL hash `c7434cfa93c3041321dd19111c4de6c52b8a9531a65661ba45425d3c51ec69e2` verified inside both `VPNRouter-v2.49.3-win.zip` and `VPNRouter-update-v2.49.3-win.zip` (lines 742..749).
  - Positive native probe: `sing-box check -c` with `libcronet.dll` present in the binary directory exits with return code 0 (confirming dynamic library load and outbound structure initialization).
  - Negative control probe: execution without `libcronet.dll` exits with code 1 and outputs fatal error `cronet: library not found` (proving the check actively exercises the native DLL dependency).
  - Update deployment verification: `libcronet.dll` verified present in the staged update directory (line 981, xcopy exit 0).
- **Security & Operational Boundaries**:
  - Verification proves DLL integrity, filesystem bundling, dynamic linker loading, and outbound configuration parsing.
  - **Limitation**: This test does *not* prove or verify real TLS/QUIC network transport or end-to-end NaiveProxy data-plane traffic.
