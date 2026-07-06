## Summary

v2.46.0-r11 makes True Split understandable in the Applications page.

- Shows whether True Split is active, missing from the build, starting, or falling back to ordinary split.
- Adds a "Start True Split" retry button for GUI-owned connected split/exclude tunnels.
- Keeps ordinary split as the fail-open fallback when the driver cannot engage.
- Bundles the split-tunnel driver in the Windows build so True Split can actually start in this candidate.

## Test Flow

1. Open Applications.
2. Use split tunnel with the active list set to "Bypass VPN".
3. Connect VPN.
4. Confirm the Applications footer shows True Split status.
5. If it shows fallback, click "Start True Split" and confirm the status updates.

## Verification

- `dotnet build VPNRouter.sln -c Release`
- `dotnet test VPNRouter.Tests/VPNRouter.Tests.csproj -c Release --filter "FullyQualifiedName~VpnEngineSplitTunnelResolveTests|FullyQualifiedName~MainWindowViewModelCharacterizationTests"`
- pre-commit gate: build clean + scoped tests 207/207
- commit CI: hard-red 0 on `54c44812`

## Commits

- `54c44812 feat(apps): surface true split status`
