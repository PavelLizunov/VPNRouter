# v2.44.2 — Dota 2 region latency "ОШИБКА" through the proxy (Steam SDR UDP)

## Триггер
User report 2026-06-24 (on v2.44.0 stable, diag 20260624-233025). Dota 2
matchmaking "ВЫБЕРИТЕ РЕГИОНЫ ПОДБОРА ИГР" shows **"Задержка: ОШИБКА"** for ALL
regions + "Не удалось вычислить задержку. Проверьте интернет-соединение."
User says it persists "даже в режиме split tunnel", suspects DNS or MTU.

## Симптом
Dota cannot compute matchmaking latency to any region while VPNRouter is connected.

## Root cause (diag-confirmed)
Dota region latency = **Steam Datagram Relay (SDR)** — small UDP probes to Valve
relay IPs (UDP ports ~27015-27068, 3478, 4379-4380). In the diag:
- `route.final = proxy` (full-tunnel) → ALL traffic incl. SDR UDP goes through the
  VLESS proxy.
- vless outbound: **`packet_encoding = None`** + `flow = xtls-rprx-vision` → VLESS
  UDP relay is limited/absent → the SDR UDP probes get no response → every region
  reports ERROR.
- NOT MTU: TUN `mtu = 1280` is already conservative; SDR probes are small, so
  fragmentation is not the cause (the user's MTU hypothesis is wrong).
- NOT DNS per se: probes are UDP to relay IPs; DNS only matters for the SDR config
  fetch (HTTPS, which works via proxy).
- Split-tunnel: persists when Dota/Valve IPs are still routed via the proxy (geo
  routing) or Dota is in the routed app list.

## Fix strategy (v2.44.2)
**(a) PREFERRED — built-in "Steam/Valve SDR direct" route rule.** Add a route rule
that sends Valve SDR UDP DIRECT (bypass the proxy), so latency probes take the real
path and report true latency. Targets: Valve IP ranges (e.g. 155.133.224.0/19,
162.254.192.0/21, 185.25.180.0/22, 146.66.152.0/21, 205.196.6.0/24) + the SDR UDP
port set, network=udp. Implement in `ConfigGenerator` as an optional "Gaming
bypass" toggle (default on?), generating a `direct`-outbound route rule ahead of
`final=proxy`. Real latency + lower proxy load. Works in full + split.
**(b) ALT — `packet_encoding = xudp` on the vless outbounds** (if servers support
xudp): UDP would forward through the proxy, but latency is measured via the proxy's
exit location (wrong region latencies). Inferior to (a) for gaming.

Recommend (a). Consider both: SDR-direct rule + xudp for general UDP apps.

## Acceptance
- [ ] Dota "ВЫБЕРИТЕ РЕГИОНЫ" shows real ms (not ОШИБКА) for nearby regions while
      connected, in BOTH full-tunnel and split-tunnel.
- [ ] No leak: only Valve SDR ranges/ports go direct; everything else stays on proxy.
- [ ] Regression test for the SDR direct-route rule generation.

## Оценка
~M, routing-rule + UI toggle + Dota live test. Needs a Dota install to verify.

## Связь
- `VPNRouter.Core/Services/ConfigGenerator.cs` (route rules)
- diag fixture: `C:\Project\logs\VPNRouter-diagnostics-20260624-233025.zip`
- No emoji per project rule.
