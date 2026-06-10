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
3. **Binary distribution (REVISED 2026-06-10 per user):** there is NO pre-built
   .exe and no pinned fork. Upstream `github.com/Mygod/slipstream-rust`
   (Apache-2.0) builds from source (Rust + picoquic via
   `scripts/build_picoquic_windows.ps1` + `cargo build --release -p
   slipstream-client`, ≥2 GB RAM). **MVP: build the .exe ourselves and place it
   at `SlipstreamExePath`.** A pinned cross-platform release under a single
   `const {repo,tag,asset}` comes later → **`SlipstreamUpdater` (slice 5) is
   deferred, not blocking.** If the binary is absent, `SlipstreamManager` throws
   a clear "build/install slipstream-client" error.

## Cert handling — RESOLVED (user, 2026-06-10)

slipstream-client has `--cert <leaf.pem>` (full PEM); there is **NO `--pin
<sha256>` flag** — the client verifies the server-presented leaf against the
PEM. Decision: **the full leaf.pem lives IN THE PROFILE** (the `dns-tunnel://`
payload), NOT bundled with the binary and NOT reduced to a fingerprint-pin.
Rationale: self-contained — scales to multi-server / cert rotation with no
client rebuild.
- `VlessServerEntry.DnsLeafCertPem` carries the full PEM (load-bearing).
- `SlipstreamManager` writes that PEM to `AppPaths.SlipstreamActiveCertPath` at
  launch and passes `--cert <that path>`. Cleaned on Stop. (The leaf is a
  *public* server cert — not a secret — so on-disk write is fine.)
- `DnsLeafFingerprint` stays optional in the link for display + an integrity
  cross-check (if present, `SlipstreamManager` verifies sha256(PEM)==fingerprint
  and refuses on mismatch — catches a tampered/corrupt link).

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

1. **Foundation** (this slice): AppPaths + model fields + parser + parser tests. ✅ DONE
2. SlipstreamManager + tests (FakeProcessRunner seam). ✅ DONE
3. ConfigGenerator dns-tunnel outbound + sing-box-check test. ✅ DONE
4. VpnEngine lifecycle coupling + LeakProtection awareness + tests. ✅ DONE
5. SlipstreamUpdater (once repo named). ⏳ DEFERRED (no pinned release yet)
6. App UI (link intake + badge + status). ✅ DONE

All shipped inert/gated behind `Protocol == "dns-tunnel"` (strict equality) → zero
effect on existing servers. 6 commits on `main`. Unit suites: ServerUriParserDnsTunnel
(15), SlipstreamManager (10), ConfigGeneratorDnsTunnel (1), DnsTunnelUi (3).

## Build provenance — Windows slipstream-client.exe (2026-06-10)

Built from source on the dev VM (no upstream pre-built .exe exists). Recorded so CI
or a future rebuild is reproducible. Build tree lives on the **D: EXTRA** disk
(`D:\build`), off the cramped 3 GB C:.

**Toolchain assembled:**
- VS Build Tools 2022 → `D:\VS2022BT` (MSVC 14.44.35207, Windows SDK 10.0.22621, bundled CMake 3.31).
- Rust 1.96 msvc host → `CARGO_HOME=D:\rust\cargo`, `RUSTUP_HOME=D:\rust\rustup`.
- vcpkg → `D:\vcpkg`; `openssl:x64-windows-static-md@3.6.2` (static OpenSSL, **dynamic CRT**).
  vcpkg also fetched **pkgconf 2.5.1** to `D:\vcpkg\dl\tools\msys2\...\mingw64\bin\pkgconf.exe`.
- slipstream-rust checkout + picoquic submodule → `D:\build\slipstream-rust`.

**The non-obvious blockers (each a one-liner once known):**
1. slproweb OpenSSL installer download stalled at 0 bytes (RU network filtering — the
   very problem this feature exists for) → switched to **vcpkg** (GitHub-hosted) OpenSSL.
2. `scripts/build_picoquic_windows.ps1` uses the PowerShell-7 automatic `$IsWindows`
   under `Set-StrictMode`; we drive it from Windows PowerShell **5.1** → `do_build.ps1`
   pre-sets `$IsWindows = $true`.
3. picotls' `CMakeLists.txt:12` does `find_package(PkgConfig)` (REQUIRED) → fails on
   Windows with no pkg-config. **Fix:** `do_build.ps1` locates vcpkg's pkgconf and exports
   `PKG_CONFIG_EXECUTABLE`/`PKG_CONFIG`; the build script then passes `-DPKG_CONFIG_EXECUTABLE`.
   (The earlier `pthread.lib LNK1104` noise was a benign FindThreads probe, not the cause.)

**Build recipe** (`D:\build\do_build.ps1`): import vcvars64 env → prepend CMake+cargo to
PATH → set CARGO/RUSTUP/VCPKG_ROOT + PKG_CONFIG_EXECUTABLE → `build_picoquic_windows.ps1
-Configuration Release -Platform x64` → `cargo build --release -p slipstream-client`.
Result: `target\release\slipstream-client.exe` (6.5 MB, cargo stage 53s).

**Verified (2026-06-10):**
- `--help` CLI flags match `SlipstreamManager` argv exactly: `--cert`, `-d/--domain`,
  `-l/--tcp-listen-port`, `--tcp-listen-host`, `-r/--resolver` (repeatable). No `--uuid`
  on the client (correct — uuid lives in the sing-box VLESS layer).
- **Local-chain smoke PASS:** spawned with a dummy self-signed leaf + НСДИ resolvers →
  `INFO Listening on TCP port 7001` and `127.0.0.1:7001` accepts TCP **before** any tunnel
  is reachable. Confirms `WaitForPortListening` ordering + the fail-closed model.
- SHA256 `ab6500bdcef3b2a563972617b4e4c60725d830391840896585ecd85ff64f71bd`.
- Placed at `SlipstreamExePath` (`%ProgramData%\VPNRouter\slipstream\bin\slipstream-client.exe`).
- **Distribution note (slice 5):** only non-OS DLL dep is `VCRUNTIME140.dll` (the
  `api-ms-win-crt-*` are UCRT, always on Win10/11). Clean machines may lack it → either
  bundle the ~120 KB DLL beside the exe, or rebuild fully static
  (`RUSTFLAGS=-C target-feature=+crt-static` + `x64-windows-static` OpenSSL).

## Full tunnel e2e — attempted against real link (2026-06-10)

User supplied a real link: `dns-tunnel://<b64>#main-brat` for domain `t.ninitux.top`,
uuid `5550051c-…-b918118f86ef`, resolvers `195.208.4.1:53` + `195.208.5.1:53`.

**Parser bug found + fixed (commit 6032875):** the production server emits SHORT keys
`{cert,d,fp,r,uuid,v}`, but `ParseDnsTunnel` only read the long spellings
(`domain/resolvers/fingerprint`) → threw "missing domain" on EVERY real link. Now reads
short keys first, long as fallback, ignores `v`. `fp` verified == sha256(leaf DER);
`NormalizeHex` already handles the `AA:BB:CC` form. Pinned by `Parse_RealProductionLink_Parses`
+ `Parse_ShortKeys_ProductionSchema_…` (27 dns-tunnel tests green).

**Headless e2e (no TUN/admin): client + integration PROVEN, tunnel blocked server-side.**
- НСДИ resolvers reachable (both answer google.com/example.com).
- slipstream-client (our build) spawns, binds 7001, and **sends Recursive-mode DNS tunnel
  queries** to `195.208.4.1:53` (trace: `mode=Recursive send_pkts+=6 send_bytes+=846`).
- sing-box `mixed:10808 → vless → 127.0.0.1:7001` config validates + routes correctly
  (`outbound/vless[proxy]: outbound connection to api.ipify.org:80`).
- BUT the tunnel never establishes (`streams=0` forever); curl through it times out (exit 28).
- **Root cause (server-side DNS):** `t.ninitux.top` has **NO NS delegation**. NXDOMAIN from
  Cloudflare (its parent NS), 1.1.1.1, and 8.8.8.8; a `<x>.t.ninitux.top` query to the НСДИ
  resolver returns SERVFAIL. The resolver answers normal domains fine, so it's healthy — the
  recursive path just has nothing to delegate to. `ninitux.top` is on Cloudflare
  (colin/eloise.ns.cloudflare.com) but no `t` sub-zone is carved out to the slipstream server.

**To finish the e2e, server side needs ONE of:**
1. **NS delegation (the censorship-resistant path):** in Cloudflare DNS for `ninitux.top`, add
   `t  NS  <ns-host>` + glue `<ns-host>  A  <slipstream-server-public-IP>`, so the НСДИ
   resolver can reach the slipstream server as authoritative for `t.ninitux.top`.
2. **Direct `--authoritative <server-ip>:53`** (bypasses delegation): proves tunnel+integration
   but sends DNS straight to the server IP (DPI can see/block it) — only a debug fallback, not
   the production path. Needs the server's public IP (not in DNS; user has it).

The client + VPNRouter integration are correct as far as testable without a working
server-side delegation; the remaining failure is purely the deployed server's DNS setup.
