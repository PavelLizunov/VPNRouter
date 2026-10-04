# R-27: faster connect, honest pings and a smarter failover, candidate v2.50.0-r27

## Why

The first block of the owner's night pool (item 3, ping measurement and automatic server choice). Evidence came from the live harness on WINBRAT (r26: connect 10-12 s, stop 5-6 s, every connect probed all servers and resolved 92 process names with where.exe). This brief follows `.dsh/skills/ship-rolling-candidate/SKILL.md`.

## What

1. Version bump PR: `AppVersion.Version` becomes `2.50.0-r27` (this PR).
2. Contents since r26: N-1a #514 (pings and the connect pre-flight you can trust: probes bound to the physical NIC, the "Implausible" rule only for unbound probes, no probe of every server on each connect), N-1b #516 (process names resolved in process, DNS lockdown rules through the firewall API), N-3a #519 (failover picks the fastest reachable server of the first eight, failover and UDP-check messages in the app language, no crash tail on a normal stop, per-app firewall skip lines at debug level).
3. After it is merged: annotated tag, draft prerelease, six Windows files (unsigned) built on `windows-worker`, macOS/Linux/Android from the tag workflows, prepublication gate, `check-open-p0.ps1` with the recorded waiver, publish as a prerelease.
4. Post-ship on WINBRAT with `tools/post-ship-local.ps1`, then the live harness `cycles`, `servers`, `ping`, `modes` runs against the installed r27 (timings go in the Outcome).

## Not covered

TUN adapter removal wait on stop, IPv6-only servers in probes, the Android side, the version selector (item 1), everything open in the r26 brief.

## Verification

Exact-head CI on this PR; the live evidence is recorded in the Outcome.

## Outcome

Published 2026-10-04 as prerelease `v2.50.0-r27` (`--latest=false`; Latest stays `v2.49.3`), annotated tag on main `c35bfac8`, 18 assets (six Windows files built on `windows-worker`, hashed there and compared by size, SHA-256 and sidecar before upload; macOS, Linux and Android from the tag workflows), strict CI gate `OK` with 8 green, waiver gate applied, integrity run green, `POSTSHIP-LOCAL: PASS` (2 cold cycles). Contents: N-1a #514, N-1b #516, N-3a #519, N-1c #522, N-3b #523, N-3c #524 and the tools #518, #520.

Live evidence on WINBRAT with the installed r27 (`live_run` scenarios; r26 numbers from the same scenarios a few hours earlier):

| | r26 | r27 |
|---|---|---|
| connect from the home screen (Hysteria2 server selected) | 10.6-12.3 s | 7.65-7.71 s |
| connect to a chosen server (Advanced shell) | 7.3-8.8 s | 3.6-3.9 s |
| stop | 5.3-5.9 s | 4.3-4.5 s |
| Test all, 13 servers, VPN off | 2.0 s | 2.0 s |
| Test all, VPN on | no probes (UI note) | 2.1 s, same 48-86 ms values as with the VPN off |
| Deep verify, 13 servers, VPN off / on | 10.6-15.9 s / n.a. | 10.9 s / 22-28 s |

No warnings or errors in the app log in any run, public IP changed while connected and was restored each time, no `VPNRouter_*` firewall rules left after a disconnect (the one remaining line is sing-box's own `sing-tun` rule), 6 mode switches never showed "Connect", tabs walk 0 failures after the harness fixes.

Found by those runs and fixed after the tag (next candidate): a selected Hysteria2/TUIC/AmneziaWG server cost 2 s on a pre-connect probe that could not finish (N-3e #528), the amber state of the "UDP ?" pill was not re-notified (N-3d #526), harness fixes (#525, #529).

Not run: the screenshot gate, the live-update gate (stable channel to r27), the Other versions list against real releases, macOS, Linux, Android on a device.
