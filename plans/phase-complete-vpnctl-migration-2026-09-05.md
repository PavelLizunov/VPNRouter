# Phase — Complete sing-box-vpnctl Migration and Packaging Hardening

**Approval**: User approved 2026-09-05 (MicroSpec: complete packaging migration to official `PavelLizunov/sing-box-vpnctl` 1.14.0-vpnctl.3)
**Author / Lead**: Gemini (sole code/test/doc author), Lead review Git
**Branch / Base**: `dsh/upgrade-singbox-vpnctl-1.14` (PR #233) / `origin/main` at `b7ce0e4f`
**Integration Note**: PR #240 (`ae352fdb`) contains approved NIGHT fixes not merged; compatibility integrated source verification required
**Current Head**: verified snapshot `9fac7b58` (not pretend docs SHA known; commit `9fac7b58ad0506fb99f40fcdaacd3235d9618993` restores Android 4 files byte-exact to base `b7ce0e4`; Android not broken anymore, restored legacy green: run 33991832141 SUCCESS, artifact 9976917773, 86335107 bytes, tooling-libbox-singbox-1.13.10 hash `239c4101465edcc270de75182764fb7566efd5fd284fbce35720fe70fd69f1a6`, minSdk 23 preserved, no install; VPNCTL-04 remains DEFERRED, no resolved claim; full vpnctl desktop Windows/Linux/macOS remains goal; backup branch `dsh/deferred-android-vpnctl-api-2026-09-05` `f4d5265cd55e91fffefb1789cd2d445b4cd1f931` pushed)
**Risk**: MEDIUM (packaging pipeline modernization; eliminating legacy third-party fork paths)
**Rollback**: Safe task commit revert review (human/lead review of git revert, no automatic rollback)

## Why

1. Deprecate legacy third-party `Leadaxe/sing-box-lx` fork across all packaging pipelines in favor of official `PavelLizunov/sing-box-vpnctl` v1.14.0-vpnctl.3.
2. Address P1 defect VPNCTL-01: `.github/workflows/sign-windows.yml:104-106` compiles from Leadaxe source (`tools/build-singbox-lx.ps1`) and passes explicit `-SingBoxPath publish/sing-box-lx.exe`, signing a legacy fork instead of official release assets.
3. Address P1 defect VPNCTL-02: `build.ps1:320-326` silently prefers `publish\sing-box-lx.exe` if present, bypassing official release downloads and SHA256 integrity verification.
4. Enforce immutable release identity across desktop Windows, Linux, and macOS (full vpnctl migration goal) while keeping legacy Android (API 23 unchanged, migration deprioritized/deferred per owner answer in ask android-vpnctl-minsdk; expected legacy build verification not new core) and retaining deliberate local `-SingBoxPath` overrides for development.

## What

1. Eliminate signed Windows legacy path in `.github/workflows/sign-windows.yml:104-106` (remove `build-singbox-lx.ps1` call and hardcoded `publish/sing-box-lx.exe`).
2. Update `build.ps1` to remove automatic preference for `publish/sing-box-lx.exe`; retain `-SingBoxPath` custom local override with verified release identity.
3. Standardize on official `PavelLizunov/sing-box-vpnctl` v1.14.0-vpnctl.3 with fixed platform SHA256 hashes across desktop targets (full vpnctl goal):
   - Windows amd64: `8094929df6c4b061dc9c360b1641474d41bdea16845d604a26d3721feefc6f74` (`sing-box-1.14.0-vpnctl.3-windows-amd64.zip`)
   - Linux amd64: `3d7fdbbf68f75b74f2bb4451eb2a1ed3421ee3ab6bccfea93f16c0d3eca91e8e` (`sing-box-1.14.0-vpnctl.3-linux-amd64.tar.gz`)
   - macOS universal: `c71bf2fab29a00d70f8706eb2f71643e35438769cbbacdd566d7c0e6058be3b1` (`sing-box-1.14.0-vpnctl.3-darwin-universal.zip`)
   - Android: Deprioritize/defer vpnctl migration per owner answer in ask android-vpnctl-minsdk. Keep legacy Android; Android API 23 unchanged; expected legacy build verification not new core. Backup branch `dsh/deferred-android-vpnctl-api-2026-09-05` (`f4d5265cd55e91fffefb1789cd2d445b4cd1f931`) pushed. Android VPNCTL-04 remains DEFERRED, not resolved and not desktop-blocking; existing failure evidence preserved (reference AAR hash `471908107fb68de65f50cc8898e193b832b2ae12f0dfe9ee93d73f0b27f1a991`). Full vpnctl desktop Windows/Linux/macOS remains goal.
4. Register P1 defects VPNCTL-01, VPNCTL-02, and VPNCTL-04 in `plans/OPEN-DEFECTS.md`.
5. Run in safe non-publishing mode (`upload_to_release=false`) on existing CI (`build-mac`, `build-linux`, `build-android`). Future agent must inspect gates.
6. Guardrails: no merging protected main, no PR merge, no release publication, no git tags, no real device install, no SDK control plane or infrastructure edits.

## How

1. Register VPNCTL-01 and VPNCTL-02 under `## Open` in `plans/OPEN-DEFECTS.md` with exact source evidence, and record VPNCTL-04 following CI findings.
2. Refactor `.github/workflows/sign-windows.yml` to package and sign official `sing-box-vpnctl` v1.14.0-vpnctl.3 archive.
3. Refactor `build.ps1` bundling logic to drop automatic `publish\sing-box-lx.exe` fallback while preserving explicit `-SingBoxPath`.
4. Run compatibility integrated source verification against `b7ce0e4f` and PR #240 (`ae352fdb`).
5. Execute non-publishing CI workflows with `upload_to_release=false` and await future agent gate inspection.

## Device Platform Matrix (Packaging & Verification, No Real Device Install)

| Platform | Target Asset | Packaging Mechanism | CI Run & Status | Real Device Install |
|---|---|---|---|---|
| Windows | `sing-box-1.14.0-vpnctl.3-windows-amd64.zip` | `build.ps1` bundling official SHA256 archive | Runs 33986314066, 33986314069, 33986314080 (ALL 5 GREEN; 46 pass 0 skip) | None |
| Linux | `sing-box-1.14.0-vpnctl.3-linux-amd64.tar.gz` | `build-linux.yml` safe non-publishing run | Run 33988031442 (SUCCESS; artifacts need inspection) | None |
| macOS | `sing-box-1.14.0-vpnctl.3-darwin-universal.zip` | `build-mac.sh` / `build-mac.yml` (`upload_to_release=false`) | Run 33988033773 (SUCCESS; artifacts need inspection) | None |
| Android | Legacy AAR / API 23 | `build-android.yml` dotnet publish (legacy verification) | Run 33991832141 SUCCESS (artifact 9976917773, 86335107 bytes, tooling-libbox-singbox-1.13.10 hash `239c4101465edcc270de75182764fb7566efd5fd284fbce35720fe70fd69f1a6`, minSdk 23 preserved, no install; restored legacy green, Android not broken anymore; VPNCTL-04 DEFERRED, no resolved claim) | None |

## Checkpoint exact `f88b7889` Status & Multi-Platform CI Evidence (2026-09-05)

- **Windows**: All 5 CI checks green across runs `33986314066`, `33986314069`, `33986314080`.
  - Windows test-update passed: 46 pass, 0 skip.
  - Bundled core verified: `sing-box-vpnctl` v1.14.0-vpnctl.3 (evidence: `/tmp/vpnrouter-vpnctl-windows-fixed-update.log`).
  - Security / test review: No fresh blockers on Windows; baseline tests need portable marker fallback.
- **Linux**: Run `33988031442` success on source `f88b7889` (non-publishing mode `upload_to_release=false`; sidecar checks PASS; embedded Linux core `973e453dc835ec07b53e950c97eb956fedb436434619e178c5dace0568cde0f6` matches official release binary).
- **macOS**: Run `33988033773` success on source `f88b7889` (non-publishing mode `upload_to_release=false`; sidecar checks PASS; embedded Mac core `f5a931da2c0a9f841decc25e9efe61bf052e5dfd706350210d17dae46324f507` matches official release binary).
- **Desktop Artifact Verification**: Earlier `f88b7889` artifact sidecar checks: all 5 PASS; embedded Linux core `973e453dc835ec07b53e950c97eb956fedb436434619e178c5dace0568cde0f6` matches official; Mac core `f5a931da2c0a9f841decc25e9efe61bf052e5dfd706350210d17dae46324f507` matches official verified archive pins.
- **Android**: Run `33988035788` actual FAIL with 23 javac errors (evidence: `/tmp/vpnrouter-vpnctl-android-failure.log`):
  - Missing `BoxService` / `newService`, `PlatformInterface` / `ConnectionOwner` return/API changes in v1.14.0-vpnctl.3 `libbox.aar`.
  - Defect registered: **VPNCTL-04 (P1)** Android AAR wrong API for current bridge compilation.
  - Root cause note: NOT a secret missing (`ANDROID_KEYSTORE_PASSWORD` shell printed message was not the actual error; javac compilation failure is the actual root cause).
  - Simplistic fallback rejected: `/tmp/vpnrouter-customcore-build-libbox.go` lines 178–188 (`libbox-legacy.aar`) shares the same v1.14 API (SDK 21, no Naive outbound) and lacks the old `BoxService`.
- **Status**: No closure until verified. Verification gates remain open/blocked.

## Checkpoint exact `947682558ed0b1c769649fc2f5235393869ffdd6` Status & Multi-Platform CI Evidence (2026-09-05)

- **Android to Java API Migration (Javac Fixes)**:
  - Adapted `CommandServer`: no IPC start; parallel verify does not bind global `command.sock`.
  - `ConnectionOwner`: implemented returning actual UID and package names.
  - Debug dropped: removed debug interfaces.
  - Preserved stderr via `CrashReportSource("vpnrouter")` writing to private working data (`filesDir/data/CrashReport-vpnrouter.log`); legacy `singbox-stderr-tail.log` exporter retained for backcompat.
  - Compiler correction: old agent invented `setStderrPath` rejected by compiler; corrected to `setCrashReportSource`.
  - Contract testing: added `AndroidVpnctlApiContractTests` with SOURCEONLY guard (validates source contracts, not native runtime behavior).
- **CI / Build Verification**:
  - All 5 PR checks green (observed via bash 78).
  - APK build in run `33990807363`: passed javac compilation completely (23 javac errors resolved), then FAIL on manifest merge: `uses-sdk:minSdkVersion 23 cannot be smaller than version 24 declared in library ... libbox.aar` (actual `/tmp/vpnrouter-android-final-api-failure.log` lines 570..581).
  - No fabricated supported API 26 per build scripts; uses actual published library manifest minSdk 24.
- **Defect & Goal Tracking**:
  - **Owner scope decision (ask android-vpnctl-minsdk)**: Keep legacy Android, deprioritize/defer migration. Android API 23 remains unchanged; full vpnctl desktop Windows/Linux/macOS remains goal.
  - **Backup branch**: `dsh/deferred-android-vpnctl-api-2026-09-05` (`f4d5265cd55e91fffefb1789cd2d445b4cd1f931`) pushed containing WIP Android vpnctl API changes.
  - **VPNCTL-04**: Remains DEFERRED, not resolved, and not desktop-blocking. Existing failure evidence preserved (CI run 33988035788 23 javac errors; CI run 33990807363 minSdk 23 < libbox 24 manifest merge failure).
  - **Build verification**: Android marked for expected legacy build verification, not new core.
  - **Goal state**: Goal tool edits denied because response arrived during goal round; recorded durable scope in plan instead; no claim that goal was updated.
- **Artifact Verification**:
  - Desktop platform artifacts match official `v1.14.0-vpnctl.3` release pins.
  - Android: `/tmp/libbox.aar` SHA256 matches pinned hash `471908107fb68de65f50cc8898e193b832b2ae12f0dfe9ee93d73f0b27f1a991`; class structures confirmed; preserved as deferred reference artifact.
- **Next Steps**:
  - Full vpnctl desktop Windows/Linux/macOS remains goal.
  - Android vpnctl migration deprioritized/deferred; verify Android against expected legacy build.
  - PR #233 + PR #240 combined compatibility verification.
  - Address Windows baseline RED.
  - Desktop artifact identity verification (Linux / macOS).
  - Independent security review.

## Verified Snapshot `9fac7b58` Status & Multi-Platform CI Evidence (2026-09-05)

- **Android Legacy Restoration (Byte-Exact to Base `b7ce0e4`)**:
  - Verified snapshot `9fac7b58ad0506fb99f40fcdaacd3235d9618993` restores 4 Android files byte-exact to base `b7ce0e4`:
    - `.github/workflows/build-android.yml`
    - `VPNRouter.Android/AndroidDeepVerifyBox.java`
    - `VPNRouter.Android/AndroidDiagnosticsExporter.cs`
    - `VPNRouter.Android/VpnRouterService.java`
    (`VPNRouter.Tests/AndroidVpnctlApiContractTests.cs` deleted).
  - Android is not broken anymore; restored legacy build is green.
  - Legacy Android APK workflow (`build-android.yml` runs `dotnet publish`, not Gradle build): CI run `33991832141` SUCCESS.
  - Produced artifact `9976917773` (86,335,107 bytes).
  - Bundled tooling `libbox-singbox-1.13.10` SHA256 hash: `239c4101465edcc270de75182764fb7566efd5fd284fbce35720fe70fd69f1a6`.
  - MinSDK 23 preserved. No install on real device.
  - Defect **VPNCTL-04** remains DEFERRED (no resolved claim; not desktop-blocking).

- **Desktop Artifact Verification (Checkpoint `f88b7889` Artifacts)**:
  - Desktop earlier `f88b7889` artifact sidecar checks: all 5 PASS.
  - Embedded Linux core SHA256 `973e453dc835ec07b53e950c97eb956fedb436434619e178c5dace0568cde0f6` matches official release binary.
  - Embedded macOS core SHA256 `f5a931da2c0a9f841decc25e9efe61bf052e5dfd706350210d17dae46324f507` matches official release binary.
  - Verified archive pins match official `PavelLizunov/sing-box-vpnctl` v1.14.0-vpnctl.3.

- **Combined SOURCEONLY Branch (`dsh/verify-vpnctl-night-combined-2026-09-05`)**:
  - Combined SHA: `c7e388a9adeab209e8524825824c94793b294956` (`9fac7b58` + NIGHT `ae352fdb` from PR #240).
  - Integrates code and tests across 58 files; no docs changes, no PR merge to main.
  - Multi-platform CI workflows on exact same combined SHA `c7e388a9adeab209e8524825824c94793b294956`:
    - Tests workflow: Run `33992003092` SUCCESS.
    - Linux build workflow: Run `33992005073` SUCCESS.
    - Mac build workflow: Run `33992007224` SUCCESS.
    (All three workflows SUCCESS on exact same combined SHA).
  - Precaution: Do not claim latest combined artifact identity until checked; no full solution / live VPN claim.

- **Packaging Characterization Witness Baseline (RED on `0f85cbfe` vs GREEN on `9fac7b58`)**:
  - Real RED witness baseline `0f85cbfeaf0d2030285c0ab44df1d64d13aefaef` based on `be0cf6f7` untouched product.
  - Run `33992362512` observed 3 Windows FAIL, 0 skip: tampered zip cache expected AUTHENTIC actual TAMPERED; corrupt cached zip exit 0; implicit LX output contains automatic selection.
  - Ubuntu: 4 source guard FAIL expected.
  - GREEN actual: Same fixture `9fac7b58` PR checks all 5 green actual; Windows 46 pass 0 skip on earlier `f88b7889` fixture before portable marker, also green on later `9fac7b58`.

## Verification Gates (BLOCKED / PENDING - No Closure Until Verified)

- [ ] Gate 1 — Clean build: Windows, Linux, and macOS compile clean against official `sing-box-vpnctl` v1.14.0-vpnctl.3; Android legacy build green (run 33991832141 SUCCESS via `dotnet publish`), 4 files restored byte-exact to `b7ce0e4`; combined branch `c7e388a9adeab209e8524825824c94793b294956` compiles green across Linux/Mac (runs 33992005073, 33992007224); no full solution / live VPN claim.
- [ ] Gate 2 — Test suite: Unit and packaging contract tests pass on Windows (46 passed, 0 skipped); combined branch test run 33992003092 SUCCESS across 58 code/test files.
- [ ] Gate 3 — Pipeline elimination: `.github/workflows/sign-windows.yml` no longer invokes Leadaxe `build-singbox-lx.ps1` (PASSED on `f88b7889`).
- [ ] Gate 4 — Autoselect elimination: `build.ps1` no longer auto-selects `publish\sing-box-lx.exe` without explicit argument (PASSED on `f88b7889`).
- [ ] Gate 5 — Hash integrity: Desktop earlier `f88b7889` artifact sidecar checks all 5 PASS (Linux embedded core `973e453dc835ec07b53e950c97eb956fedb436434619e178c5dace0568cde0f6` and macOS embedded core `f5a931da2c0a9f841decc25e9efe61bf052e5dfd706350210d17dae46324f507` match official release pins); Android tooling libbox singbox 1.13.10 hash `239c4101465edcc270de75182764fb7566efd5fd284fbce35720fe70fd69f1a6` in legacy artifact 9976917773; do not claim latest combined artifact identity until checked.
- [ ] Gate 6 — Safe CI: Non-publishing CI runs succeed with `upload_to_release=false` (Android legacy run 33991832141 SUCCESS; combined branch runs 33992003092, 33992005073, 33992007224 all SUCCESS; no PR merge).

## Checklist: Rollback & Regression Failure Criteria

- [ ] Rollback trigger: Any packaging failure, checksum mismatch, or unhandled regression in core sing-box-vpnctl 1.14.
- [ ] Rollback protocol: Safe task commit revert review; lead inspects and executes clean `git revert`; no automatic rollback.
- [ ] Regression criteria: Breakage in existing Clash API, v2ray StatsService, DNS ECH suppression, or SCM service wrapping.
- [ ] Artifact match: Windows, Linux, macOS, and Android release artifacts strictly match official SHA256 hashes.
- [ ] Safety invariants: No push to protected `main`, no PR merge, no release tag, no real device install.
