## VPNRouter v2.46.0-r32

Small MTU auto-pick hotfix after r31.

### Fixed

- MTU auto-pick no longer jumps directly from 1350 to 1320. It now probes 1340 and 1332 before trying lower values.
- MTU auto-pick no longer saves values below 1332 automatically. If only a lower MTU works, the UI reports that the path is too narrow and leaves the setting unchanged.

### Notes

- Roblox `Error Code: 277` after MTU 1320 is likely not solved by lowering MTU further. Check VPN endpoint timeouts / server transport next.

### Verified

- `dotnet build VPNRouter.sln -c Release -p:BuildInParallel=false --no-restore`
- `dotnet test VPNRouter.Tests\VPNRouter.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~HealthCheckRobloxDiagnosticsTests|FullyQualifiedName~MainWindowViewModelCharacterizationTests"`
