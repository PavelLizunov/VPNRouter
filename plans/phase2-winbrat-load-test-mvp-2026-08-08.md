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

- [x] **Gate 1 — Build clean**: solution and both new projects build with 0 errors.
- [x] **Gate 2 — Tests green**: new focused tests and existing suite pass.
- [x] **Gate 3 — Docs**: this brief Outcome and `OPEN-DEFECTS.md` are current.
- [x] **Gate 4 — Self-review**: Ponytail and manual security review; exact
  DeepSeek-in-Qwen review attempted with the prescribed read-only flags.
- [x] **Gate 5 — Remote brat UI verify**: N/A — no UI/product change; live
  workload acceptance is BLOCKED by the recorded provisioning dependency.
- [x] **Gate 6 — Characterization diff**: N/A — no product god-file split.

## Outcome (filled 2026-08-08)

**Status**: PARTIAL — the Phase-1 contract implementation is build- and
test-verified. Live GameUdp, BrowserBurst and Mixed acceptance remains BLOCKED
until the owner deploys the endpoint and the separate per-process measurement
gate proves the workloads use the tunnel; no remote load, VPNRouter
installation or configuration mutation occurred.

**Commits**: `24dc65b4`, `8b9a185a`, `e44021eb`, `5708367d`, `d168f073`

**Pushed**: `codex/winbrat-loadtest`; stacked draft PR
[#124](https://github.com/PavelLizunov/VPNRouter/pull/124) targets
`codex/winbrat-stability-harness`.

**Tests**: 22 focused load/stability contract tests passed; the scoped
pre-commit suite passed 185 tests and manual stacked-branch CI workflows are
green.

**Files changed**: endpoint/protocol/load-generator tooling, verifier,
coordinator, focused tests and tracked planning evidence; product code unchanged.

**Separate stability evidence**: the existing strict cold-cycle harness completed
10/10 cycles. This is lifecycle evidence only; it neither runs nor substitutes
for a load profile, which remains BLOCKED.

**Gate results:**

- [x] Gate 1: `VPNRouter.sln` Release build plus both new tooling projects pass
  with 0 errors (pre-existing solution warnings remain).
- [x] Gate 2: focused MVP and existing stability contracts pass 22/22; the
  scoped pre-commit suite passes 185/185. Manual stacked-branch dotnet-test and
  placeholder workflows are green for `d168f073`.
- [x] Gate 3: this brief and `OPEN-DEFECTS.md` record the endpoint block and
  worker outcome; no README/zone contract change is needed.
- [x] Gate 4: Ponytail full review retained only shared-framework primitives and
  fixed constants; manual security review verified environment-only secret use,
  HMAC/source/expiry/replay validation, capped UDP response, fixed rate limits,
  synchronized aggregate state, no target/config/log fields in coordinator
  evidence, no generic coordinator remoting, and an empty source-reviewed
  payload allowlist. Exact DeepSeek-in-Qwen review was attempted with the
  required read-only zero-tool flags; tool-budget attempts failed closed and a
  later stdin attempt timed out without output. No finding, PASS claim or
  permission change derives from that worker.
- [x] Gate 5: N/A — no product/UI change. Live workload acceptance is explicitly
  BLOCKED, not substituted with a public service.
- [x] Gate 6: N/A — no product god-file split.

**Surprises encountered**:

- The local shell did not expose the pinned SDK on `PATH`; the checkout's
  bundled .NET 10 SDK was used for verification without changing the machine.
- The current connection/lifecycle log schema has no process-to-outbound link.
  A route check alone is therefore insufficient and the verifier remains
  explicitly BLOCKED rather than inferring split-tunnel success.

**Follow-ups spawned**:

- Owner provisions the fixed endpoint. After that, add the separate
  measurement gate based on opaque run-token ingress, direct-control comparison
  and TUN on/off byte correlation before approving a source-built payload hash
  and enabling any live profile.
- Keep the current GameUdp profile as moderate game-like traffic. The known
  high-burst UDP boundary is measurement-gated after the owned-endpoint
  baseline; this MVP makes no capacity-stress claim and does not raise public
  HTTP limits.

## Live 30-minute evidence (2026-08-08)

### Scope boundary

Two bounded checks ran concurrently and must not be conflated:

- WINBRAT ran the lifecycle soak with one connected core, a TUN state sample
  and paired HTTP/UDP control probe every 15 seconds.
- The fixed GameUdp executable ran from the dev host against the owned public
  endpoint in six independent five-minute sessions. This validates the
  endpoint and public NAT path, but it is not yet process-attributed WINBRAT
  tunnel load.

### WINBRAT lifecycle soak

- 30-minute requested window; 83 paired samples were recorded.
- 81 samples were `HH` (HTTP and UDP healthy). Samples 19 and 29 were isolated
  `HNotU` UDP timeouts and the immediately following samples recovered.
- The initial and final boundary probes passed at 64, 512, 1200 and 1392-byte
  UDP sizes; HTTP stayed healthy through both isolated UDP events.
- The app kept exactly one core and an Up TUN. No crash, core restart or FATAL
  event was classified. Working set settled rather than growing: 66.2 MiB near
  minute 5, 64.7 MiB near minute 15 and 64.7 MiB near minute 25.
- Lifecycle sanitization reported one unknown error-level event. The harness
  therefore returned strict `FAIL`; it did not reinterpret that event as
  harmless. Cleanup passed with zero core processes and no TUN.

Evidence: `artifacts/brat-stability/20260808-182140-Soak.jsonl`.

### Owned GameUdp endpoint baseline

- Total: 36,366 sent, 34,627 received, 1,739 lost (4.7819%).
- Per-run losses: 882, 604, 249, 0, 0 and 4. The first half was materially
  worse; the last three sessions were clean or near-clean.
- Duplicate, reorder, corruption and unknown reply counts were all zero.
- Worst per-run RTT p99 was 409.9 ms; the worst acknowledged gap was 607.1 ms.
- After the run the endpoint remained active. Linux UDP counters showed zero
  `InErrors`, `RcvbufErrors` and `SndbufErrors`; the container interface showed
  zero RX/TX errors or drops and systemd reported zero service restarts.

Evidence: `artifacts/brat-loadtest/20260808-182139-30m-endpoint-baseline.json`.

### Decision

- Do not call this an end-to-end WINBRAT load PASS: the workload process was
  not yet approved and measured on WINBRAT.
- Do not tune server limits or VPNRouter MTU from this run. The loss is
  measurement-gated because it is concentrated before the container and the
  direct test used the home public hairpin/NAT path.
- Next useful test is the same fixed payload from WINBRAT with opaque egress
  attestation, followed by one independent non-hairpin control source. Keep the
  current strict lifecycle FAIL until the single unknown event is safely
  classified from redacted diagnostics.

## Post-outcome live enablement (2026-08-09)

GameUdp is no longer blocked: the exact source-built payload was approved,
Full Tunnel plus fixed-destination Tunnel routing and TUN-byte correlation were
proven on WINBRAT, and the live HY2/AWG matrix ran. BrowserBurst and Mixed remain
`MeasurementGated`; there is still no browser-process attribution and enabling
them would not explain the newly observed UDP-only gaps.

The detailed measurements and cleanup proof are in
`phase2-winbrat-protocol-load-matrix-2026-08-08.md`. The important result is not
"AWG fixes games": HY2 and AWG each reached the fixed three-second no-reply
failure threshold while core/TUN/lifecycle stayed healthy; their last completed
acknowledged gaps were 1.05 and 2.65 seconds. Independent dev-host endpoint
controls did not reproduce a multi-second pause. Attribution
remains measurement-gated pending server counters and another underlay.
