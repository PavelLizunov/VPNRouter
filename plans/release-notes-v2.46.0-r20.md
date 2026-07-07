# Summary

This rolling candidate makes the Apps page explicit when VPN is owned by the
Windows Service: the desktop GUI now says that True Split is controlled by the
service instead of leaving the retry/control state ambiguous.

# Changes

- When the GUI adopts a service-owned VPN session in split/exclude mode, show a
  True Split banner explaining that Windows Service owns the VPN and True Split
  retry must be done by stopping VPN and starting from the app.
- Keep the retry button hidden in that state so the GUI does not try to start a
  parallel VpnEngine/True Split owner.
- Preserve the r19 driver fixes: stale stopped `mullvad-split-tunnel`
  `ERROR_ALREADY_EXISTS` repair, detailed `err=5` device-busy reason, and
  diagnostics `windows-services.txt`.

# Verification

- `dotnet build VPNRouter.sln -c Release`
- `dotnet test VPNRouter.Tests/VPNRouter.Tests.csproj -c Release --filter "FullyQualifiedName~MainWindowViewModelAppsModeTests|FullyQualifiedName~SplitTunnelDoubleStartGuardTests|FullyQualifiedName~MainWindowViewModelCharacterizationTests"`
- `dotnet test VPNRouter.Tests/VPNRouter.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~VlessServersResolverTests|FullyQualifiedName~ConfigGeneratorEmptyServersGuardTests|FullyQualifiedName~FreeConfigAggregatorPreserveTests|FullyQualifiedName~MainWindowViewModelCharacterizationTests"`
- `dotnet test VPNRouter.Tests/VPNRouter.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~VisualDiffTests"`

# User Test Flow

1. Update to `v2.46.0-r20`.
2. Open Advanced mode -> Apps.
3. Use split mode with active list "Mimo VPN".
4. If VPN is running via Windows Service, the Apps page should explain that
   True Split is controlled by the service and manual retry requires stopping
   VPN and starting it from the app.
5. If True Split still cannot start after a normal app-owned start, collect
   diagnostics; the ZIP should include `windows-services.txt` with
   `mullvad-split-tunnel` state/path/process evidence.
