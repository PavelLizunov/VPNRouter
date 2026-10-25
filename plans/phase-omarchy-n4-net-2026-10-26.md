# Omarchy Linux protocol and DNS field repair

## Intent and invariants

Investigate the owner's VLESS/HY2 disparity, TUN routing and MTU behavior on the authorized Omarchy test VM. Work starts at 2cc9e7e7. Preserve other VPN routes, user overrides, subscription credentials and unrelated shared VM workloads. No release or default-branch merge.

## Behavior contract

System resolver requests in a Linux full tunnel must reach sing-box DNS rather than a DHCP LAN resolver excluded by private routes. Reuse the per-link resolved adapter and a narrowly scoped package-owned polkit grant if measurements confirm it works. Keep DNS-lockdown capability honest: resolver steering is not a firewall lockdown. Select a different TUN stack or MTU policy only with measured improvement and compatibility evidence. Preserve VLESS subscription fields.

## Verification

Compare matched HTTPS downloads, TTFB and HTTP/3 requests through VLESS/HY2. Inspect policy routes, TUN byte counters, system proxy settings and LAN DNS counters. Exercise alternate stacks with a temporary interface-only firewall rule, uplink MTU changes and ICMP filtering; restore test changes and disconnect before releasing the VM lock. Run logic regressions and package checks; .NET builds/tests and package creation run in dispatched GitHub CI at the task SHA. Install the resulting package and repeat the same measurements.

## Observed so far

- VM initially has r33; systemd-resolved uses DHCP DNS 192.168.0.1 on ens18. Connected HY2 config has a TUN-only inbound, MTU 1420, and resolved reports no DNS scope on VPNRouter-TUN.
- A connected resolver lookup reports ens18 and unencrypted network transport. Temporary nft counter observes LAN UDP/53 traffic.
- tcpdump is absent; pacman install failed with a 404 from the configured Omarchy mirror. Use nft counters and resolved link attribution instead.
- phase-task-launcher is not present in the available skill/tool catalog; the explicit task contract supplies the bounded work record.

## Implementation and checks

- Reuse LinuxDnsHardening for generated full, exclude and strict-DNS routing even while the stronger DNS-lockdown capability remains false. Use the TUN peer, reject loopback targets, and reapply after sing-box restart.
- Package the exact interface/action/local-session polkit grant and verify its bytes, mode and presence during package inspection. Packaging metadata records its SHA.
- Local packaging suite: 127 tests passed. Polkit rule executed in Node against 60 allowed/denied action/interface/session combinations: all passed.
- Initial matched throughput is roughly 9-10 MB/s for both protocols under gVisor. system/mixed TCP times out with ufw; system+auto_redirect works with the packaged capabilities. Neither removing Reality record fragmentation nor auto_redirect showed a consistent latency improvement. Keep existing defaults pending further evidence.
- The original Cloudflare 50 MB endpoint later rate-limited repeated measurements with HTTP 429. Those responses are not throughput samples; subsequent bounded MTU measurements use a separate endpoint and will state that method change.
