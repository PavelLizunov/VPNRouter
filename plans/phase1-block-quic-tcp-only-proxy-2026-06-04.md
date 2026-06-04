# Block QUIC on TCP-only proxy — YouTube full-tunnel fix (2026-06-04)

Brief for the v2.41.0-r4 ConfigGenerator change. Triggered by a real Windows
user diagnostics bundle (`VPNRouter-diagnostics-20260604-165536.zip`):
"постоянно проблемы с youtube".

## Why

YouTube serves video over QUIC (HTTP/3, UDP/443) to `*.googlevideo.com`. In
**full-tunnel** mode every packet — including that QUIC — is routed into the
single proxy outbound, which for a subscription/manual VLESS server is
**VLESS+Reality with `flow: xtls-rprx-vision` = a reliable TCP transport**.

QUIC carried over a reliable TCP tunnel suffers head-of-line blocking
("TCP-over-TCP meltdown"): QUIC's own loss recovery fights TCP retransmission →
stalls, buffering, throughput collapse. Vision-flow splice only helps TCP; UDP
relay over VLESS is un-optimized and many Reality servers relay UDP poorly.

Critically, the QUIC path is *slow/lossy, not cleanly rejected*, so Chrome does
not fall back to HTTP/2-over-TCP — it keeps retrying QUIC → "constant YouTube
problems". The user's `current.json` has **no QUIC route rule**.

Evidence from the bundle's `singbox-tail.log`:
- 186 TCP `outbound connection` to googlevideo:443 — succeed, 100-112ms.
- 40 UDP `outbound packet connection` to googlevideo:443 → routed to `vless[proxy]`.
- No sing-box errors (1 benign WSAECONNABORTED) — health-check stays green while
  the user suffers.

`Strings.cs:684-685` already documents "VLESS+Reality routes TCP only. For UDP
(games, QUIC) use a custom config with a TUIC/Hysteria2 outbound." — confirming
the limitation is known; we just never *acted* on it for the generated path.

The standard, proven fix (v2rayN / Nekoray / Hiddify all ship a "Block QUIC"):
**reject QUIC when the proxy is TCP-only**, forcing the browser onto
HTTP/2-over-TCP, which rides VLESS cleanly (the 186 working TCP connections
prove the TCP path is healthy).

## What

- `VPNRouter.Core/Models/AppSettings.cs` — new `AppConfig.BlockQuicOnTcpProxy`
  (`block_quic_on_tcp_proxy`), default **true**, next to `ForceIpv4Only`.
- `VPNRouter.Core/Services/ConfigGenerator.cs` `BuildRoute(...)` — thread the
  flag in; after the private-IP rule (so LAN/QUIC to private IPs already went
  direct and is untouched), when `blockQuic && !hasUdpProxy`:
  - **full tunnel OR exclude mode** (final=proxy → ~all traffic proxied):
    global `{ "protocol": "quic", "action": "reject" }`.
  - **split include** (only listed apps ride the TCP-only proxy): scoped
    `{ "process_name": [...], "protocol": "quic", "action": "reject" }`.
  - Skipped when `hasUdpProxy` (a UDP-capable/dual outbound exists) — don't
    override the user's deliberate UDP routing.
- Call site at ConfigGenerator.cs:106 passes
  `settings.App.BlockQuicOnTcpProxy`.

`reject` on `protocol: quic` relies on the always-on `sniff` rule (route rule
#0) which detects the QUIC Initial. Private IPs are already routed direct above
the reject, so LAN QUIC (mDNS-over-QUIC, local services) is never blocked.

Before:
```
[sniff, hijack-dns, private→direct]                  final=proxy   (QUIC tunneled, stalls)
```
After (full tunnel, VLESS-only):
```
[sniff, hijack-dns, private→direct, quic→reject]     final=proxy   (QUIC rejected → TCP fallback)
```

## How

1. Add `BlockQuicOnTcpProxy` to AppSettings (default true) + XML doc.
2. Add `bool blockQuic = true` param to `BuildRoute`; insert reject rule(s).
3. Update call site to pass the setting.
4. Tests in a new `ConfigGeneratorQuicBlockTests.cs`:
   - full tunnel + VLESS-only → global `protocol:quic`+`reject` present.
   - split include + VLESS-only → scoped reject with process_name.
   - exclude mode → global reject present.
   - `hasUdpProxy` (mixed flow/no-flow servers) → NO reject rule.
   - flag false → NO reject rule.
   - reject sits AFTER private-ip and (include) BEFORE the per-app proxy route.
   - sing-box `check` integration still passes (skip if binary absent).

## Verification gate

- [ ] `dotnet build VPNRouter.sln -c Release` 0 errors
- [ ] full test suite green incl. new ConfigGeneratorQuicBlockTests
- [ ] regression: ConfigGeneratorTests / ConfigGeneratorEmptyServersGuardTests /
      LeakProtectionTests unaffected
- [ ] simplify if diff >100 LOC; this is leak-path adjacent → quick self-review
- [ ] Core-only (no UI surface) — MCP-not-applicable label in ship report;
      the default-on behavior fixes all full-tunnel VLESS users with no UI

## Risk

LOW. Additive route rule; behavior change is "QUIC→reject" which always has a
TCP fallback by web design. Gated to TCP-only proxies (`!hasUdpProxy`) so
TUIC/Hysteria2 setups are untouched. Default-on is a strict improvement for the
dominant config (subscription/manual VLESS). Power users can set
`block_quic_on_tcp_proxy: false` to restore old behavior. No leak-protection
invariant changes (LeakProtection doesn't assert on QUIC).

## Follow-ups

- UI toggle on the Network page ("Block QUIC / fix YouTube on VLESS") — later
  -rN, needs MCP verify; not required for the fix to work.
- Consider same reject for the `hasUdpProxy` case too (proxy-udp is still
  VLESS=TCP in the generated path) — deferred, conservative for now.

## Outcome (filled 2026-06-04)

**Status**: PASS (pending full-suite + ship)
**Files changed**:
- `VPNRouter.Core/Models/AppSettings.cs` — `BlockQuicOnTcpProxy` (default true).
- `VPNRouter.Core/Services/ConfigGenerator.cs` — `BuildRoute` param + reject rule
  insertion after private-IP; call site passes the setting.
- `VPNRouter.Tests/ConfigGeneratorQuicBlockTests.cs` — NEW, 10 tests.
- `VPNRouter.Tests/ConfigGeneratorIncludeModeTests.cs` — scoped the proc-rule
  filter to `Action=="route"` (the QUIC reject also carries process_name).
**Verification gate results**:
- [x] Gate 1 build: Core 0 errors (44 pre-existing CA1416 warnings only).
- [x] Gate 2 tests: 10/10 new + 190/192 broad regression green (2 skipped =
      multi-server sing-box check). Full suite pending.
- [x] sing-box 1.13.10 `check` PASS on the generated full-tunnel QUIC config —
      proves `{ "protocol":"quic", "action":"reject" }` valid + LeakProtection OK.
- [x] Gate 3 docs: brief + release notes written.
- [-] Gate 4 self-review: diff < 100 LOC product code; leak-path adjacent —
      reviewed inline (private-IP-first ordering keeps LAN QUIC direct; gated to
      `!hasUdpProxy`; no LeakProtection invariant touched).
- [-] Gate 5 MCP verify: Core-only (no UI surface) — N/A; default-on behavior
      fixes all full-tunnel VLESS users without UI.
**Surprises**:
- `GetActiveServers()` only keeps the active server's same-IP TCP+UDP pair —
  the `hasUdpProxy` test needed both servers on one IP (fixed).
- LeakProtection rejects fake reality keys — integration test fixture needed a
  valid 32-byte base64url public key.
- One pre-existing include-mode test assumed every process_name rule is a route
  rule; refined to `Action=="route"`.
**Rollback**: `git revert <hash>` or set `block_quic_on_tcp_proxy: false`.
**Follow-up**: UI toggle on Network page (later -rN, needs MCP verify).
