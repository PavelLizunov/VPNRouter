# H-17: let the kill switch pass Tailscale and IPv6 local ranges

## Why

Audit finding LINUX-NFT-TAILSCALE-LOCKOUT (2026-09-29). The engaged block rules accepted only
loopback, RFC 1918, 169.254/16 and the server IPs, so an engaged kill switch cut remote
management over Tailscale and IPv6 neighbor discovery. Owner decision 2026-09-29: allow
everything Tailscale-related, including MagicDNS.

## What

- Linux nftables and macOS pf rulesets also pass `100.64.0.0/10` (Tailscale CGNAT, includes the
  MagicDNS resolver 100.100.100.100), `fe80::/10` (IPv6 link-local) and `fc00::/7` (IPv6 ULA,
  includes the Tailscale ULA range and its MagicDNS address).
- One test per platform pins the new ranges.

## Not covered

Traffic to these ranges is local or overlay traffic only. Public destinations stay blocked. The
Windows firewall path is unchanged.

## Verification

Exact-head CI (Windows job runs the full suite including both firewall test classes).

## Outcome

Pending CI.
