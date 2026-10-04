# N-1a: pings and connect pre-flight you can trust (night pool item 3, first block)

## Why

Evidence: the tester's r20 log (2026-10-03). (1) With the tunnel up every probe answered in under 5 ms ("Implausible: local intercept?"), 10 of 13 servers looked dead and the pre-flight picked a Hysteria2 server (fixed for the choice itself in F-2). (2) The Hysteria2 / TUIC probe reports a silent UDP port as
"Ok 2000 ms": the row showed a green "2000 ms" that is only the timeout. (3) The TCP latency included DNS resolution. (4) A fast answer from a server in the same room (LAN) counted as a fault even without any tunnel. (5) Every Connect on the home screen re-created the subscription from the (pre-filled)
URL: the entry was replaced by an empty one (cached servers and any other subscription dropped), the URL fetched again and the connect blocked when that fetch failed; the pre-flight then probed all 13 servers (a silent UDP port costs 2 s) before connecting.

## What

1. `TcpTlsProbe`: the name is resolved before the timing starts; probe sockets are created through one helper that binds them to the physical interface with IP_UNICAST_IF when the VPN's TUN adapter is up (Windows); the "under 5 ms" interception rule applies only to an unbound probe made while the TUN is up
   (so a LAN server is no longer a fault, and a bound probe measures the real path). `NetworkInterfaceDetector.GetInternetInterfaceIndex` gives the interface (same pick as the split-tunnel bind).
2. "Test all" and the per-row test run while connected when binding is available (before: refused with a message).
3. A silent UDP port is displayed as "UDP ?" (muted, not a green number) and does not count as a green ping.
4. Home screen Connect: an unchanged subscription URL keeps the saved servers (no replace, no fetch when servers are cached; refresh in place when there are none); a changed URL replaces as before. The pre-flight probes the selected server first (2 s budget) and the whole list only when it does not answer.

## Not covered

Auto-select itself (sing-box urltest group), the failover engine and `HealthMonitor` (next blocks of item 3); IPv6-only servers; Linux/macOS interface binding (no-op there); Android probes.

## Verification

Unit tests (`TcpTlsProbeTrustTests`, `SimpleConnectPolicyTests`), existing probe/health suites on windows-worker at the exact SHA, CI; a live run on WINBRAT with the tunnel up (Test all through the physical interface vs. before) in the night harness.

## Rollback

Revert the PR.

## Outcome

Merged as #514 and shipped in r27. Verified live on WINBRAT: with the tunnel up, Test all runs (13 servers in 2.1 s) and shows the same 48-86 ms as with the VPN off; the connect pre-flight probes only the selected server. Follow-ups found by the live runs: #526 (the amber state of the "UDP ?" pill) and #528 (a selected UDP server is kept without a probe that cannot finish).
