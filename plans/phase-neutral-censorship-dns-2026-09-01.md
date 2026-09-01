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

- `ConfigGeneratorTests.FullTunnel_BypassRuAndBlockAds_PreservesSniffPrefix` — geo DNS uses `vpn-dns` and no country-specific server is emitted.
- `CustomConfigInjectorTests.Inject_WithBypassRussianTraffic_PassesSingBoxCheck` — geo DNS targets a proxy-detour server and legacy Yandex artifacts are absent.
- Add a deterministic custom-config regression that starts with the legacy injected server/rule and proves cleanup.

## Verification gate

- [ ] **Gate 1 — Scope/trust**: active Core and public docs contain no Yandex DNS endpoint or VPNRouter-owned country-specific resolver.
- [ ] **Gate 2 — DNS behavior**: generated and custom geo rules resolve through a real proxy outbound; explicit custom DNS and LAN exceptions remain intact.
- [ ] **Gate 3 — Build/tests**: focused DNS tests, Release build, and full discovered suite pass on the exact head.
- [ ] **Gate 4 — Documentation**: `README.md`, `README.ru.md`, readiness plan, prior consolidation plan, and this Outcome match runtime behavior.
- [ ] **Gate 5 — Independent review**: correctness, DNS/privacy, and test lenses leave no source-confirmed P0/P1.
- [ ] **Gate 6 — Integration**: exact-head GitHub `test`, `grep`, Windows Go, and characterization checks pass; UI verification is N/A because no UI changes.

## Outcome

Pending approved implementation and exact-head verification. Merge remains a separate owner decision.
