# VPNRouter v2.46.0-r12

## Summary

- True Split retry is now shown as a visible warning banner above the app list instead of being buried in the list footer.
- True Split driver start now treats Windows `ERROR_ALREADY_EXISTS` from `StartService` as a recoverable state and continues to the real device-open verification.

## Test flow

1. Open Applications in split/exclude mode.
2. If True Split fails, confirm the warning banner and `Start True Split` action are visible before the app list.
3. Press `Start True Split`.
4. Confirm the status changes to `True split: active` or the app keeps the ordinary split fallback with a visible retry action.

## Verification

- `dotnet build VPNRouter.sln -c Release`
- `dotnet test VPNRouter.Tests/VPNRouter.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~VpnEngineSplitTunnelResolveTests|FullyQualifiedName~MainWindowViewModelCharacterizationTests"`
- `dotnet test VPNRouter.Tests/VPNRouter.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~VlessServersResolverTests|FullyQualifiedName~ConfigGeneratorEmptyServersGuardTests|FullyQualifiedName~FreeConfigAggregatorPreserveTests|FullyQualifiedName~MainWindowViewModelCharacterizationTests"`
- `dotnet test VPNRouter.Tests/VPNRouter.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~VisualDiffTests"`

## Commits

- `e76f88b1 fix(apps): make true split retry visible`
