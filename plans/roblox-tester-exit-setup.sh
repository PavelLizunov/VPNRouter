#!/usr/bin/env bash
# Roblox-tester exit setup — AmneziaWG + calibrated Hysteria2 + TUIC on one Debian 12 box.
# Implements plans/roblox-tester-vps-spec-2026-06-27.md so T1 becomes ~one command.
#
# !!! REVIEW BEFORE RUNNING. Written WITHOUT a server to test on (Claude can't rent/run a VPS),
#     so treat it as a careful draft: read it, adapt package/service names to your distro, run
#     section by section if unsure. It opens UDP ports + enables IP forwarding + NAT.
#
# Usage:  sudo bash roblox-tester-exit-setup.sh <PUBLIC_IP_OR_HOSTNAME>
# Output: prints the client subscription URIs (HY2 + TUIC) and the AmneziaWG client config to
#         paste into the AmneziaVPN app / your subscription server.
set -euo pipefail

HOST="${1:?usage: sudo bash roblox-tester-exit-setup.sh <public-ip-or-hostname>}"
WAN_IF="$(ip route get 1.1.1.1 2>/dev/null | grep -oP 'dev \K\S+' || echo eth0)"
HY2_PORT=8443; TUIC_PORT=8444; AWG_PORT=51820
rand() { head -c "${1:-16}" /dev/urandom | base64 | tr -dc 'A-Za-z0-9' | head -c "${1:-16}"; }

echo "== base: forwarding + NAT + firewall (WAN=$WAN_IF) =="
apt-get update -y
apt-get install -y curl wget jq iproute2 iptables nftables ufw openssl
sysctl -w net.ipv4.ip_forward=1
grep -q '^net.ipv4.ip_forward=1' /etc/sysctl.conf || echo 'net.ipv4.ip_forward=1' >> /etc/sysctl.conf
ufw allow 22/tcp || true
ufw allow ${AWG_PORT}/udp || true
ufw allow ${HY2_PORT}/udp || true
ufw allow ${TUIC_PORT}/udp || true
yes | ufw enable || true

# ── AmneziaWG (awg) — the 94% transport. Obfuscation params Jc..H4 must MATCH on client. ──
echo "== AmneziaWG =="
# Install amneziawg-tools (awg/awg-quick). Repo: github.com/amnezia-vpn/amneziawg-* . If the
# distro lacks a package, build amneziawg-go or use the AmneziaVPN server installer instead.
apt-get install -y wireguard-tools || true
command -v awg >/dev/null || { echo "  awg not found — install amneziawg-tools (amnezia-vpn/amneziawg-tools) then re-run the AWG section."; }
mkdir -p /etc/amnezia/amneziawg
SK=$(awg genkey 2>/dev/null || wg genkey); PK=$(echo "$SK" | (awg pubkey 2>/dev/null || wg pubkey))
CSK=$(awg genkey 2>/dev/null || wg genkey); CPK=$(echo "$CSK" | (awg pubkey 2>/dev/null || wg pubkey))
# Obfuscation: Jc junk-packet count, Jmin/Jmax junk sizes, S1/S2 init/response junk, H1-H4 magic
# headers. These four H values are random per-deployment; reuse the SAME set on the client.
H1=$((RANDOM*RANDOM)); H2=$((RANDOM*RANDOM)); H3=$((RANDOM*RANDOM)); H4=$((RANDOM*RANDOM))
JC=4; JMIN=40; JMAX=70; S1=86; S2=574
cat >/etc/amnezia/amneziawg/awg0.conf <<EOF
[Interface]
Address = 10.13.13.1/24
ListenPort = ${AWG_PORT}
PrivateKey = ${SK}
Jc = ${JC}
Jmin = ${JMIN}
Jmax = ${JMAX}
S1 = ${S1}
S2 = ${S2}
H1 = ${H1}
H2 = ${H2}
H3 = ${H3}
H4 = ${H4}
PostUp = iptables -A FORWARD -i awg0 -j ACCEPT; iptables -t nat -A POSTROUTING -o ${WAN_IF} -j MASQUERADE
PostDown = iptables -D FORWARD -i awg0 -j ACCEPT; iptables -t nat -D POSTROUTING -o ${WAN_IF} -j MASQUERADE

[Peer]
PublicKey = ${CPK}
AllowedIPs = 10.13.13.2/32
EOF
(awg-quick up awg0 && systemctl enable awg-quick@awg0) 2>/dev/null || echo "  bring up awg0 manually: awg-quick up awg0"

# ── Hysteria2 (calibrated-ready) + Salamander obfs. Client declares up/down ~75% of measured. ──
echo "== Hysteria2 =="
bash <(curl -fsSL https://get.hy2.sh/) 2>/dev/null || echo "  install hysteria2 manually (app.hysteria.network)"
HY2_PW=$(rand 24); HY2_OBFS=$(rand 24)
openssl req -x509 -newkey ec -pkeyopt ec_paramgen_curve:prime256v1 -keyout /etc/hysteria/key.pem \
  -out /etc/hysteria/cert.pem -days 3650 -nodes -subj "/CN=${HOST}" 2>/dev/null || true
cat >/etc/hysteria/config.yaml <<EOF
listen: :${HY2_PORT}
tls: { cert: /etc/hysteria/cert.pem, key: /etc/hysteria/key.pem }
obfs: { type: salamander, salamander: { password: ${HY2_OBFS} } }
auth: { type: password, password: ${HY2_PW} }
EOF
systemctl enable --now hysteria-server 2>/dev/null || echo "  start hysteria-server manually"

# ── TUIC (gentler QUIC fallback) ──
echo "== TUIC =="
TUIC_UUID=$(cat /proc/sys/kernel/random/uuid); TUIC_PW=$(rand 24)
# Install the tuic-server binary from github.com/EAimTY/tuic releases, then:
cat >/etc/tuic-config.json <<EOF
{ "server": "[::]:${TUIC_PORT}",
  "users": { "${TUIC_UUID}": "${TUIC_PW}" },
  "certificate": "/etc/hysteria/cert.pem", "private_key": "/etc/hysteria/key.pem",
  "congestion_control": "bbr", "alpn": ["h3"] }
EOF
echo "  (install tuic-server binary + a systemd unit pointing at /etc/tuic-config.json)"

# ── Output: client subscription entries ──
cat <<OUT

================  PASTE THESE INTO YOUR SUBSCRIPTION / AmneziaVPN  ================
Hysteria2 (set up/down to ~75% of the tester's MEASURED speed to this box):
  hysteria2://${HY2_PW}@${HOST}:${HY2_PORT}/?obfs=salamander&obfs-password=${HY2_OBFS}&sni=${HOST}&insecure=1&up=50&down=75#Tester-HY2

TUIC:
  tuic://${TUIC_UUID}:${TUIC_PW}@${HOST}:${TUIC_PORT}?congestion_control=bbr&alpn=h3&sni=${HOST}&allow_insecure=1&udp_relay_mode=native#Tester-TUIC

AmneziaWG (import into the AmneziaVPN app on the tester's PC — Jc..H4 MUST match):
  [Interface]
  PrivateKey = ${CSK}
  Address = 10.13.13.2/32
  DNS = 1.1.1.1
  Jc = ${JC}
  Jmin = ${JMIN}
  Jmax = ${JMAX}
  S1 = ${S1}
  S2 = ${S2}
  H1 = ${H1}
  H2 = ${H2}
  H3 = ${H3}
  H4 = ${H4}
  [Peer]
  PublicKey = ${PK}
  Endpoint = ${HOST}:${AWG_PORT}
  AllowedIPs = 0.0.0.0/0
  PersistentKeepalive = 25
==================================================================================
NOTE: self-signed TLS -> the HY2/TUIC URIs carry insecure=1. Use a real (ACME) cert if you
prefer strict TLS. Verify each service is listening:  ss -ulnp | grep -E '${AWG_PORT}|${HY2_PORT}|${TUIC_PORT}'
OUT
