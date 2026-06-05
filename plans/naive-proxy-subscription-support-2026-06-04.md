# NaiveProxy support (subscription + native) — plan

> **2026-06-05 CORRECTION.** The original 2026-06-04 "Findings" said `sing-box
> check` on a naive outbound fails with `cronet: library not found`. **That was
> WRONG** — it only fails when `libcronet` is absent from the binary dir. The
> official SagerNet sing-box release archive **ships `libcronet` alongside the
> binary** on Windows + Linux, and naive works out of the box there. macOS is
> the only real gap. Re-verified below against 1.13.10 / 1.13.13 / 1.14-alpha.28.

## Trigger
User wants naive-proxy servers usable from a subscription. Shipping as **v2.41.1**.

## Verified findings (2026-06-05)

### Runtime: naive already works on Win+Linux, never on macOS
- Our bundled sing-box build tags include **`with_naive_outbound`** + `with_purego`
  (it dlopens libcronet at runtime). `sing-box run` on a naive outbound prints
  `NaiveProxy started, version: 147.0.7727.49` — **no rebuild needed**.
- The official release **archive** ships the runtime lib next to the binary:

  | Platform | naive tag | libcronet in archive | naive runs? |
  |---|---|---|---|
  | windows-amd64 | yes | `libcronet.dll` (~9 MB) | **YES** |
  | linux-amd64 | yes | `libcronet.so` | **YES** |
  | darwin-arm64 / darwin-amd64 | (tag present) | **NO `libcronet.dylib`** | **NO** |

- Checked v1.13.10, latest stable **v1.13.13**, and dev **v1.14.0-alpha.28** —
  **macOS never ships libcronet** on any version. SagerNet simply doesn't build
  Cronet for Darwin. A mac libcronet would be a third-party/self-build supply-chain
  project → **out of scope; macOS is gated off for naive.**

### The actual gap: our packaging drops libcronet
`build.ps1` copies **only `sing-box.exe`** from the upstream archive (lines ~280,
~313, and the update-bootstrap ~461-463). So naive works on a dev box (libcronet
sits in `tools/singbox-cache/...`) but would FATAL for an end user. **Fix = stop
cherry-picking; copy libcronet too.**

### Verified naive outbound schema (against real `sing-box check`)
Deliberately minimal — these are sing-box's own constraints:
- OK: `{ type:"naive", tag, server, server_port, username, password,
  tls:{ enabled:true, server_name } }`
- `tls.insecure:false` — **accepted**; `tls.insecure:true` — rejected.
- `network`, `tls.alpn`, `tls.utls` — **all rejected** on naive outbound.
- So `BuildNaiveOutbound` emits only `username/password` + `tls{enabled,server_name}`.

### URI form (community / NekoBox / sing-box subscription import)
```
naive+https://user:pass@host:port?sni=...#name   (HTTP/2 over TCP)
naive+quic://user:pass@host:port#name            (HTTP/3 over QUIC)
naive://user:pass@host:port#name                 (bare → treated as https)
```
`+https` / `+quic` is informational (naive outbound takes no `network` field).

## Locked decisions
1. **Platforms: Windows + Linux only.** macOS naive is gated (no upstream
   libcronet). Android = out of scope (libbox.aar, separate runtime).
2. **libcronet shipping: ALWAYS BUNDLE** (copy from the same archive we already
   download). Rationale: it's ~0 new code (just stop dropping it), +~9 MB on a
   package that already carries a 44 MB sing-box, and naive servers arrive via
   *subscription* (not an explicit opt-in button like Zapret/TgProxy) — so an
   on-demand fetch would force a blocking 9 MB download mid-connect = worse UX.
   On-demand stays a future option if installer size ever matters.

## Slices / status (v2.41.1)
1. **Data layer — DONE (build green 2026-06-05):**
   - `VlessServerEntry.Username` (YAML) + `SingBoxOutbound.Username` (JSON).
   - `ServerUriParser.ParseNaive` + dispatch + `IsSupportedScheme` (3 schemes).
   - `ConfigGenerator.BuildNaiveOutbound` + `"naive"` switch case.
2. **macOS gate — TODO:** filter naive entries from active-server selection /
   config-gen on macOS with a clear "unsupported on macOS" UI marker; never emit
   a naive outbound on mac (it would FATAL at sing-box start). Also: subscription
   may still SHOW the server, just not Connect with it on mac.
3. **Packaging — TODO:** `build.ps1` (+`libcronet.dll`), Linux CI (+`libcronet.so`),
   update-bootstrap. Verify the shipped ZIP/deb actually contains it.
4. **Subscription dedup — TODO:** `SubscriptionFetcher` dedup key currently
   `Server:Port:UUID:Flow`; naive has neither → extend to fold in `Username`.
5. **Tests — TODO:** `ServerUriParserTests` naive shapes; a ConfigGenerator naive
   `sing-box check` integration (passes on Win since libcronet present); sub-line
   parse; mac-gate test. Also check `Username` survives any `VlessServerEntry`
   clone/persist path (grep for copy sites).
6. **Ship:** AppVersion → `2.41.1-r1`, rolling-rN, MCP verify (Win), stable on
   user command.

## QUIC note
naive over H2 = TCP (same `block_quic_on_tcp_proxy` r4 caveat applies); over H3 =
QUIC/UDP native. Outbound takes no network field, so no special handling needed —
the existing QUIC-block default is correct either way.
