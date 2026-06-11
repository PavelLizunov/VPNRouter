# StrictDns auto-off when no internet (continuous) — v2.42.0-r2

## Problem (user logs, 2026-06-11)
Broken config had `strict_dns: true` + `dns_leak_lockdown: true` + `config_mode: subscribe`.
StrictDns forces `dns.final = vpn-dns` (DoH **through the proxy/tunnel**). When the
selected server is dead/slow (germany), ALL DNS rides the dead tunnel → "endless
loading / no internet". User-agreed fix: **"весь DNS через туннель" должен сам
отключаться, когда нет интернета** (continuous, not just at connect).

NOT related to the dns-tunnel protocol — separate from the v2.42.0-r1 loopback fix.

## Design (mirror DnsLeakLockdown fail-open pattern)
- **Lever**: `dns.final` — `vpn-dns` (through tunnel) when StrictDns active, `local-dns`
  (Cloudflare DoH on real NIC) when suppressed. One field, hot-reloadable.
- **Scope**: only when StrictDns is the **sole** driver of `vpn-dns` — split/include
  mode, not full-tunnel/exclude (those legitimately need all-DNS-through-tunnel; never
  override them).
- **Detector**: Clash API `GET /proxies/proxy/delay?url=gstatic204&timeout=3000` —
  tests the proxy's REAL reachability without touching `dns.final` (so it works as both
  the fail-over trigger AND the flap-safe re-arm signal; no DNS blips).
- **Policy** (`StrictDnsFailoverPolicy.Decide`, pure/testable):
  `suppress = strictDnsSoleDriver && !proxyHealthy`
  - suppress && !failedOver → FailOpen
  - !suppress && failedOver → ReArm
  - else None
- **Orchestration** (HealthMonitor tick, only when strictDnsSoleDriver || failedOver):
  probe proxy delay (debounced N consecutive) → FailOpen: regen config with
  `strictDnsOverride=false` (dns.final→local-dns) + hot-reload + log/event;
  ReArm: regen normal (dns.final→vpn-dns) + reload + log/event.
- **Suppression mechanism**: thread `bool? strictDnsOverride` through
  `ConfigPipeline.Generate → ConfigGenerator.Generate → BuildDns`
  (`defaultVpnDns = full || exclude || (strictDnsOverride ?? settings.App.StrictDns)`).
  Optional param, null default — VpnEngine callers unchanged.

## Files
- `ISingBoxApi.cs` + `ClashSingBoxApi.cs` + `Fakes/FakeSingBoxApi.cs` — `GetProxyDelayAsync`.
- `StrictDnsFailoverPolicy.cs` (new, pure).
- `ConfigPipeline.cs` + `ConfigGenerator.cs` — thread `strictDnsOverride`.
- `HealthMonitor.cs` — probe + reconcile + GenerateConfigJson override + reload.
- Tests: `StrictDnsFailoverPolicyTests`, ConfigGenerator override case, HealthMonitor wiring.

## Verify
Unit tests (policy truth table + override + wiring) against installed Core.dll; plus
live: connect to a dead/slow server with StrictDns on → DNS fails over to direct →
internet returns; server recovers → re-arms. Folds into v2.42.0-r2.
