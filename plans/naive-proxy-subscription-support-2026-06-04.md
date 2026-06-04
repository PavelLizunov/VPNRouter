# NaiveProxy support (subscription + native) — plan (2026-06-04)

## Trigger
User wants naive-proxy servers usable from a subscription. Latest sing-box has a
`naive` outbound; we currently don't parse `naive://` and don't bundle its
runtime dep.

## Findings (verified 2026-06-04 against bundled sing-box 1.13.10)
- Our `ServerUriParser` / `ConfigGenerator` do NOT handle `naive://` (only the
  CrashReporter regex mentions it — log redaction, not a feature). Native protocols:
  VLESS+Reality, Hysteria2, TUIC v5, Shadowsocks 2022, ShadowTLS.
- sing-box 1.13.10 DOES recognise a `naive` OUTBOUND type, but it is a thin
  wrapper over Chromium **Cronet** — `sing-box check` on a naive outbound fails
  with `FATAL ... cronet: library not found. Place libcronet.dll in the
  executable directory or PATH`. We do NOT bundle libcronet → naive outbound
  cannot run today, even via custom-config mode.
  (naive INBOUND parses without cronet, but that's server-side, irrelevant.)

## Scope to add naive-from-subscription
1. **Bundle libcronet** per-platform next to sing-box: `libcronet.dll` (Win),
   `libcronet.dylib` (mac, x64+arm64), `libcronet.so` (Linux). Source: sing-box
   release `with_cronet`/`with_naive` build OR klzgrad/naiveproxy. This is the
   heavy part — Chromium net stack, tens of MB per platform; inflates every ZIP/
   DMG/AppImage + Android (libcronet.so in the .aar/jniLibs). Decide: bundle vs
   download-on-demand (like Zapret/TgProxy pull from GitHub on first use — likely
   the right call to keep base installer small).
2. **Parser**: add `naive://` (NaiveProxy URI: `naive+https://user:pass@host:port?
   padding=...`) to `ServerUriParser` → a `VlessServerEntry { Protocol="naive" }`
   (Username/Password/Server/Port/Tls/SNI/padding).
3. **ConfigGenerator**: `BuildNaiveOutbound(entry, tag)` → sing-box naive outbound
   (`{ "type":"naive", "server", "server_port", "username", "password",
   "tls":{...} }`). Wire into the `BuildVlessOutbound` protocol switch.
4. **Subscription fetch**: `SubscriptionFetcher.ParseBody` already dedups by
   `Server:Port:UUID:Flow`; extend to accept naive lines (key by Server:Port:User).
5. **Probe/verify**: `TcpTlsProbe` is TLS-based — naive is HTTPS/H2, so a TLS
   probe works for reachability; deep-verify spawns sing-box (needs libcronet
   present → gates on the on-demand fetch).
6. **Tests**: ServerUriParserTests (naive URI shapes), ConfigGenerator naive
   outbound + a `sing-box check` integration that SKIPS when libcronet absent
   (mirrors the multi-server skip), SubscriptionFetcher naive-line parse.
7. **YouTube/QUIC note**: naive is HTTP/2 (TCP) or HTTP/3 (QUIC) via cronet — if
   H2/TCP it has the same QUIC caveat as VLESS (the r4 block_quic_on_tcp_proxy
   default already covers it); if H3 it carries UDP natively (treat like
   hasUdpProxy → don't QUIC-block).

## Risk / cost
- libcronet bundling is the dominant cost (size + per-arch builds + supply-chain
  pinning by SHA256 like libbox.aar). On-demand download (TgProxy/Zapret pattern)
  is the pragmatic path — base installer stays lean, only naive users pull it.
- Protocol itself is niche; Hy2/TUIC/Reality cover most anti-DPI needs. Worth
  doing only if there's real demand for naive-only servers.

## Recommendation / sequencing
Deferred backlog (not a v2.41.0 item). When picked up: do the on-demand
libcronet fetch first (NaiveRuntimeUpdater, mirror TgProxyUpdater), then parser +
generator + subscription, then tests. Estimate: ~1 focused cycle for parser+gen+
sub+tests + ~1 for the runtime fetch/bundle + per-platform verification.
