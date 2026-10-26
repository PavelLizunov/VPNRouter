# N4 T: TUN self-routing loop

Investigate the owner's unexplained upload volume on the authorized Omarchy VM,
using the r52 backend and 3bb9b30 plugin. No owner workstation operations.

Before edits, HY2 reproduced a self-sustaining ICMP loop after plugin rescan:
60 seconds added 80,576,296 TUN TX bytes while ens18 TX added 2,592,265 bytes.
Metadata capture contains echo requests from 172.19.0.1 to virtual peer
172.19.0.2 with changing IDs. Raw sockets belong to sing-box and are bound to
VPNRouter-TUN. The shell's network-status command pings the routed gateway.
Ordinary HY2 and VLESS idle/download baselines did not flood.

Fix scope: prevent direct outbound ICMP routing back into the TUN's own address
prefix in generated and injected configurations. Preserve DNS interception
and private LAN access, configured address prefixes and existing
custom/geo rule ordering. No UI/asset changes or protocol tuning.

Verification: generated/custom JSON regression cases; dispatched backend and
Arch CI at the exact task SHA; installed package on the VM; explicit peer ping,
rescan/restart, 60-second idle plus 10 MiB download on HY2 and VLESS, kernel
counters, metadata captures and raw-socket ownership. Leave VPN disconnected.

Unknown before repair: the owner's inaccessible laptop and full IPv6 dataplane.
Evidence remains in the private N4 T-evidence directory; no server addresses,
credentials or raw configuration are committed.

## Observed outcome

Product fix ed82e139 rejects only ICMP to the configured virtual prefix, keeping
DNS and other private destinations. Five generated/custom cases cover a changed
prefix, both rule priorities, normalized legacy IPv4/IPv6 and a non-TUN config.
Initial CI found four existing fixed-index assertions needing the new guard in
their expected infrastructure prefix. cef9397e preserves their explicit ordering
checks. Backend CI 40260604256 and Arch CI 40260602362 are green.

Installed r53 (ed82e139) on the authorized VM and observed 76 one-second samples
per scenario. After peer ping, rescan and cold shell restart, HY2/VLESS TUN TX
grew 57,880 / 55,002 bytes over 60 seconds, compared with 80,576,296 before the
fix in the HY2 rescan scenario. Both fixed captures contain four peer echo
requests and four unreachable replies, not the 1,987 echo requests in the first
2,000 pre-fix packets. Raw sockets owned by sing-box fell from 15,201 in the
VLESS single-ping reproduction to zero for both fixed protocols.

Each fixed 10 MiB download returned HTTP 200, in 1.228392 / 1.629313 seconds.
Download-period TUN RX/TX were 11,087,241/222,315 bytes for HY2 and
11,031,767/194,027 for VLESS. RX maps to download and TX to upload in Headless;
SessionStatus uses the same order. Real RU Home capture was inspected. DNS
remained on the virtual peer with the catch-all route domain, and physical LAN
ping succeeded. Service, engine and TUN identities survived both reload types.

The final exact-commit package receipt, CI logs, cleanup and remote branch SHA
are recorded in the private round T report. No plugin product files changed.
Custom IPv6 is covered by JSON tests only; its live dataplane, all other field
VLESS/PMTU issues and the inaccessible owner laptop remain outside this proof.
