# GOAL: Tester's Roblox works from RU, no Error 277

**North star:** the tester completes a **30+ minute Roblox session with zero Error 277**,
repeatable, from his static-IP RU line — without setting up a second VPN app by hand.

**Why the earlier ideas don't suffice (evidence):** 277 is a RakNet idle-timeout (no game
UDP for ~10-15 s). RU TSPU injects loss/jitter on the access leg (RU->exit), invisible to
sing-box (proxy log stays clean), starving RakNet. On plain VLESS the game UDP rides
xudp-over-TCP (HoL amplifies loss); Hysteria2 is QUIC (RU QUIC-throttles it, and VPNRouter
runs it on BBR, no calibrated Brutal). The discriminator vs the unaffected user is the
tester's **static IP** (pinnable for per-flow soft-throttle), not his (better) hardware.
RU-community data: stock WG ~12% vs **AmneziaWG ~94%** success for Roblox (Moscow, Dec-2025).
Full RCA + sources: `plans/roblox-277-rca-2026-06-27.md`, `plans/roblox-277-ru-vpn-research`.
(Decision: do NOT use the existing Wgturn/EmergencyChannel component for this.)

## Workstreams

### T1 — AmneziaWG exit for the tester  [LOAD-BEARING] · owner: user provisions, I spec
Stand up an **AmneziaWG** endpoint the tester can use as the game's UDP exit. AmneziaWG
because plain obfuscated-UDP has no QUIC signature and no loss-amplifying CC — the
community's #1 working transport. Two paths (pick one):
- **(a) New dedicated VPS** for the tester — a clean /24, **close to RU** (Finland / Baltics
  / Germany) with good Cloudflare/Roblox peering (shorter, less-jittery RU->exit leg + a
  nearer Roblox edge).
- **(b) Add AmneziaWG to the existing subscription server.**
**My deliverable:** provider/location spec, AmneziaWG install + server config (Jc/Jmin/Jmax/
S1/S2/H1-H4 obfuscation params), firewall/port spec, and the subscription-entry format so
VPNRouter can pull it. **(I cannot rent/pay — user does that; I provide everything else.)**
**Acceptance:** tester's VPNRouter lists + connects via the AmneziaWG exit.

### T2 — VPNRouter routes Roblox realtime UDP through a UDP-native / WG exit  [code] · me
Make the game's realtime UDP ride the AmneziaWG (or UDP-native HY2/TUIC) exit while
login/web stays on the working in-tunnel proxy. Includes:
- Confirm/implement a **WireGuard/AmneziaWG outbound** in `ConfigGenerator` (sing-box
  `wireguard` outbound; assess AmneziaWG-obfuscation support, plan integration if not native).
- Per-game route: `RobloxPlayerBeta.exe` UDP -> the WG/UDP-native outbound.
- Extend RB1/RB2 so a **plain-VLESS active server no longer forces all UDP over TCP**
  (`hasUdpProxy=false` today) — pair it with a co-located UDP-native exit.
**Acceptance:** generated config sends Roblox UDP to the WG/UDP-native outbound; sing-box check passes.

### T3 — Don't tear down the live game on reconfig  [code] · me
Gate sing-box "Forced full restart (structural change)" to a **hot-reload** when only route
rules changed (diag showed 5 full restarts mid-session; each drops the live RakNet NAT map =
instant 277). Keep the already-landed HealthMonitor 2-consecutive-fail debounce.
**Acceptance:** a subscription refresh / route-only change during play does not restart sing-box.

### T4 — Roblox DNS off the proxy detour  [code] · me
In full-tunnel, `vpn-dns` (Detour=proxy) is `dns.final`, so `*.roblox.com` resolves through
the congested proxy (diag: 1023 lookups >=10 s). Bound the remote-DoH timeout + fallback, or
resolve Roblox domains off the detour (real-NIC `local-dns`/`dns-direct` exists, unused in
full-tunnel). Keep web/login DNS on proxy.
**Acceptance:** Roblox DNS lookups don't stall on a slow proxy DoH; joins don't hang.

### T5 — RB4 inner-stream detector + cross-transport failover  [code] · me
Wire the staged `UdpDegradationDetector` with a **TUN-boundary inner-stream signal**
(jitter/reorder/loss of RakNet UDP toward the game servers) — the only place the
277-degradation is visible — and make auto-failover cross **transport families**
(QUIC -> AmneziaWG/Reality-XUDP), not just servers within one transport.
**Acceptance:** sustained inner-stream degradation triggers a failover to the WG/UDP-native exit.

### T6 — Subscription-server protocol spec  [deliverable] · me writes, user applies
A concrete prompt/spec for the subscription server admin (you) to expose the new
protocol(s): AmneziaWG (primary), and optionally a **calibrated Hysteria2** (up/down ~75% of
the measured RU->exit goodput, NOT a static default; + Salamander) and/or TUIC as fallbacks.
Includes the exact server config + how each appears in the subscription.
**Acceptance:** the subscription returns the new protocol entries; VPNRouter parses them.

### T7 — Live RU validation loop  [tester + me]
The tester A/B-tests each lever and sends a diag; I analyse and iterate:
1. **Tonight, no code, no new app:** rotate the tester to a different exit IP / closer exit
   from the existing subscription, play 20 min. (Cheapest test of the static-IP-throttle.)
2. AmneziaWG exit (T1) -> play 20-30 min.
3. Each code fix (T2-T5) -> re-test.
**Acceptance (= goal done):** a clean repeatable 30+ min Roblox session, no 277.

## Order
T1 + T2 are the load-bearing pair (UDP-native/WG exit + route the game through it). T7.1
(exit rotation) runs tonight in parallel as a free datapoint. T3/T4/T5 harden. T6 is the
server-side enabler for T1/T2.

## Status (2026-06-27 — autonomous code complete)
- **T2 — DONE** (93b338b5): Hysteria2 Brutal calibration via `?up=&down=`. + verified the
  other A/B transports are already optimal: **TUIC `udp_relay_mode=native`**, **VLESS leaves
  `packet_encoding` at the sing-box xudp default (never empty)**. So the A/B test is VALID.
- **T6 — DONE**: `plans/roblox-tester-vps-spec-2026-06-27.md`.
- **T3 — already correct, no change**: `ApplyAsync` only forces a full restart on a genuine
  structural change (RoutingMode flip / TUN-fingerprint change — hot-reload can't re-lay TUN
  routes; Brat-2026-05-04 incident). The diag's mid-game restarts were the tester *changing
  MTU/server/routing during testing* — necessary, not a normal-play bug. Transient health
  restarts already covered by the landed 2-consecutive-fail debounce. Server/route changes
  hot-reload (don't restart). No safe gap to widen.
- **T4 — deferred (downstream + risky-blind)**: in full-tunnel Roblox DNS rides `vpn-dns`
  (DoH via the congested proxy). It CAN move to `local-dns` (Cloudflare DoH on the real NIC —
  encrypted, so NOT RU-poisoned), which would skip the proxy. But that depends on Cloudflare-
  DoH reachability from RU (un-verifiable blind) and is a behavior change for ALL users, and
  the stall is downstream of the proxy congestion that T1/T2 fix. Hold as a gated opt-in if
  the transport fix doesn't fully clear it.
- **T5 — blocked on T1**: cross-transport failover needs a non-QUIC (WG) outbound to fail
  over to (all QUIC shares the RU throttle), which doesn't exist until T1; and a clean
  inner-stream jitter signal isn't readily available. Revisit once T1 lands.
- **T1 — needs YOU**: provision the VPS (I can't rent/pay). Spec ready.
- **T7 — needs the tester**: live RU A/B.

### Two validation tracks (T7)
- **Track A (fast, NO new VPS):** add a **calibrated Hysteria2** entry (`?up=&down=` ~75% of
  the tester's measured speed + Salamander) and a **TUIC** entry to the EXISTING subscription
  server (T6 spec §3/§4). The tester A/B-tests them via VPNRouter *today* — this directly
  tests whether T2 (calibrated HY2) / TUIC clears the 277 without any new infra.
- **Track B (new VPS):** closer/cleaner-IP exit + AmneziaWG (validate via the Amnezia app
  once). Needed only if Track A doesn't clear it.

**Critical path to "tester works": Track A first (you add HY2-calibrated/TUIC to the existing
subscription → tester tests) — it needs no new VPS and exercises the code that's already
shipped.** Track B + the AmneziaWG-fork decision only if Track A fails.

## Constraints
- I cannot rent a VPS or pay / create accounts — user does the financial/account steps; I
  provide specs, scripts, and configs.
- Do NOT reuse Wgturn/EmergencyChannel for the game path (user decision, 2026-06-27).
- Keep all Roblox in-tunnel (login needs the VPN; roblox.com is RU-blocked). No route-direct.
