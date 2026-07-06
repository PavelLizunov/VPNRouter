# VPNRouter v2.46.0-r10

## Summary

- Changed the generic VLESS/TCP TUN MTU default from 1280 to 1420.
- Added schema v8 migration:
  - `tun.mtu: 1280` -> `1420`
  - `tun.mtu: 1500` -> `1420`
  - invalid `tun.mtu <= 0` or `> 1500` -> `1420`
  - explicit custom MTU values are preserved.
- Kept AWG/WireGuard effective MTU at 1420.
- Added soft warnings for risky MTU values instead of hard-failing:
  - below 1332 may break Dota 2 / CS2 / TF2 / Steam Datagram Relay traffic
  - above 1420 may break VPN/proxy paths due to PMTU or fragmentation blackholes
- Updated MTU help text: 1420 is the default, 1400/1380 are compatibility fallbacks for narrow mobile/PPPoE/nested VPN paths.

## Test Flow

1. Start with an older config containing `tun.mtu: 1280`; launch r10 and confirm it migrates to `1420`.
2. Start with `tun.mtu: 1500` or `9000`; confirm it migrates/clamps to `1420`.
3. Set custom `tun.mtu: 1400`; confirm it is preserved.
4. Open Settings -> Network and confirm the MTU hint says default 1420 and mentions 1400/1380 fallback guidance.
5. Reconnect VPN after changing MTU.

## Verification

- `dotnet build VPNRouter.sln -c Release`
- `dotnet test VPNRouter.Tests/VPNRouter.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~SettingsMigratorMtuTests|FullyQualifiedName~MtuJumboFixTests|FullyQualifiedName~AwgDnsAndMtuTests|FullyQualifiedName~YamlStaticContextRoundTripTests|FullyQualifiedName~SettingsValidatorTests|FullyQualifiedName~SplitTunnelDirectAppImpactTests|FullyQualifiedName~VpnEngineTunFingerprintTests"`
- `powershell -ExecutionPolicy Bypass -File tools/verify-last-commit-ci.ps1`
