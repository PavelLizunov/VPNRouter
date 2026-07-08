## VPNRouter v2.46.0-r33

Health Advisor and auto intent for game/connectivity troubleshooting.

### Added

- New connection intent selector on the subscription page:
  - Sites and messaging
  - Games and calls
  - Maximum privacy
  - Maximum compatibility
- Smart Connect now uses the selected intent when choosing between alive subscription servers.
- Health check now adds actionable advice for Roblox on VLESS/TCP, recent sing-box crashes, repeated endpoint timeouts, and MTU values below 1332.

### Notes

- Privacy intent never adds direct app bypass automatically. If Roblox needs direct internet, the user must explicitly switch intent or add an app exclusion.
- Old configs without `connection_intent` load as the default general intent.
- Windows build is shipped with `sing-box-lx.exe` to keep AWG and XHTTP support.

### Verified

- `dotnet build VPNRouter.sln -c Release -p:BuildInParallel=false --no-restore`
- `dotnet test VPNRouter.Tests\VPNRouter.Tests.csproj -c Release --filter "FullyQualifiedName~AutoIntentScoringTests|FullyQualifiedName~HealthCheckRobloxDiagnosticsTests|FullyQualifiedName~YamlStaticContextRoundTripTests|FullyQualifiedName~MainWindowViewModelCharacterizationTests"`
- `dotnet test VPNRouter.Tests\VPNRouter.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~VlessServersResolverTests|FullyQualifiedName~ConfigGeneratorEmptyServersGuardTests|FullyQualifiedName~FreeConfigAggregatorPreserveTests|FullyQualifiedName~MainWindowViewModelCharacterizationTests"`
- `dotnet test VPNRouter.Tests\VPNRouter.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~VisualDiffTests"`
