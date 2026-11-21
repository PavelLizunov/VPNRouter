# VMess standard subscription support (2026-11-22)

## Intent

Import the VMess entries produced by the VPNRouter app feed and stock sing-box JSON
subscriptions, render a dedicated sing-box VMess outbound, and keep the existing
VLESS/VMess two-hop association fail-closed when the entry is absent.

## Scope

- Advertise `vmess-v1` beside `detour-v1` (and `dnstt-v1` when available).
- Parse standard `vmess://` payloads: padded base64 JSON with
  `v, ps, add, port, id, aid, scy, net, type, host, path, tls, sni, alpn`
  (optional `fp`, `allowInsecure`, `outbound`, `detour`).
- Generate `type: vmess` with `security`, `alter_id: 0`, TLS server name, WS
  Host/path, and `domain_resolver: local-dns`.
- Accept only AEAD VMess (`aid 0`; `aes-128-gcm` or `chacha20-poly1305`;
  `auto` normalizes to `aes-128-gcm`). Legacy `aid > 0`, `none`, `zero`, and
  `aes-128-ctr` are rejected.
- Map stock sing-box JSON `type: vmess` outbounds that use only representable
  TCP/WS, TLS, AEAD, and keepalive options.
- Allow a VMess chained target whose one upstream is a direct VLESS Reality row.
  Never dial the target directly when the upstream is missing or ambiguous.
- Reuse the existing association, dedupe, selection, sanity-check, and verifier
  paths; add VMess to the allowlists that enumerate outbound types.

## Invariants

- Existing VLESS subscription bytes and generated config shape stay unchanged.
  New model fields are null/default and omitted for non-VMess rows, so stored
  row keys and YAML do not change for existing entries.
- A VMess share URI carries `add` as the dial address. For a hop, `add` is the
  literal origin IP while `host` and `sni` remain the public certificate name.
- `outbound` and `detour` are opaque IDs. A target is selected only when exactly
  one active row has a matching `outbound` ID and no detour of its own.
- VMess JSON IDs are matched as literal strings. The VLESS entry URI keeps its
  normal percent-encoded `outbound=<id>` query.
- A VMess exit never becomes a chain entry. The chain validation accepts one
  direct VLESS Reality upstream only.

## Verification

Not run on the workstation per `docs/agent-contract.md` (no local SDK build or
full test run). The patch was checked with `git apply --check` against
`83f9bac7`. The Windows-VM has eight logical CPUs, 13 GiB available RAM and
36 GiB free on C:, with no .NET SDK available. CI owns the initial build gate.

CI must run the Core config/connection oracle and the full suite before merge:

```powershell
dotnet test VPNRouter.Tests/VPNRouter.Tests.csproj -c Release --filter "FullyQualifiedName~ConfigGeneratorTests|FullyQualifiedName~VpnEngineStartAsyncSeamTests|FullyQualifiedName~SingBoxManagerProcessRunnerTests"
dotnet build VPNRouter.sln -c Release
dotnet test VPNRouter.Tests/VPNRouter.Tests.csproj -c Release --no-build
```

Targeted tests added in `VmessSubscriptionTests.cs` cover import, refresh,
config build, chain association after ID rotation, missing-entry fail-closed
behavior, and verifier shape. `SingBoxJsonSubscriptionTests` covers stock JSON
mapping and native VMess chain association. Capability-header expectations are
updated in `AppConfigDetourTests` and `DnsttSubscriptionTests`. Android/macOS
runtime behavior remains unverified until CI and a packaged candidate exercise it.

## Unknowns

- Clash YAML VMess proxies are not mapped; stock sing-box JSON is the supported
  native alternative.
- Standard VMess without TLS and TCP VMess are parsed for direct compatibility;
  chained VMess targets require TLS, matching the app feed contract (TLS + WS).
  Enforce TLS for direct entries at the feed boundary if required.
- VMess stock JSON outbounds with `network`, `multiplex`, gRPC/HTTP transport, or
  non-AEAD security are skipped rather than silently degraded.
- No live server was exercised; DNS bootstrap and WS Host behavior are pinned by
  generated-config tests, not by a live connection.
