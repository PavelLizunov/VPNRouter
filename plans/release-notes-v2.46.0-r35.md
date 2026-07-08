## Summary

- Local network hard-bypass now also covers IPv6 local scopes: loopback, link-local, and ULA.
- This keeps LAN/local traffic out of the TUN before sing-box sees it, matching the existing IPv4 local bypass behavior.

## Test Flow

- Confirm generated TUN route exclusions include IPv4 local ranges plus `::1/128`, `fe80::/10`, and `fc00::/7`.
- Smoke split include/exclude, custom config injection, and TUN fingerprint tests to ensure the effective list stays stable and persisted user settings are not mutated.

## Verification

- `dotnet test VPNRouter.Tests\VPNRouter.Tests.csproj -c Release --filter "FullyQualifiedName~TunSettingsEffectiveExcludeTests|FullyQualifiedName~SplitTunnelDirectAppImpactTests|FullyQualifiedName~ConfigGeneratorExcludeModeTests|FullyQualifiedName~ConfigGeneratorIncludeModeTests|FullyQualifiedName~CustomConfigInjectorTests|FullyQualifiedName~VpnEngineTunFingerprintTests|FullyQualifiedName~YamlStaticContextRoundTripTests"`
