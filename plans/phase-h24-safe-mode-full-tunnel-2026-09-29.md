# H-24: make safe mode route as full tunnel in the generated config

## Why

Ledger NIGHT-FOLLOWUP-02 (confirmed by the 2026-09-29 audit). Safe mode set a local
`isFullTunnel` and built a `FullTunnel` profile, but `ConfigGenerator`, the committed firewall
config and the active-routing readout read `settings.App.RoutingMode`, which stayed `split`. Safe
mode therefore generated a split config with an empty profile.

## What

- `StartupPipeline` also sets `settings.App.RoutingMode = "full"` in the safe-mode branch. Safe mode
  loads defaults and blocks `Save`, so nothing is persisted.
- Regression test `SafeMode_ForcesFullTunnelRoutingIntoSettingsAndConfig`: `FullTunnel` profile,
  `RoutingMode == "full"` and `route.final != "direct"`.

## Not covered

Safe mode with no active profile still throws "No active profile" before the override, as the
existing `ResolveProfile_NoActiveProfileInSplitMode_ThrowsInvariantViolation` test pins.

## Verification

Targeted run of `StartupPipelineTests` on `windows-worker` at the exact head SHA, then exact-head CI.

## Outcome

Pending.
