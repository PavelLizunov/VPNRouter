# Phase — neutral censorship-resistant DNS correction

**Owner**: DSH session `session-b7bb95fc-bbc1-4f52-8dfd-3eea0fae24de`
**Branch**: `dsh/remove-censored-dns`
**Roadmap ref**: approved correction to PR #201 DNS design
**Effort**: 1–2 hours
**Risk**: HIGH
**Blast radius**: generated/custom sing-box DNS policy, regression tests, bilingual DNS documentation
**Rollback**: revert the implementation commit; no binary, release, tag, deployment, or installation action

## Why

PR #201 encrypted the RU-bypass resolver but chose Yandex DoH. Encryption protects the transport from ISP spoofing; it does not make a resolver governed by a censoring jurisdiction trustworthy. VPNRouter must not assign country-specific resolvers to censorship or geo rules.

## What

- Remove the VPNRouter-owned `vpnrouter-dns-ru` Yandex server from generated configs.
- Route generated geo/censorship DNS through the existing proxy-detoured `vpn-dns`.
- In custom configs, remove legacy `vpnrouter-dns-ru` server/rules and route geo DNS through an existing proxy-detour resolver or synthesized Cloudflare DoH via the selected proxy.
- Preserve explicit custom DNS, LAN/system exceptions, direct/smart behavior, bootstrap loop avoidance, route rules, and user-enabled RU traffic bypass.
- Correct active English/Russian documentation and readiness notes; do not rewrite historical evidence snapshots.

## How

1. Reuse `vpn-dns`, `FindRemoteDnsTag`, and `EnsureSynthesizedRemoteDns`; add no provider abstraction or dependency.
2. Make legacy Yandex cleanup idempotent before geo-rule injection.
3. Update generated/custom regression tests to require proxy-detour DNS and forbid the old tag/server.
4. Run focused and full CI, then independent DNS/correctness/security review.

### Tests written

- `ConfigGeneratorTests.GeoBypass_DnsUsesTunnelResolverWithoutCountrySpecificServer` — generated geo DNS uses `vpn-dns` and emits no country-specific server.
- `CustomConfigInjectorTests.Inject_GeoBypass_RemovesLegacyCountryDnsAndUsesProxyDns` — existing proxy DNS is reused, missing proxy DNS is synthesized, and legacy injected artifacts are removed.
- `CustomConfigInjectorTests.Inject_WithBypassRussianTraffic_PassesSingBoxCheck` — optional local sing-box integration keeps the geo rule on a proxy-detour resolver.

## Verification gate

- [x] **Gate 1 — Scope/trust**: active Core and public docs contain no Yandex DNS endpoint or VPNRouter-owned country-specific resolver.
- [x] **Gate 2 — DNS behavior**: generated and custom geo rules resolve through a real proxy outbound; explicit custom DNS and LAN exceptions remain intact.
- [x] **Gate 3 — Build/tests**: focused DNS regressions, Release build, and full discovered suite pass on the implementation head.
- [x] **Gate 4 — Documentation**: `README.md`, `README.ru.md`, readiness plan, prior consolidation plan, and this Outcome match runtime behavior.
- [x] **Gate 5 — Independent review**: correctness, DNS/privacy, and test lenses leave no source-confirmed P0/P1.
- [x] **Gate 6 — Integration**: implementation-head GitHub `test`, `grep`, Windows Go, and characterization checks pass; UI verification is N/A because the ViewModel change is comment-only.

## Outcome

Implemented in PR #202 at code head `00aa0ca87d1170ea5215e33e438a2bbe394e423e`. Generated geo/censorship DNS now reuses proxy-detoured `vpn-dns`. Custom injection removes the legacy server, rules, and stale `dns.final` before per-app selection; reuses a real proxy-detour resolver or synthesizes Cloudflare DoH through the selected proxy; creates safe DNS state when the source omitted `dns`; and gives geo policy priority over process-specific direct DNS. Explicit custom resolvers, LAN/system exceptions, bootstrap loop avoidance, direct/smart behavior, and RU traffic routing remain in place. Historical evidence snapshots were not changed.

`git diff --check` and active Core/App/public-doc endpoint scans passed. Four independent bug-hunt lanes found two P1 custom-config edge cases (missing initial `dns` and stale legacy `dns.final`); both were fixed and two focused rechecks were clean. GitHub Actions on the implementation head passed `test` (2,830 total: 2,773 passed, 57 platform/UI skips), `characterization-windows` (19/19), `go-test-windows`, and `grep`. The control plane has no .NET SDK or PowerShell, so GitHub Actions is the build/test oracle. This outcome-only commit must pass the same exact-head checks before merge.

Rollback is a revert of `00aa0ca87d1170ea5215e33e438a2bbe394e423e`. No binary bump, release, tag, deployment, installation, merge, or stable cut was performed; merge remains a separate owner decision.
