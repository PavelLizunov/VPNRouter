## VPNRouter v2.46.0-r31

Roblox / game-connectivity release-candidate fix.

### Fixed

- Diagnose now flags recent VPN endpoint `i/o timeout` bursts in sing-box logs, so Roblox disconnects are not misdiagnosed as app-list or True Split issues.
- Diagnose now probes Windows path MTU with DF ping and warns when configured TUN MTU is too high or too close to the measured ceiling.
- Network settings now include a manual "Auto-pick MTU" action. It probes the current path, saves the first working safe MTU, and asks for VPN reconnect to apply it.

### Notes

- Logs can identify likely endpoint/MTU trouble, but exact MTU still needs an active probe. This release keeps the change manual instead of silently rewriting MTU on every start.
- If plain ping fails while True Split is active, MTU probing is blocked by local WFP/driver state; turn True Split off or fix the driver state first.

### Verified

- `dotnet build VPNRouter.sln -c Release -p:BuildInParallel=false --no-restore`
- `dotnet test VPNRouter.Tests\VPNRouter.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~HealthCheckRobloxDiagnosticsTests|FullyQualifiedName~MainWindowViewModelCharacterizationTests"`
- `dotnet test VPNRouter.Tests\VPNRouter.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~VlessServersResolverTests|FullyQualifiedName~ConfigGeneratorEmptyServersGuardTests|FullyQualifiedName~FreeConfigAggregatorPreserveTests|FullyQualifiedName~MainWindowViewModelCharacterizationTests"`
- `dotnet test VPNRouter.Tests\VPNRouter.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~VisualDiffTests"`
