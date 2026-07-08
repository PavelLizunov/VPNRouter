# VPNRouter v2.46.0-r28 - True Split conflict diagnostics

## Summary

- True Split now surfaces Windows WFP/BFE duplicate-object failures (`0x80320009`) as an Amnezia/Mullvad split-driver conflict instead of a generic ordinary-split fallback.
- Diagnostics and Health Check now explain the common conflict codes: `err=5`, `StartService err=183`, `0x80320009`, and why `ping` `General failure` is a local WFP/driver block, not an MTU result.
- The Apps page retry button is now labeled as a safe retry/check action, not a forceful driver takeover.

## Test Flow

1. Open Apps in split + bypass-list mode.
2. If Amnezia/Mullvad split driver is active, True Split must stay in ordinary split fallback and show a clear conflict reason.
3. Use the diagnostics bundle or Health Check and confirm the True Split notes mention `0x80320009` and `General failure`.
4. Confirm VPN remains usable in ordinary split mode; VPNRouter must not stop or delete a foreign kernel split driver.

## Verification

- `dotnet build VPNRouter.sln -c Release`
- `dotnet test VPNRouter.Tests/VPNRouter.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~SplitTunnelManagerTests|FullyQualifiedName~SplitTunnelDoubleStartGuardTests|FullyQualifiedName~SplitTunnelProtocolTests|FullyQualifiedName~VpnEngineSplitTunnelResolveTests|FullyQualifiedName~DiagnosticsRedactorTests"`

Note: full local test project currently fails on this machine because legacy lifecycle tests write to `C:\ProgramData\VPNRouter\config\current.json` without sufficient ACL. The targeted True Split conflict suite is green.

## Commits

- `2846c72f` - `fix(split): explain true split driver conflicts`
