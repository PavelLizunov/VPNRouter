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
