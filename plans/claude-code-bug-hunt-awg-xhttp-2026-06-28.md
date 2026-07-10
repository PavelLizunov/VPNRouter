# Claude Code task: AWG/XHTTP bug-hunt follow-up

Date: 2026-06-28
Scope reviewed: new AWG, XHTTP, game-DNS, sing-box-lx build, and related diagnostics changes after `57d882d0` (`release: v2.44.4-r6`) through `8cb1cb45`.

Important repo state note: when this report was written, the working tree already had in-progress changes in `ConfigGenerator.cs`, `LeakProtection.cs`, `CrashReporter.cs`, `DiagnosticsRedactor.cs`, `ServerUriParser.cs`, `VlessUriParser.cs`, plus a new `SingBoxFeatures.cs`. Treat those as possible partial fixes. Inspect them first; do not blindly reapply the same changes.

## Goal

Make AWG and XHTTP safe to accept from user input/subscriptions, safe to generate, and safe to ship with mixed sing-box binaries.

Acceptance criteria:

- Official upstream sing-box builds must never receive fork-only config (`type=xhttp` or AWG fields such as `jc`, `jmin`, `h1`).
- AWG configs must pass the real production path, not only `ConfigGenerator.Generate`: `ConfigPipeline` strict validation, `LeakProtection`, `ConfigSanityCheck`, and pre-start checks.
- AWG must be selected only when the active selected server itself is AWG. A same-host AWG sibling must not hijack a selected VLESS/HY2/TUIC server.
- AWG URI parsing must preserve valid WireGuard base64 characters (`+`, `/`, `=`), or the accepted format must explicitly require base64url/percent encoding and validate it.
- Malformed subscription lines must not log full secret-bearing URIs.
- `StrictDns=true` must retain "all DNS through VPN" semantics even when `ResolveGameDnsOffProxy=true`.
- `DeepVerify` must either support AWG/XHTTP correctly or mark them unsupported explicitly instead of false-failing with a mismatched VLESS config.
- Tests must cover every item above.

## Confirmed findings

### P0/P1 - AWG endpoint cannot pass production validation

Evidence:

- `ConfigGenerator.BuildOutbounds` emits AWG as a top-level endpoint tagged `proxy` and returns only `direct` / `dns-direct` outbounds.
- `LeakProtection.ValidateConfig` currently checks `config.Outbounds.Any(o => o.Tag == "proxy")`.
- `ConfigSanityCheck.CheckBeforeStart` also searches only proxy-typed outbounds and returns "no proxy outbound found".
- `ConfigPipeline.BuildGenerated` calls `LeakProtection.ValidateConfig` in strict mode and throws on validation errors.

Impact:

- An AWG config can pass unit-level generation tests but fail before launch in the real cold-start path.

Fix direction:

- Introduce a shared route-target resolver: a tag can be satisfied by either an outbound or an endpoint.
- Teach `LeakProtection` and `ConfigSanityCheck` endpoint-aware validation.
- Add endpoint-specific AWG validation: type `wireguard`, tag `proxy`, non-empty interface private key, non-empty peer public key, at least one address, valid peer address/port, and valid allowed IPs.
- Add a `ConfigPipeline` test for AWG, not just `ConfigGenerator.Generate`.

Suggested test names:

- `ConfigPipeline_AwgEndpointProxy_PassesStrictValidation`
- `ConfigSanityCheck_AwgEndpointProxy_IsNotDeadConfig`
- `LeakProtection_AwgEndpointSatisfiesProxyTarget`

### P1 - AWG/XHTTP are accepted without checking the bundled binary capability

Evidence:

- `tools/build-singbox-lx.ps1` can build Leadaxe sing-box-lx with `with_xhttp,with_awg`.
- But default packaging still uses upstream SagerNet:
  - `build.ps1` default `SingBoxVersion = "1.13.14"` and downloads from SagerNet unless `-SingBoxPath` is passed.
  - `build-mac.sh` downloads SagerNet `1.13.14`.
  - `.github/workflows/build-linux.yml` downloads SagerNet `1.13.14`.
- Current `publish/dist/sing-box.exe version` had no `with_awg` / `with_xhttp` tags.
- Upstream `sing-box 1.13.14 check` rejects `transport.type=xhttp` with `unknown transport type: xhttp`.
- Upstream `sing-box 1.13.14 check` rejects AWG-specific endpoint fields such as `jc` with `json: unknown field "jc"`.

Impact:

- A normal official-binary build can ingest `awg://` or `vless://...?type=xhttp`, generate config, and then fail at sing-box config load.

Fix direction:

- Add a runtime/build-time capability source, for example `SingBoxFeatures`, based on bundled binary `version` tags or release build metadata.
- Gate `ServerUriParser.IsSupportedScheme` and manual parse for `awg://` / `amneziawg://` on AWG availability.
- Gate `VlessUriParser` `type=xhttp` on XHTTP availability.
- Decide product policy:
  - Option A: all shipped desktop assets bundle sing-box-lx when AWG/XHTTP features are enabled.
  - Option B: fork-only protocols are hidden/rejected unless this exact install bundles lx.

Suggested tests:

- `ServerUriParser_AwgUnsupported_ReturnsFalseFromIsSupportedScheme`
- `ServerUriParser_AwgUnsupported_ManualParseThrowsClearFormatException`
- `VlessUriParser_XhttpUnsupported_ThrowsClearFormatException`
- `VlessUriParser_XhttpSupported_ParsesTransport`

### P1 - Same-host AWG can hijack selected non-AWG server

Evidence:

- `VlessConfig.GetActiveServers()` returns all entries sharing the selected active server host.
- `ConfigGenerator.BuildOutbounds()` then selects `servers.FirstOrDefault(s => protocol is amneziawg/awg)`.
- Therefore selecting a VLESS/HY2/TUIC server can still enter the AWG endpoint branch when an AWG sibling shares the same host.

Impact:

- User selection is not respected. A non-AWG active server can be silently replaced by AWG.

Fix direction:

- Determine the selected active entry explicitly.
- Enter AWG endpoint generation only if the selected entry itself is AWG.
- Keep same-host cross-protocol pooling limited to intentional Naive UDP sibling behavior.

Suggested tests:

- `GetActiveServers_SelectedVlessWithSameHostAwg_DoesNotGenerateAwgEndpoint`
- `GetActiveServers_SelectedAwg_GeneratesAwgEndpoint`

### P1 - AWG URI parser corrupts standard base64 keys

Evidence:

- `ServerUriParser.ParseAmneziaWg` uses `HttpUtility.ParseQueryString`.
- `HttpUtility.ParseQueryString("?private_key=abc+def/ghi==")` decodes `+` as a space.
- WireGuard keys commonly use base64 with `+`, `/`, `=`.

Impact:

- Valid AWG keys can be corrupted at parse time.

Fix direction:

- Either manually parse the raw query with RFC3986 percent-decoding that preserves literal `+`, or define the subscription format as base64url/percent-encoded only and reject nonconforming values.
- Validate required AWG fields at parse time: peer public key, private key, address CIDR, host, positive port.

Suggested tests:

- `ParseAwgUri_PreservesPlusSlashEqualsInKeys`
- `ParseAwgUri_MissingPrivateKey_Throws`
- `ParseAwgUri_MissingAddress_Throws`
- `ParseAwgUri_InvalidPort_Throws`

### P1 - Malformed subscription lines can leak secret URIs into logs

Evidence:

- `SubscriptionFetcher` logs `[Subscription] Failed to parse line: {Line}` on parse exceptions.
- With AWG, `Line` can include `private_key=` and `preshared_key=`.
- `CrashReporter.ScrubSecrets` did not originally include `awg` / `amneziawg` in proxy URI redaction.

Impact:

- A malformed subscription can write private key material to `vpnrouter*.log` before diagnostics export redaction has a chance to scrub it.

Fix direction:

- Do not log full subscription lines. Log only scheme/host or a redacted URI.
- Extend free-form scrubbers to recognize `awg://` and `amneziawg://`.
- Redact key-value shapes for `private_key`, `preshared_key`, `psk`, etc. in logs.

Suggested tests:

- `SubscriptionFetcher_ParseFailure_DoesNotLogAwgPrivateKey`
- `CrashReporter_ScrubSecrets_RedactsAwgUri`
- `DiagnosticsRedactor_RedactLogText_RedactsAwgKeyValueSecrets`

### P1/P2 - `ResolveGameDnsOffProxy` overrides `StrictDns`

Evidence:

- `BuildDns` suppresses LAN system DNS when `strictDns` is true.
- But full-tunnel game DNS rule adds `roblox.com` / `rbxcdn.com` -> `local-dns` whenever `ResolveGameDnsOffProxy` is true, without checking `strictDns`.

Impact:

- `StrictDns=true` no longer means all DNS goes through `vpn-dns`.

Fix direction:

- Guard the game-DNS exception with `!strictDns`, or document and expose a stronger explicit override. The safer default is strict DNS wins.

Suggested test:

- `FullTunnel_StrictDnsAndGameDnsOffProxy_DoesNotRouteGameDnsToLocalDns`

### P2 - Simple-mode paste does not classify AWG links

Evidence:

- `ServerUriParser` supports `awg://` and `amneziawg://`.
- `SimpleInputDetector.Classify` recognizes VLESS/HY2/TUIC/SS/Naive/DNS-tunnel but not AWG.

Impact:

- Manual Simple-mode AWG paste can be rejected before reaching the parser.

Fix direction:

- Add AWG schemes to `SimpleInputDetector`, gated consistently with the AWG capability decision.

Suggested test:

- `SimpleInputDetector_AwgUri_ClassifiesAsServerUri_WhenAwgSupported`

### P2 - DeepVerify does not mirror new AWG/XHTTP generation

Evidence:

- `VlessDeepVerifier.BuildSingleOutboundConfig` dispatches Hysteria2/TUIC/SS/Naive, then falls back to `BuildVlessOutbound`.
- `BuildVlessOutbound` only handles `grpc` and `ws` transport blocks; it does not build `xhttp`.
- There is no AWG endpoint path in DeepVerify.

Impact:

- Deep Verify can false-fail working AWG/XHTTP entries or test a config that does not match production generation.

Fix direction:

- Either implement full parity with production generation for AWG/XHTTP, or return an explicit unsupported result with a localized status.
- Add tests so future protocol additions cannot bypass DeepVerify parity.

Suggested tests:

- `DeepVerify_XhttpEntry_EmitsXhttpTransport_WhenSupported`
- `DeepVerify_AwgEntry_UnsupportedOrEndpointConfig_IsExplicit`

## Ops script risks

File: `plans/roblox-tester-exit-setup.sh`

Findings:

- Runs `bash <(curl -fsSL https://get.hy2.sh/)` as root without pin/checksum/signature.
- Enables UFW after allowing only `22/tcp`; nonstandard SSH ports can lock out a VPS.
- Writes secret configs under default umask.
- Prints HY2/TUIC credentials and AWG client private key to stdout.

Fix direction:

- Pin releases and verify checksums/signatures.
- Detect current SSH port or require `--ssh-port`.
- Set `umask 077` before writing secrets and `chmod 600` generated config files.
- Write client material to a root-only output file; print only the path unless `--print-secrets` is explicitly passed.

## Recommended implementation order

1. Stabilize capability gating for AWG/XHTTP.
2. Fix AWG production validation and selected-server semantics.
3. Fix parser/base64 validation and subscription/log redaction.
4. Fix `StrictDns` precedence.
5. Decide DeepVerify behavior: real support or explicit unsupported.
6. Harden the VPS setup script.

## Verification plan

Run after fixes:

```powershell
dotnet build VPNRouter.sln -c Release
dotnet test VPNRouter.Tests/VPNRouter.Tests.csproj -c Release --filter "FullyQualifiedName~AmneziaWgEndpointTests|FullyQualifiedName~XhttpTransportTests|FullyQualifiedName~GameDnsOffProxyTests|FullyQualifiedName~DiagnosticsRedactorTests|FullyQualifiedName~VlessDeepVerifierTests|FullyQualifiedName~ServerUriParserTests|FullyQualifiedName~SimpleInputDetectorTests"
dotnet test VPNRouter.Tests/VPNRouter.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~VlessServersResolverTests|FullyQualifiedName~ConfigGeneratorEmptyServersGuardTests|FullyQualifiedName~FreeConfigAggregatorPreserveTests"
```

Manual/schema checks:

- With official `sing-box 1.13.14`, AWG/XHTTP inputs must be rejected before config generation.
- With sing-box-lx built by `tools/build-singbox-lx.ps1`, generated AWG and XHTTP configs must pass the matching `sing-box-lx check`.
- Full production AWG path must pass `ConfigPipeline` strict validation.

## Do not do automatically

- Do not ship a rolling candidate from this task unless explicitly requested.
- Do not cut stable.
- Do not force-push or bypass hooks.
- Do not add broad refactors outside the listed files without a separate reason.
