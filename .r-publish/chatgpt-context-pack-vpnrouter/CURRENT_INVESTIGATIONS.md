# Current Investigations

Snapshot date: 2026-07-09.

This file is intentionally separate from the stable project context. Treat it as active troubleshooting notes, not as permanent product truth.

## Roblox on VLESS/TUN

Observed symptom:

- Roblox disconnects with Error 277.
- Basic DNS/HTTPS connectivity can still look alive.
- ICMP MTU probing can suggest one value, while Roblox still fails.

Working assumptions:

- MTU is only one input.
- VLESS/TUN path behavior, UDP/WebSocket/session stability, DNS, and transport choice can also matter.
- Auto-picking MTU from ping should not be presented as a guaranteed Roblox fix.

Useful diagnostics:

- generated sing-box config
- current TUN MTU
- route table
- DNS logs around Roblox domains
- proxy transport and selected server
- whether the same Roblox session works on AWG or another server

## Discord Voice in Russia

Context:

- Discord is blocked in Russia.
- Default advice should not be "bypass VPN" unless the user confirms direct Discord works on their ISP.

Observed symptom:

- Discord voice can show huge ping spikes under AWG.
- Logs can show Discord DNS/voice relay stalls.

Useful diagnostics:

- whether Discord app is routed through VPN or bypass
- DNS resolution latency for Discord domains
- selected server and transport
- UDP behavior and relay region
- whether another server/transport fixes voice

## True Split Conflicts

Observed symptom:

- True Split may fail to start when another split-tunnel driver/service exists.
- Connectivity can return "General failure" while driver-level split is active or broken.

Working assumptions:

- True Split cannot be assumed to coexist with Amnezia/Mullvad-like split drivers.
- UI should explain conflict and offer the safest repair path.
- App should not crash or silently leave the user in a broken state.

Useful diagnostics:

- `sc.exe query` for split-tunnel driver services
- Windows event logs around driver start
- VPNRouter logs
- whether another VPN app is installed/running
- route table before/after enabling True Split
