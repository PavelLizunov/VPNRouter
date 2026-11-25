# Phase: Honor `dns.local_dns` for RF-resilient direct/bootstrap DNS (2026-11-25)

## Intent

Fix `ConfigGenerator.Dns.cs` (and `CustomConfigInjector`) ignoring `settings.Dns.LocalDns`
and unconditionally hardcoding `https://8.8.8.8/dns-query` over `dns-direct` for `local-dns`.
On Russian ISP networks where `8.8.8.8` (UDP/53 and/or TCP/443 DoH) is blocked or throttled
by TSPU, hardcoding `8.8.8.8` breaks domain-based VPN server bootstrap, `vpn-dns` DoH hostname
resolution, and split-tunnel direct apps even when the operator configures a reachable
`dns.local_dns` resolver (such as `https://77.88.8.8/dns-query`, `tls://77.88.8.8`,
`udp://77.88.8.8`, `77.88.8.8`, or explicit `system`).

## Scope

- `VPNRouter.Core/Services/ConfigGenerator.Dns.cs`:
  - Add `BuildLocalDnsServer(AppSettings settings, out DnsServer? bootstrapServer)` so
    `local-dns` honors `settings.Dns.LocalDns` when explicitly configured:
    - `null`, whitespace, or `"local"` (the legacy default in `EngineSettings.cs`) retains
      the existing encrypted default `{ type: "https", server: "8.8.8.8", path: "/dns-query", detour: "dns-direct" }`.
    - `"system"` / `"os"` emits `{ tag: "local-dns", type: "local" }` (explicit opt-in to OS
      resolver when all external resolvers are unreachable).
    - `https://...` emits typed DoH (`type: "https"`, `server`, optional `server_port`, `path`,
      `detour: "dns-direct"`).
    - `tls://...` emits typed DoT (`type: "tls"`, `server`, optional `server_port`,
      `detour: "dns-direct"`).
    - `udp://...` or bare IP / `host[:port]` emits typed UDP (`type: "udp"`, `server`,
      optional `server_port` when non-53, `detour: "dns-direct"`).
    - Whenever a custom `local-dns` server specifies a hostname (non-IP literal), attach a
      `local-bootstrap` (`type: "local"`) server and set `local-dns.domain_resolver = "local-bootstrap"`
      so `sing-box 1.12+` avoids a circular `default_domain_resolver` dependency.
- `VPNRouter.Core/Services/CustomConfigInjector.cs`,
  `VPNRouter.Core/Services/CustomConfigInjector.Compat.cs`,
  `VPNRouter.Core/Services/CustomConfigInjector.Dns.cs`:
  - Forward `settings.Dns.LocalDns` into `ReplaceLocalDnsServers` and `EnsureLocalBootstrapDns`
    so custom sing-box configs also honor explicit `dns.local_dns` overrides while preserving
    exact default `https://8.8.8.8/dns-query` behavior when `LocalDns` is default (`"local"` / empty).
- `VPNRouter.Tests/LocalDnsSettingsTests.cs`:
  - Comprehensive unit tests covering default `"local"` retention, `https://` IP and hostname
    (with `local-bootstrap`), `tls://`, `udp://`, bare IPv4 (`77.88.8.8`), IPv4:port, IPv6,
    explicit `"system"`, invalid/malformed fallback, and `CustomConfigInjector` override behavior.

## Invariants

- Default behavior (`LocalDns = "local"` or blank) is 100% unchanged: `local-dns` stays
  encrypted DoH `8.8.8.8` via `dns-direct`, preserving all existing split-tunnel, anti-censorship,
  night DNS privacy, and custom-config test contracts.
- Every network-based `local-dns` server always uses `detour: "dns-direct"` (never routed back
  into TUN or proxy).
- `vpn-dns` remains routed via `proxy` with `domain_resolver: "local-dns"` when HTTPS.
- `DnsttUriParser` already validates arbitrary public IPv4 resolvers (e.g., `77.88.8.8`) and
  requires no code change (verified by inspection).

## Verification

- Workstation does not run local `dotnet build` / `dotnet test` per `docs/agent-contract.md`.
- Local checks: independent diff review, `git diff --check`, `gitleaks`.
- CI verification: GitHub Actions workflow on draft PR must run full `dotnet test` suite green.

## Unknowns & Observed Outcomes

- Initial state: `VPNRouter` worktree created from `origin/main` (`8e8e88e2`), branch
  `fix/rf-dns-local-resolver-20261125`.
- Outcome: pending implementation and GitHub Actions CI run.
