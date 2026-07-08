## Summary

- Local/private networks are now mandatory TUN route exclusions, so LAN ranges stay on the OS routing table before sing-box sees them in any routing mode.
- Health diagnostics now detect Discord DNS/voice stalls through the VPN and suggest switching server/transport or routing Discord direct when low latency matters more than ISP hiding.

## Test Flow

- Confirm `route_exclude_address` contains mandatory local ranges in generated TUN configs.
- Run Health Check after Discord lag: it should surface Discord DNS/voice stall advice when sing-box logs repeated Discord DNS timeouts or multi-second resolves.
- Smoke split include/exclude modes and custom config injection to confirm local network exclusions remain present.

## Verification

- `dotnet test VPNRouter.Tests\VPNRouter.Tests.csproj -c Release --filter "FullyQualifiedName~TunSettingsEffectiveExcludeTests|FullyQualifiedName~HealthCheckRobloxDiagnosticsTests|FullyQualifiedName~SplitTunnelDirectAppImpactTests|FullyQualifiedName~ConfigGeneratorExcludeModeTests|FullyQualifiedName~ConfigGeneratorIncludeModeTests|FullyQualifiedName~AwgDnsAndMtuTests"`
- `dotnet test VPNRouter.Tests\VPNRouter.Tests.csproj -c Release --filter "FullyQualifiedName~CustomConfigInjectorTests|FullyQualifiedName~LeakProtectionTests|FullyQualifiedName~VpnEngineTunFingerprintTests|FullyQualifiedName~YamlStaticContextRoundTripTests"`
- Full `VPNRouter.Tests` run was attempted but timed out on existing lifecycle/state-machine failures unrelated to this routing change.

## Commits

- `67eb955f` fix(routing): never capture local networks
