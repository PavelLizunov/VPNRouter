# TurnRelay integration and restart-only apply (2026-11-25)

Branch: `feat/turnrelay-integration` from `origin/main` `ac85310f`.

## Intent

1. Remove the P0 false-positive hot reload: sing-box 1.14.4 accepts
   `PUT /configs` with HTTP 204 but does not apply the document, so a reported
   success skipped the restart and could lift the kill switch over the old
   config.
2. Integrate the locked TurnRelay v1 contract used by `sing-box-vpnctl` and
   `vpnctl` on desktop, headless and Android.

## Invariants

- Config apply always restarts; the result is confirmed by a bounded `/proxies`
  tag probe (2.5s budget). A fake 204 can never count as success.
- `HealthMonitor` advances `_lastScan` only after a confirmed apply and never
  lifts kill-switch block rules from the restart path itself.
- `turnrelay://` is accepted only with `SingBoxFeatures.TurnRelayAvailable`.
- A TurnRelay row is a chain upstream only. `OutboundId` is the exact JSON `id`,
  and a VLESS/VMess target references it through `detour`.
- Capability order over HTTPS: `detour-v1`, optional `dnstt-v1`, optional
  `turnrelay-v1`, `vmess-v1`.
- Secrets in TurnRelay profiles are redacted from logs, crash reports and
  diagnostics.

## Scope

- Core model, URI parser, capability probe, JSON/YAML contexts.
- Subscription headers/dedupe, native sing-box JSON mapping, chain selection.
- Config generation, failover, deep verifier, custom-config gate, rollback
  compatibility, placeholder defense, redaction, macOS helper contract.
- Desktop view model/classification/simple-mode guard, Android list and
  `TEST_SET_CONFIG` multi-link support.
- Tests for the restart-only apply and the TurnRelay contract.

## Verification

- `dotnet test VPNRouter.Tests/VPNRouter.Tests.csproj -c Release` on an
  authorized worker, plus the focused filters for the new suites.
- Android Java compile contract from `.github/workflows/android-compile.yml`
  where applicable; macOS C helper contract tests where a macOS worker is used.
- Android APK/AVD readiness is a separate environment report; no emulator build
  runs on `harness-test`.

## Observed outcomes

- `harness-test`: `dotnet`, Java, `adb`, `emulator` and any Android SDK are
  absent. This control plane cannot build or run the APK/AVD.
- `mac-worker` at `2330b714`: the production helper compiled with the workflow
  flags (`-std=c11 -O2 -Wall -Wextra -Werror -D_FORTIFY_SOURCE=2
  -fstack-protector-strong`), `install-privileged.sh` passed `bash -n`, and the
  typed contract tests passed when compiled without the sanitizer. The
  sanitizer build stalled inside the macOS ASAN runtime initializer on this
  worker, before the test binary could run; that is an environment limitation,
  not a contract failure.
- `windows-worker` (VM100, 8 logical CPUs, 12.7 GiB free RAM, 100.5 GB free on
  C:, no running build jobs) at `f36d6ef8` with SDK `10.0.400` instead of the
  pinned `10.0.404` (that exact patch is absent; the test checkout used a
  temporary `global.json` pin, the repository commit is unchanged):
  - Focused TurnRelay/P0/chain/diagnostic suites: 541 passed, 0 failed,
    10 skipped (`ProtocolGroupsTests` excluded because its non-Avalonia tests
    initialize the Pictogram platform only when a full-suite run has already
    started Avalonia; the class passed in the full run).
  - Full CI filter (`!~Headless`, `!~PageScreenshot`, `!~VisualDiff`):
    4830 passed, 10 failed, 31 skipped in 6m11s. All 10 failures are host
    environment failures unrelated to this change: nine
    `VpnctlPackagingCharacterizationTests` cases launch a temporary
    `powershell -File` fixture that the worker's execution policy blocks
    (`running scripts is disabled on this system`), and
    `ReleaseToolingContractTests.WindowsMaintenanceFixtureCompletes`
    (`Test-InstallerRepair.ps1`) fails deleting its own temp tree ("directory is
    not empty"). No TurnRelay/P0 test failed.
  - Android Java checks on `mac-worker` with Temurin 17:
    `NativeEngineSlotTest`, `NativeReportPrivacyTest`,
    `AndroidTunnelLifecycleTest` all passed.
- WINBRAT Android readiness: `adb`, SDK 36 and emulator exist; AEHD reports
  "installed and usable"; AVDs under `C:\android-build\avd` include
  `pixel6-api34`, `pixel9pro-api36`, `pixel9pro-api37`, `pixel3a-api29`,
  `huawei-nogms-api33`, `redmi-budget-api34`. `libbox.aar` is not in the repo,
  so the APK build waits for the new core archive. `harness-test` itself has no
  SDK/adb/emulator/dotnet and remains control-plane only.

## Unknowns

- The pinned `sing-box-vpnctl` release hash is intentionally unchanged until a
  `v1.14.4-vpnctl.4` tag exists.
- Android on-device verification needs the new `libbox.aar` below.
