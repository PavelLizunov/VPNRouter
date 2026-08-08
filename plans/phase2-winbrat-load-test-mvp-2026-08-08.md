# Phase 2 — WINBRAT operator-owned load-test MVP

**Owner**: Codex implementation agent  
**Branch**: `codex/winbrat-loadtest` (stacked on `codex/winbrat-stability-harness`)  
**Roadmap ref**: `plans/phase2-winbrat-stability-harness-2026-08-08.md` “Load and browser-game follow-up decision”  
**Effort**: 4–6 hours  
**Risk**: MEDIUM — UDP authentication and evidence boundaries are security-sensitive; all live traffic remains disabled until the operator endpoint exists  
**Blast radius**: new .NET 10 tooling projects, `tools/brat-verify.ps1`, one coordinator, static contracts and plans; no product/runtime code  
**Rollback**: revert task commits or delete this stacked branch

## Why

The stability harness verifies lifecycle and low-rate probes but deliberately does
not exercise an owned browser/UDP workload. This MVP supplies the smallest
bounded, reproducible workload and an endpoint that cannot become a generic
reflector. It preserves split-tunnel safety by refusing live assertions until the
fixed owned destination is proven to use the tunnel.

## What

- Add a shared-framework-only endpoint with fixed health/blob/browser responses,
  WebSocket echo and source-bound authenticated UDP echo.
- Add a fixed-profile load generator for GameUdp metrics and a browser page for
  BrowserBurst; all metrics and evidence are aggregate-only.
- Extend the existing verifier with fixed-profile route proof and a fail-closed
  load-test action. Add a coordinator that delegates every remote operation to
  that verifier and never changes VPNRouter configuration or selected apps.
- Add focused tests for cookie auth, expiry/replay, rate limits, response-size
  caps, UDP metric classification, coordinator isolation and evidence redaction.

## How

1. Define fixed target/profile/evidence constants and testable standard-library
   UDP primitives.
2. Host fixed HTTP, browser and WebSocket routes plus bounded UDP echo.
3. Implement the fixed GameUdp client metric accumulator and static contracts.
4. Add verifier/coordinator delegation with `BLOCKED` when the endpoint or
   tunnel proof is absent; do not execute remote actions in this task.
5. Run build/tests, diff checks, security and minimality reviews, then commit,
   push and open a stacked draft PR.

### Tests written

- `LoadTargetContractTests.Cookie_*` — source binding, expiry/replay and
  anti-amplification behavior.
- `LoadTargetContractTests.RateLimit_*` — fixed per-source/global limits.
- `GameUdpMetricsTests.Observe_*` — loss, duplicate, reorder, RTT and
  sent-but-unanswered gap classification.
- `BratLoadTestToolingContractTests.*` — fixed profile, no configuration
  mutation, verifier-only remote delegation and evidence allowlist.

### Verification approach

The new projects and their tests run locally without contacting the endpoint.
Source contracts enforce the narrow remote/evidence boundary. Live acceptance is
explicitly BLOCKED until external provisioning and route proof exist; no public
site, STUN host or local VPNRouter execution is used as a substitute.

## Verification gate

- [ ] **Gate 1 — Build clean**: solution and both new projects build with 0 errors.
- [ ] **Gate 2 — Tests green**: new focused tests and existing suite pass.
- [ ] **Gate 3 — Docs**: this brief Outcome and `OPEN-DEFECTS.md` are current.
- [ ] **Gate 4 — Self-review**: Ponytail and manual security review; exact
  DeepSeek-in-Qwen review attempted with the prescribed read-only flags.
- [x] **Gate 5 — Remote brat UI verify**: N/A — no UI/product change; live
  workload acceptance is BLOCKED by the recorded provisioning dependency.
- [x] **Gate 6 — Characterization diff**: N/A — no product god-file split.

## Outcome (filled after implementation)

**Status**: IN PROGRESS
