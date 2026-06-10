# DNS-tunnel (slipstream) transport — client integration

**Created:** 2026-06-10 · **Status:** in progress (foundation slice)

Last-resort transport: VLESS traffic tunnelled inside DNS queries to НСДИ
resolvers (195.208.4.1 / 195.208.5.1) via a `slipstream-client` sidecar
(Rust/QUIC-over-DNS, github.com/Mygod/slipstream-rust). Server side is already
in prod. Only the client integration is needed.

## Architecture (two processes)

```
app traffic ──TUN──▶ sing-box ──VLESS outbound──▶ 127.0.0.1:<localPort>
                                                         │
                                              slipstream-client (sidecar)
                                                         │
                                              DNS queries to НСДИ resolvers
                                                         ▼
                                              dns-tunnel server (prod)
```

- `slipstream-client` listens on a local TCP port; everything in → tunnelled
  over DNS. It is the **transport**.
- sing-box VLESS outbound points at `127.0.0.1:<localPort>` instead of the real
  server, **no TLS on the VLESS layer** (slipstream does its own QUIC-TLS).

## Decisions locked (user, 2026-06-10)

1. **Scope = Windows-only MVP.** Build + e2e-test on the dev VM. Linux (binary
   already built upstream) and Android (JNI native lib, anonvector/SlipNet
   reference) are separate later phases.
2. **Activation = manual only.** User picks the dns-tunnel server/link like any
   server. No auto-failover wiring in MVP (that's a phase-2 tie-in with
   AutoFailover's "no candidate servers left" path).
3. **Distribution = GitHub release (pull-on-demand)**, mirroring TgProxy/Zapret.
   ⚠ **OPEN: repo + tag + asset names** — fill `SlipstreamUpdater.RepoPublic`
   const + asset-name convention once the user names the release. Everything
   else is independent of this; `SlipstreamUpdater` is the only blocked file.

## Open clarification — cert vs fingerprint

The `dns-tunnel://` profile carries `fingerprint` (sha256 of leaf), but the CLI
wants `--cert <leaf.pem>` (full PEM). A hash can't reconstruct a PEM. MVP
assumption: **the leaf.pem is a server property (same for all users) and ships
in the release bundle** alongside the binary, written to `SlipstreamCertPath`;
the per-user `fingerprint` is a **pin** the manager verifies against the bundled
PEM's sha256 before launch (refuse on mismatch). Confirm slipstream-client has a
pin mode OR that the leaf ships with the binary.

## Local port — MVP simplification

Fixed default `7001` (const `SlipstreamManager.DefaultLocalPort`). Manager does
a pre-flight bind probe (TgProxy:79 pattern); clear error if taken. Config-gen
uses the same const. Dynamic free-port + manager→engine→config-gen plumbing is a
future improvement (noted, not MVP).

## Key architectural note — NOT an independent sidecar

TgProxy/Zapret are user-toggled, independent of the VPN lifecycle. **Slipstream
is a transport dependency of the connection**: the VLESS outbound points at a
dead local port until slipstream is up. So `VpnEngine.StartAsync`, when the
active server is `dns-tunnel`, must:
1. start `SlipstreamManager` → **wait until the local port is listening**,
2. only then start sing-box,
3. on Stop: stop sing-box, then slipstream.
**Fail-closed**: slipstream didn't come up → do NOT start sing-box; surface the
honest status (reuses the v2.41.2-r3 dead-config surfacing). This is the only
genuinely new lifecycle coupling.

## File plan (Core, Windows MVP)

| File | Change |
|---|---|
| `AppPaths.cs` | + `SlipstreamDir/BinDir/ExePath/CertPath/VersionPath/LogPath` (wgturn pattern) |
| `Models/AppSettings.cs` `VlessServerEntry` | + `Protocol="dns-tunnel"` doc; + `DnsDomain`, `DnsResolvers`, `DnsLeafFingerprint` fields |
| `Services/ServerUriParser.cs` | + `SlipstreamRuntimeAvailable`; dispatch branch; `ParseDnsTunnel` (base64url-JSON); `IsSupportedScheme` entry |
| `Services/SlipstreamManager.cs` (new) | clone of TgProxyManager: start/stop, port pre-flight, 2s watchdog, SuppressExitedEvent, pin-verify, slipstream.log |
| `Services/SlipstreamUpdater.cs` (new) | clone of TgProxyUpdater (simpler): pull binary+cert from GitHub release → bin/, version.txt. ⚠ repo const TBD |
| `Services/ConfigGenerator.cs` | `dns-tunnel` dispatch in BuildVlessOutbound → plain VLESS outbound to 127.0.0.1:port, **no TLS block** |
| `Services/VpnEngine.cs` | lifecycle coupling above (start/await/stop slipstream around sing-box) |
| `Services/LeakProtection.cs` | recognise dns-tunnel no-TLS-local outbound as legitimate (don't false-warn) |

App (UI): accept `dns-tunnel://` in config box + subscription; server-type badge;
status surfacing. Distribution: the GitHub release (repo TBD).

## Slice order (each compiles + tests green before next)

1. **Foundation** (this slice): AppPaths + model fields + parser + parser tests.
2. SlipstreamManager + tests (FakeProcessRunner seam).
3. ConfigGenerator dns-tunnel outbound + sing-box-check test.
4. VpnEngine lifecycle coupling + LeakProtection awareness + tests.
5. SlipstreamUpdater (once repo named).
6. App UI (link intake + badge + status).
