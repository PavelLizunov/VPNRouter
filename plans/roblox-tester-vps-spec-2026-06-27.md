# T1/T6 — VPS + multi-protocol server spec for the tester's Roblox exit

Goal: give the tester an exit that survives RU TSPU for realtime game UDP. We host
SEVERAL protocols on ONE VPS so the tester A/B-tests which actually beats his path,
evidence-first, before we commit client-side effort (esp. the AmneziaWG fork decision).

**I (Claude) cannot rent/pay/create accounts — you do those steps. Everything below is the spec + configs.**

## 1. The VPS to rent
- **Location: close to RU + good EU/Cloudflare peering** — Helsinki (Finland) or a Baltic
  city (Tallinn/Riga) beats Frankfurt: shorter, less-jittery RU->exit leg AND a nearer
  Roblox edge. Avoid US.
- **Clean IP / good /24 reputation** (not a flagged hosting range). A fresh dedicated IP for
  the tester also tests the "static-IP soft-throttle" angle.
- **Specs:** 1-2 vCPU, 1-2 GB RAM, >=1 Gbps port, Debian 12 / Ubuntu 22.04. Cheap is fine.
- **Providers that tend to have clean EU IPs:** Hetzner (Helsinki), Aeza, 3xLOGIC, Serversaurus,
  or any with a Finland/Baltic PoP. (Pick one you can pay; verify the IP isn't already RU-blocked
  by pinging a RU speedtest after setup.)
- Open UDP ports in the firewall for each protocol below (and 22/tcp for SSH).

## 2. Protocol A — AmneziaWG  (HIGHEST community success: ~94% vs ~12% plain WG)
Validated FIRST with the standalone **AmneziaVPN app** on the tester's gaming PC (one-time, to
confirm 94% holds for HIS path) — NOT a permanent second VPN. If it fixes 277, that justifies the
client-side work (sing-box-lx fork OR a separate awg integration — a decision we make THEN).

```bash
# Debian/Ubuntu — AmneziaWG (awg) server
apt update && apt install -y wireguard-tools iptables
# install amneziawg-go (userspace) or the awg kernel module per docs.amnezia.org
# generate keys:
awg genkey | tee server.key | awg pubkey > server.pub
awg genkey | tee client.key | awg pubkey > client.pub
```
`/etc/amnezia/amneziawg/awg0.conf` (server) — the obfuscation params Jc/Jmin/Jmax/S1/S2/H1-H4
are what defeat DPI; keep them IDENTICAL on client and server:
```
[Interface]
Address = 10.13.13.1/24
ListenPort = 51820
PrivateKey = <server.key>
Jc = 4
Jmin = 40
Jmax = 70
S1 = 86
S2 = 574
H1 = 1234567890
H2 = 2345678901
H3 = 3456789012
H4 = 4567890123
PostUp = iptables -A FORWARD -i awg0 -j ACCEPT; iptables -t nat -A POSTROUTING -o eth0 -j MASQUERADE
PostDown = iptables -D FORWARD -i awg0 -j ACCEPT; iptables -t nat -D POSTROUTING -o eth0 -j MASQUERADE

[Peer]
PublicKey = <client.pub>
AllowedIPs = 10.13.13.2/32
```
Client (Amnezia app import) uses the same Jc..H4, `Endpoint = <vps-ip>:51820`, `AllowedIPs = 0.0.0.0/0`.
(Generate the 4 random H values once and reuse them; the J/S values above are common defaults.)

## 3. Protocol B — Hysteria2, CALIBRATED  (sing-box-native, VPNRouter can use TODAY)
This is the lever the diag exposed: VPNRouter set NO up/down -> HY2 ran BBR -> exposed. With
Brutal calibrated to the tester's REAL goodput it can hold. Install `sing-box` or `hysteria`
server:
```yaml
# /etc/hysteria/config.yaml  (hysteria2 server)
listen: :8443
tls:
  cert: /etc/hysteria/cert.pem   # real cert (acme) or self-signed + client insecure
  key:  /etc/hysteria/key.pem
obfs:
  type: salamander
  salamander:
    password: <obfs-pass>        # stops the QUIC classifier throttling after ~30s
auth:
  type: password
  password: <auth-pass>
# IMPORTANT: server doesn't set client bandwidth; the CLIENT declares up/down.
# The client (VPNRouter) must declare up/down ~= 70-80% of the tester's MEASURED
# RU->this-VPS goodput (run a speedtest tester->VPS first). Over-declaring self-induces loss.
```
Subscription entry: `hysteria2://<auth-pass>@<vps-ip>:8443/?obfs=salamander&obfs-password=<obfs-pass>&sni=<sni>#TesterHY2-Helsinki`

## 4. Protocol C — TUIC  (sing-box-native, gentler QUIC) — optional fallback
```json
// sing-box server inbound (tuic)
{ "type":"tuic","listen":"::","listen_port":8444,
  "users":[{"uuid":"<uuid>","password":"<pw>"}],
  "congestion_control":"bbr",
  "tls":{"enabled":true,"certificate_path":"...","key_path":"..."} }
```
Subscription entry: `tuic://<uuid>:<pw>@<vps-ip>:8444?congestion_control=bbr&sni=<sni>&udp_relay_mode=native#TesterTUIC`

## 5. Protocol D — VLESS+Reality with XUDP  (sing-box-native, community #2)
Standard VLESS+Reality node (you already run these); ensure the client uses packet_encoding
xudp (sing-box default) for FullCone. TCP-shaped -> dodges QUIC throttling, mild HoL risk.
Subscription entry: your usual `vless://...?security=reality&flow=xtls-rprx-vision...` at this VPS.

## 6. How the tester A/B-tests (T7 loop)
On the SAME VPS, in order of expected success:
1. **AmneziaWG** via the Amnezia app, play 20-30 min. (Confirms the 94% direction for HIS path.)
2. **Calibrated Hysteria2** via VPNRouter (after I add the up/down field), with up/down set to
   ~75% of his measured tester->VPS speed.
3. **TUIC**, then **VLESS-XUDP**, via VPNRouter.
Send a diag after each. The first that gives a clean 30-min no-277 session wins; we then make
that the tester's default and (if it's AmneziaWG) decide the client integration path.

## What I'm doing in parallel (client side, no provisioning needed)
- Add a per-server **Hysteria2 up/down (Brutal) calibration field** so #3 is usable (T2 core).
- T3 (don't restart mid-game) + T4 (Roblox DNS off the proxy detour).
- Hold the AmneziaWG client integration (sing-box-lx fork vs separate awg client) until the
  Amnezia-app test proves it's worth it — that fork breaks the "no custom sing-box rebuild"
  rule, so it's your explicit call when we get there.
