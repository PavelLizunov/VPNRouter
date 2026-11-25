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

## Unknowns

- The pinned `sing-box-vpnctl` release hash is intentionally unchanged until a
  `v1.14.4-vpnctl.4` tag exists.
- Android on-device verification needs the new `libbox.aar` below.
