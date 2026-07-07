# VPNRouter v2.46.0-r26 - True Split foreign driver safety

## Summary

- Removed the True Split retry path that tried to stop or reconfigure foreign `mullvad-split-tunnel.sys` services, including Amnezia/Mullvad split drivers.
- Treat `StartService err=183` as a stale or busy kernel driver object that needs fallback/reboot, not as a delete-and-recreate service repair.
- The retry button now only retries VPNRouter's own True Split engage path. If another split driver owns the device, VPNRouter stays in ordinary split fallback and reports the owner.

## Why

Diagnostics from 2026-07-08 showed two Windows bugchecks (`0x3B SYSTEM_SERVICE_EXCEPTION`) around True Split retry attempts while AmneziaVPN was auto-restarting its own split driver service. The old retry path could race a foreign kernel driver and leave Windows reporting an already-existing driver object.

## Test Flow

- `dotnet build VPNRouter.sln -c Release`
- `dotnet test VPNRouter.Tests/VPNRouter.Tests.csproj -c Release --filter "FullyQualifiedName~VpnEngineSplitTunnelResolveTests|FullyQualifiedName~SplitTunnelManagerTests|FullyQualifiedName~SplitTunnelDoubleStartGuardTests"`
- Pre-commit gate: clean build and scoped tests 207/207.
- Commit-level CI gate: green for main commit `4d96e4a5`; `characterization-windows` was skipped by workflow.

## User Check

1. Install v2.46.0-r26.
2. Keep Amnezia/Mullvad split tunnel present, open Apps -> True Split retry.
3. Expected: no foreign service stop attempt, no driver takeover, no crash. The app should show fallback with the foreign owner/reboot guidance.
4. With no foreign split driver loaded after a reboot, True Split can be retried normally.

## Commits

- `4d96e4a5` - fix(apps): stop true split foreign driver takeover
