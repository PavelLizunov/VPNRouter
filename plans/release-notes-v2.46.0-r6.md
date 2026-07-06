# VPNRouter v2.46.0-r6

## Summary

- Fixed the Applications include/exclude mode flip so visible app checkboxes refresh from the newly active list even if saving config.yaml fails.
- Kept include and exclude app selections independent: include defaults no longer visually bleed into an empty exclude list during the mode switch.
- Hardened AppsMode and VisualDiff tests so they use in-memory settings instead of the live ProgramData config.

## Test flow

1. Open Advanced -> Applications.
2. In "Only selected -> VPN", confirm routed apps are checked.
3. Switch to "Except selected -> VPN".
4. Confirm the checked rows switch to the exclude list; on a fresh exclude list, no include defaults remain checked.
5. Check one app in exclude mode, switch back to include mode, then back to exclude mode. Both selections should persist independently.

## Verification

- `dotnet build VPNRouter.sln -c Release`
- `dotnet test VPNRouter.Tests/VPNRouter.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~VlessServersResolverTests|FullyQualifiedName~ConfigGeneratorEmptyServersGuardTests|FullyQualifiedName~FreeConfigAggregatorPreserveTests|FullyQualifiedName~MainWindowViewModelCharacterizationTests|FullyQualifiedName~MainWindowViewModelAppsModeTests|FullyQualifiedName~AppItemViewModelBridgeTests"`
- `dotnet test VPNRouter.Tests/VPNRouter.Tests.csproj -c Release --filter "FullyQualifiedName~VisualDiffTests"`
