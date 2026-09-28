# Phase N - <Task name>

- Owner: <session or task owner>
- Branch: dsh/<task-slug>
- Base: <full commit SHA>
- Roadmap: <applicable section or N/A>
- Risk: <LOW / MEDIUM / HIGH, with reason>
- Blast radius: <files, consumers, runtime impact>
- Rollback: <task-specific revert or recovery>

## Why

<Problem and expected result.>

## What

<Files and behavior in scope, invariants, exclusions and material unknowns.>

## How

<Implementation sequence and dependencies. Reuse this record through delivery.>

## Verification

Record PASS, FAIL, BLOCKED or N/A with evidence and reasons. Do not pre-fill passes.
Use authorized, preflighted workers for builds and tests; never provision the control plane.

1. Build: Release solution build; Android changes also need the owning zone's APK build.
2. Tests: focused and full applicable suites, regressions or characterization baseline.
3. Docs: owning documentation updated and Outcome complete.
4. Review: applicable procedure, evidence and independence. `bug-hunt` requires a
   permitted model route; unavailable required independent review is BLOCKED,
   not a self-review pass. Justify N/A for a trivial no-behavior change.
5. UI: isolated `PageScreenshotTests`/`VisualDiffTests` when applicable. After an
   explicitly authorized ship, use `tools/post-ship-verify.ps1` on
   WINBRAT @ `100.115.182.0`; missing VM/WinRM is BLOCKED, with
   no developer-machine fallback.
6. Characterization: compare pre/post surfaces for mechanical splits; otherwise N/A.

## Outcome

- Status: <PASS / PARTIAL / BLOCKED>
- Changed files and delta: <observed result>
- Checks: <commands, snapshot, exit status and evidence>
- Review: <confirmed/rejected findings, fixes, remaining limitations>
- Git: <commits, task branch to origin, PR URL, exact-head check status>
- Follow-ups: <ledger references, remaining work and next step>
- Cleanup: <task-owned artifacts removed or intentionally retained>
