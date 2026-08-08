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

## Outcome (filled 2026-08-08)

**Status**: PARTIAL — the Phase-1 contract implementation is build- and
focused-test verified. Live GameUdp, BrowserBurst and Mixed acceptance remains
BLOCKED until the recorded owner provisioning and tunnel-proof dependency is
met; no remote load, VPNRouter installation or configuration mutation occurred.

**Commits**: implementation commit pending

**Test deltas**: +8 focused MVP contract/unit tests (13 focused load/stability
tooling tests total).

**Files changed**: endpoint/protocol/load-generator tooling, verifier,
coordinator, focused tests and tracked planning evidence; product code unchanged.

**Gate results:**

- [x] Gate 1: `VPNRouter.sln` Release build plus both new tooling projects pass
  with 0 errors (pre-existing solution warnings remain).
- [x] Gate 2: the focused MVP and existing stability contracts pass 13/13.
  The attempted non-elevated full local suite reached the known
  `%ProgramData%` ACL failures already recorded in `OPEN-DEFECTS.md`, then was
  stopped after it held the test host; it is not attributed to this change.
  The stacked-branch preflight full suite and placeholder workflow were green
  before implementation; post-push CI is required for the changed revision.
- [x] Gate 3: this brief and `OPEN-DEFECTS.md` record the endpoint block and
  worker outcome; no README/zone contract change is needed.
- [x] Gate 4: Ponytail full review retained only shared-framework primitives and
  fixed constants; manual security review verified environment-only secret use,
  HMAC/source/expiry/replay validation, capped UDP response, fixed rate limits,
  no target/config/log fields in coordinator evidence and no generic coordinator
  remoting. Exact DeepSeek-in-Qwen review was attempted with the required
  read-only zero-tool flags and failed closed without a finding.
- [x] Gate 5: N/A — no product/UI change. Live workload acceptance is explicitly
  BLOCKED, not substituted with a public service.
- [x] Gate 6: N/A — no product god-file split.

**Surprises encountered**:

- The local shell did not expose the pinned SDK on `PATH`; the checkout's
  bundled .NET 10 SDK was used for verification without changing the machine.
- The known non-elevated full-suite ACL limitation also leaves a child test host
  alive after failures, so it was stopped before the focused re-run.

**Follow-ups spawned**:

- Owner provisions the fixed endpoint and a fixed remote browser/load-generator
  payload, then proves both workloads route through `VPNRouter-TUN` before
  enabling live acceptance.
