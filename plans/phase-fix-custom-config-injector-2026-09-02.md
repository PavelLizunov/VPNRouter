# Phase — CustomConfigInjector Full-Tunnel Direct Rule & DNS Hijack Safety

**Owner**: DSH session `session-b7bb95fc-bbc1-4f52-8dfd-3eea0fae24de`
**Branch**: `dsh/fix-custom-config-injector`
**Accepted base**: `origin/main` head `503f5b51`
**Roadmap ref**: matrix audit Category 1.4 / findings `SEC-01` & `SEC-02`
**Effort**: 1 day
**Risk**: LOW to MEDIUM (targeted injection rule validation in `CustomConfigInjector.cs`)
**Blast radius**: `VPNRouter.Core/Services/CustomConfigInjector.cs` route and DNS rule injection; unit tests in `VPNRouter.Tests`
**Rollback**: revert branch commits; restore prior custom config injection behavior

## Why

Auditing Category 1.4 identified two critical leak vectors in custom configuration injection:
1. `SEC-01`: In full-tunnel mode (`isFullTunnel == true`), `CustomConfigInjector.cs:148` skips `InjectRouteRules`. While line 198 sets `route.final = proxyTag`, existing user rules in `route.rules` with `outbound: "direct"` (or `action: "route", outbound: "direct"`) are preserved intact. Because sing-box evaluates `route.rules` sequentially from top to bottom (first-match-wins) before evaluating `route.final`, any traffic matching a pre-existing direct rule is routed outside the tunnel, creating an uninspected plaintext leak in full-tunnel mode.
2. `SEC-02`: `CustomConfigInjector` does not ensure that an `action: "hijack-dns"` rule exists in `route.rules`. If a custom configuration omits this rule, incoming UDP/TCP port 53 packets captured by the TUN interface bypass the sing-box encrypted DNS module entirely. Port 53 queries to private gateway IPs (e.g. `192.168.1.1:53`) match `ip_is_private -> direct` and leak to the local ISP in plaintext.

## What

- In `VPNRouter.Core/Services/CustomConfigInjector.cs`:
  - Implement `EnsureDnsHijackRule`: if `route.rules` lacks `action: "hijack-dns"` or `outbound: "dns-out"`, inject `{ "protocol": "dns", "action": "hijack-dns" }` immediately after any `sniff` rule and before private-IP/custom rules.
  - In full-tunnel mode (`isFullTunnel == true`), sanitize `route.rules` to remove user direct routing rules that shadow `route.final`, preserving RFC1918 private IP bypass and Russian geo bypass (`vpnrouter-geosite-ru`).
- In `VPNRouter.Tests/CustomConfigInjectorTests.cs`:
  - Add unit test verifying that `EnsureDnsHijackRule` inserts `hijack-dns` when missing from custom JSON.
  - Add unit test verifying that in full-tunnel mode, non-private direct rules are sanitized and cannot shadow `route.final = proxy`.

## How

1. Commit approved phase brief and verify baseline CI on `origin/main`.
2. Implement `EnsureDnsHijackRule` and full-tunnel direct rule sanitization in `CustomConfigInjector.cs`.
3. Add covering unit tests in `VPNRouter.Tests/CustomConfigInjectorTests.cs`.
4. Run independent adversarial review via `opus-swarm`.
5. Verify clean build and all test suites on Ubuntu and Windows in GitHub Actions.

### Tests written

- `Inject_MissingDnsHijackRule_InjectsHijackDnsAfterSniff`
- `Inject_FullTunnel_SanitizesUserDirectRules_PreventingBypass`

### Verification approach

Run focused `CustomConfigInjector` unit tests and full test suites on Ubuntu and Windows. GitHub Actions is the mechanical oracle.

## Verification gate

- [ ] **Gate 1 — Build clean**: Release solution build and Windows CLI publish complete with zero errors.
- [ ] **Gate 2 — Tests green**: all unit and characterization tests pass with zero failures.
- [ ] **Gate 3 — Docs**: outcome recorded with commit SHAs and test counts; `plans/` updated.
- [ ] **Gate 4 — Self-review**: adversarial review confirms fail-closed routing and DNS hijack safety.
- [ ] **Gate 5 — UI verify**: N/A (Core injector changes; UI surface untouched).
- [ ] **Gate 6 — Characterization diff**: existing custom config injection tests continue to pass.

## Outcome

**Status**: IN PROGRESS
**Commits**: brief commit pending
**Pushed**: pending
**Test deltas**: pending
**Files changed**: pending

**Gate results**: pending.
**Surprises encountered**: pending.
**Follow-ups spawned**: pending.
**Lessons for methodology doc**: pending.
