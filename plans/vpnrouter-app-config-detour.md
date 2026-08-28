# VPNRouter app-config client detour

**Owner**: DSH session `session-9885869b-a27f-46b9-8feb-e561c76eba49`
**Branch**: `codex/vpnrouter-app-config-detour`
**Risk**: MEDIUM
**Blast radius**: subscription request/parser, server selection, sing-box outbound generation; no UI or credential format change
**Rollback**: revert the task commit; old clients remain compatible because the server capability gate withholds chained targets

## Trigger

A user refreshed the VPNRouter device subscription and could not see S5. The client consumes `/api/v1/app/config/{device_id}`, not the separate stock sing-box `/sub/{token}?format=sing-box` URL.

## Root cause

The compatibility endpoint intentionally omits chained targets from URI-only output. VPNRouter currently sends only `User-Agent: VPNRouter`, parses standard share-link fields, and has no model/generator contract for a target outbound's sing-box `detour`.

## What

- Send `X-VPNRouter-Capabilities: detour-v1` on subscription fetches.
- Parse capability-gated URI metadata `outbound=<opaque-id>` and `detour=<upstream-id>`.
- Persist the metadata on `VlessServerEntry`.
- When a chained target is selected, include its upstream entry and generate exactly two VLESS outbounds with `proxy.detour = chain-entry`.
- Fail closed if the referenced upstream is absent or unusable.
- Leave ordinary subscriptions and entries without detour byte/behavior compatible.
- Initial scope is VLESS+REALITY; chained XHTTP remains omitted.

## How

1. Add focused parser/request contract tests.
2. Extend `VlessServerEntry`, `VlessUriParser`, and `SubscriptionFetcher` using existing request-header and query parsing seams.
3. Extend `VlessConfig.GetActiveServers` and `ConfigGenerator.BuildOutbounds` with one explicit chain branch.
4. Add generated-JSON and missing-upstream tests, including sing-box check coverage.
5. Coordinate with the vpnctl PR that emits metadata only for the capability header.
6. Update README and Core/Test zone inventories; run independent correctness/security review.

## Verification gate

- [ ] Release solution build: `dotnet build VPNRouter.sln -c Release` — unavailable on the control-plane and provisioned workers; the changed Core plus tests built in Release CI with zero errors.
- [x] Focused parser/config tests pass as part of the full suite.
- [x] Full discovered test suite passes.
- [x] No capability/no detour output remains unchanged.
- [x] Missing upstream fails closed; no direct S5 fallback.
- [ ] Generated chain JSON passes bundled sing-box check — reserved for the authorized candidate/WINBRAT gate because CI has no bundled binary.
- [x] README/zone docs and this Outcome are current.
- [x] Independent correctness, compatibility, simplicity, and security reviews have no unresolved important findings.
- [x] PR CI is green; merge/release/WINBRAT installation waits for an explicit owner command.

## Outcome

Implemented on `codex/vpnrouter-app-config-detour` in PR #189, coordinated with vpnctl PR #187. The client advertises `detour-v1`, persists `OutboundId`/`DetourVia`, excludes chained targets from ordinary auto-select pools, and emits `proxy.detour=chain-entry` only for one exact direct VLESS upstream. Missing, duplicate, nested, unsupported, or platform-filtered chain members throw instead of falling back.

Delta: six Core files, focused regression tests, one xUnit serialization collection, README EN/RU, and Core/Test zone inventories. GitHub Actions Release-built the changed Core/test graph and ran 2,799 tests: 2,742 passed, 57 platform/UI skips, zero failures; Windows characterization and Go repair tests also passed. The coordinated vpnctl branch passed its full Rust, Docker SSH, gitleaks, deny, clippy, format, and project-map gates.

Independent reviews found and fixed ordinary-auto-select target leakage, nested-entry ambiguity, outbound ordering, static HTTP seam races, and platform-filtered target fallback. The first CI pass exposed one test-only false positive: a global JSON substring matched the existing DNS `detour`; the assertion now inspects only the `proxy` outbound and the rerun is green.

Remaining gates require explicit owner release authority: full release-solution packaging, bundled sing-box binary check, candidate installation, subscription refresh, and live S5-through-Iceland egress verification on WINBRAT. Rollback is `git revert` of the feature commits; an old client remains protected by vpnctl's capability gate.
