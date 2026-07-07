Summary
=======

This rolling candidate tightens True Split recovery for the stale
`mullvad-split-tunnel` driver case seen on DESKTOP-M922IJ2.

Changes
=======

- Treat `StartService` returning `ERROR_ALREADY_EXISTS` while
  `mullvad-split-tunnel` is `STOPPED` as a stale driver-object collision, not as
  a successful start.
- Run the existing safe repair path for our own stopped driver service:
  delete/recreate only when the registered driver path is recognisably
  VPNRouter-owned.
- Preserve the detailed True Split failure reason in the Apps page instead of
  collapsing every `err=5` into a generic "driver busy" message.
- Include the driver service path and Win32 exit code in the failure reason, so
  diagnostics can distinguish our stale service from a foreign Mullvad install.

Verification
============

- `dotnet build VPNRouter.sln -c Release`
- `dotnet test VPNRouter.Tests/VPNRouter.Tests.csproj -c Release --filter "FullyQualifiedName~SplitTunnelManagerTests|FullyQualifiedName~VpnEngineSplitTunnelResolveTests|FullyQualifiedName~MainWindowViewModelCharacterizationTests"`
- `dotnet test VPNRouter.Tests/VPNRouter.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~VlessServersResolverTests|FullyQualifiedName~ConfigGeneratorEmptyServersGuardTests|FullyQualifiedName~FreeConfigAggregatorPreserveTests|FullyQualifiedName~MainWindowViewModelCharacterizationTests"`
- `dotnet test VPNRouter.Tests/VPNRouter.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~VisualDiffTests"`

User Test Flow
==============

1. Update to `v2.46.0-r19`.
2. Open Advanced mode -> Apps.
3. Use split mode with active list "Mimo VPN".
4. Click "Запустить True Split" if True Split is not active.
5. If it still cannot start, collect diagnostics from the app menu; the ZIP
   should include `windows-services.txt` and the UI/log should now show the
   exact `mullvad-split-tunnel` service state/path/exit code.
