# Phase 2 — MTU contract repair

**Owner**: Codex task `019fb7b4-38ad-7ea2-9359-b8d5fa47cfae` follow-up
**Branch**: `codex/mtu-contract-fix-2026-08-03`
**Roadmap ref**: out-of-roadmap audit follow-up; source is `plans/mtu-end-to-end-audit-2026-08-03.md`
**Effort**: 2 hours
**Risk**: MEDIUM
**Blast radius**: Core config/validation, desktop settings copy, and regression tests · about 12 files / 100 changed lines · generated TUN MTU changes only for invalid values and AWG with a user MTU below 1420
**Rollback**: `git revert <implementation-commit>`

## Why

The MTU audit confirmed four contract defects: AWG discards a deliberately lower
user MTU, accepted ranges disagree between UI/validator/generator/example,
IPv6-enabled TUN accepts values below 1280, and the Windows helper is described
as path auto-tuning although it is only an IPv4 DF ping to `8.8.8.8`. The repair
must make those existing paths agree without inventing an unproved adaptive MTU
algorithm or changing Android runtime behavior.

## What

- Put the existing 576/1500 bounds and IPv6 minimum 1280 next to
  `TunSettings.DefaultMtu` and reuse them at save, validation, and generation.
- Generate AWG TUN MTU as `min(normalized user MTU, 1420)`.
- Reject `<1280` only when IPv6 is enabled.
- Keep the Windows probe algorithm unchanged, but identify its fixed IPv4 target
  in the button/help/status/diagnostic copy.
- Replace the stale sample `mtu: 9000` and correct nearby comments already
  listed in `plans/refactor-backlog.md`; do not migrate stored 1280 or change
  Android's hard-coded runtime MTU.
- Mark the four confirmed audit defects resolved by the implementation PR.

```diff
- Mtu = proxyIsUdpNative ? AwgEndpointMtu : NormalizeTunMtu(settings.Tun.Mtu)
+ Mtu = proxyIsUdpNative ? Math.Min(NormalizeTunMtu(settings.Tun.Mtu), AwgEndpointMtu) : NormalizeTunMtu(settings.Tun.Mtu)
```

## How

1. Add the three bounds to the existing `TunSettings` contract.
2. Reuse them in `ConfigGenerator`, `SettingsValidator`, and desktop save logic.
3. Make the fixed-target IPv4 probe wording explicit without changing its
   candidate list, safety floor, or persistence behavior.
4. Update the focused AWG, normalization, validation, and diagnostic regressions.
5. Run focused tests, full Release build, full test suite, Markdown/link checks,
   read-only Qwen review, then update this Outcome and the defect/backlog ledgers.

### Tests written

- `AwgDnsAndMtuTests.Awg_TunMtuPreservesLowerUserSetting` — a user value below
  1420 survives AWG config generation.
- `MtuJumboFixTests.NormalizeTunMtu_ClampsOutsideContract` — values below 576 or
  above 1500 fall back while valid custom values survive.
- `SettingsValidatorTests.TunMtu_BelowIpv6Minimum_IsInvalidOnlyWhenIpv6Enabled`
  — 1279 is fatal for dual stack and remains valid for IPv4-only mode.
- `HealthCheckRobloxDiagnosticsTests.BuildPathMtuWarning_IdentifiesFixedIpv4Target`
  — diagnostic output cannot imply an endpoint-aware measurement.

### Verification approach

Focused xUnit regressions run first, followed by `dotnet build VPNRouter.sln -c
Release` and the complete `VPNRouter.Tests` Release suite. NetworkPage headless
rendering covers the copy-only UI surface. Remote brat deployment is not part of
this PR because no release artifact is created; the mandatory post-ship gate
still applies if this change is later shipped.

## Verification gate

- [ ] **Gate 1 — Build clean**: `dotnet build VPNRouter.sln -c Release` → 0 errors.
- [ ] **Gate 2 — Tests green**: full suite passes. New tests included.
- [ ] **Gate 3 — Docs**: brief Outcome and MTU ledgers updated.
- [ ] **Gate 4 — Self-review**: read-only Qwen review plus Codex diff review.
- [ ] **Gate 5 — Remote brat UI verify**: N/A for this PR — no release artifact;
  copy-only NetworkPage surface is covered headlessly. Post-ship verify remains
  mandatory for a later rolling release.
- [ ] **Gate 6 — Characterization diff**: N/A — not a god-file split.

## Outcome

**Status**: PENDING
**Commits**: pending
**Pushed**: pending
**Test deltas**: pending
**Files changed**: pending

**Gate results:** pending

**Surprises encountered**: pending

**Follow-ups spawned**: none planned; runtime underlay and Android questions stay
measurement-gated in the source audit.

**Lessons for methodology doc**: pending
