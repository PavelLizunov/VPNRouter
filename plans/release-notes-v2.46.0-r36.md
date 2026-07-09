# VPNRouter v2.46.0-r36

## Summary

Fixes a Windows recovery path where VPNRouter could show the VPN as connected while traffic was not actually working after a stale or orphaned sing-box/TUN restart.

The HealthMonitor restart path now refuses to hot-reload through a loopback Clash API unless the current SingBoxManager still owns a live sing-box process. If the managed handle is gone but a VPNRouter-owned sing-box is still running, VPNRouter kills that orphan before doing a full restart.

## Why

Diagnostics from `VPNRouter-diagnostics-20260709-122614.zip` showed:

- app version `2.45.0`;
- full tunnel active;
- repeated `Health check failed - sing-box is not healthy`;
- repeated TUN startup failures: `Cannot create a file when that file already exists`;
- hot-reload reported HTTP 204 even when the manager no longer had a valid sing-box process handle.

That means the Clash API could belong to an orphaned process. Hot-reloading it made logs look successful but left HealthMonitor unhealthy, producing the user-visible state: connected in UI, no working traffic.

## Verification

- `dotnet test VPNRouter.Tests\VPNRouter.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~HealthMonitorRecoveryGapTests"`: PASS, 8 tests.
- Core/App/CLI/Service project builds: PASS.
- Full `VPNRouter.sln` build is blocked in this dev session by the already-running MCP host locking `tools\VpnRouterTestMcp\bin\Release\net8.0-windows\VpnRouterTestMcp.dll`; the app projects build cleanly.
