# Phase: Linux Headless runtime contract

Approval: direct owner `Approve`, 2026-09-19, for the complete Micro-Spec below.
Task branch: dsh/omarchy-plugin-2026-09-17; accepted backend base64e1e805.
Why: readiness, feature probing and launch currently choose different binaries;
file presence does not establish provenance or authority to elevate code.
Risk: HIGH (process execution/trust boundaries and shared Core consumers).
Rollback: revert this task's implementation commit on task branch; no deployed
state exists to revert. No merge, release or deployment authority.

## 1. Intent & Invariants

Add a Headless-specific runtime policy identifying the selected sing-box and
allowed operations. Use one runtime for readiness, feature capabilities,
start/restart, related Headless checks and diagnostics. Never inspect one binary
and launch another. Headless cannot implicitly elevate via pkexec/sudo or copy a
binary to the data directory to bypass missing provisioning. No PATH search,
automatic download or fallback runtime. Policy comes from an internal interface,
not QML/NDJSON executable paths. Missing approved runtime/prerequisites returns
unavailable before engine launch. Presence, version and build tags are not trust.
Preserve default App/CLI/Windows/macOS/Android behavior. Source and isolated tests
only; no helper, Polkit policy, setcap, install/activation/network mutation.

## 2. Interface / Data Contract

Internal selection -> file identity -> operation authorization or typed refusal.
Distinguish missing runtime, untrusted runtime, changed file and unavailable
launch prerequisites with safe errors. Changed runtime invalidates earlier
verification. Internal test injection only; no production environment overrides.
NDJSON v1 unchanged; Linux killSwitch/dnsLockdown remain false. Do not silently
choose a production artifact, distribution trust root or privileged broker.
Default Linux Headless stays unavailable until those prerequisites are approved.
Test fixtures cannot serve as release provenance evidence. Path identity checks
must not be advertised as race-free executable binding unless that is proven.

## 3. Verification Checklist

- Enumerate reachable Headless runtime consumers and route through one policy.
- Missing/untrusted/replaced runtime rejected before process creation.
- No implicit bundle/data/PATH fallback, copying, downloads or elevation.
- Start/restart and diagnostics agree on selected identity.
- Other client defaults preserved by regression tests.
- Isolated exact-snapshot worker tests, independent Gemini review, task commit,
  immediate push, exact-SHA canonical PowerShell verifier.
- Documentation distinguishes implemented contract from deployment/live readiness.

## How and scope

Core runtime/process/lifecycle/feature seams and directly reachable verifier and
diagnostic consumers; Headless wiring/operations; Core and Headless tests;
component documentation. Inspect closest AGENTS before edits. No global mutable
path override or boolean feature override used as production policy. Runtime
context, if needed for static parsers, must be scoped across async operations,
restore on disposal and not leak into legacy callers; deferred work must retain
its policy. All process launch/restart sites must recheck policy at use.

## Six gates / Outcome

1. Build: PENDING solution Release on authorized worker.
2. Tests: PENDING focused red/green, Headless groups, audited Core subset and CI;
   full live-worker Core suite remains safety-gated, not silently waived.
3. Docs: brief approved; owning README and consumer inventory pending.
4. Review: PENDING independent correctness/test/security review and verification.
5. UI/remote: no UI edit or deployment; isolated worker checks only, live gate N/A.
6. Integration: PENDING default-behavior regression and reachable-consumer proof.

This brief records approval, not completed implementation or green acceptance.
