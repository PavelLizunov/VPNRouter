# Phase — CLI run-generation-bound stop

**Owner**: DSH session `session-b7bb95fc-bbc1-4f52-8dfd-3eea0fae24de`  
**Branch**: `dsh/cli-run-generation`  
**Accepted base**: PR #211 head `632f44d8daa0ab7ea8d7a0140c34255c526c9e79`  
**Roadmap ref**: matrix audit SU-3-1 adversarial follow-up / `plans/OPEN-DEFECTS.md`  
**Effort**: 1 day  
**Risk**: HIGH  
**Blast radius**: CLI persisted state, Windows named-event shutdown, exact fallback process kill, test source linkage; no GUI/service/package behavior  
**Rollback**: revert this task's commits; the additive state fields preserve the existing schema-1 reader contract

## Why

PR #211 pins the post-wait Windows child handle, but `state.json` still identifies a run only by reusable PIDs. A stale owner can overwrite or delete a replacement run's state, the PID-derived event can address a recycled owner, and Stop initially trusts the identity it observes now rather than the child identity published by Start. Bind every IPC and state mutation to one random run generation plus exact owner/child process identities.

## What

- Add an additive `RunGeneration` and flat owner/child start-tick/path fields to `RunState`; retain schema version 1 so old JSON remains readable.
- Create/register `VPNRouter_CLI_Stop_{ownerPid}_{generation:N}` before publishing state.
- Serialize every new state read/write/update/conditional-clear with one bounded cross-process named mutex; write through same-directory temp + flush + atomic move.
- Make `SingBoxStarted` update state only when its captured generation still matches.
- Make Stop reject legacy/default generation, validate the exact owner before signaling, use the latest same-generation child identity for fallback, and clear only that generation.
- Preserve PR #211's Windows SafeHandle pin through child identity comparison, kill and wait.
- Treat a replacement generation as untouched and return non-zero instead of reporting the whole VPN stopped.
- Exclude GUI/service behavior, Unix signaling, package changes, merge, release, tag, deploy and install.

## How

1. Expose a generic internal process-identity snapshot in `ProcessOwnership`; continue using the ownership-validating reader for sing-box.
2. Add generation-bound locked operations and internal path/mutex test seams to `StateFile` using only .NET/AppPaths primitives.
3. Reorder Start's CTS/event publication and replace read-modify-write/unconditional clear calls.
4. Refactor Stop around an immutable generation snapshot, exact owner event and latest same-generation child snapshot.
5. Link the state source/context into the existing cross-platform test assembly for direct temp-directory tests; retain source guards and controlled Windows child characterization.
6. Run independent Gemini implementation support, correctness/security/test reviews, lead source verification, exact-head CI and Windows characterization.

### Tests written

- `CliGenerationStateTests.LegacyState_RemainsReadable_ButCannotBeConditionallyCleared`.
- `CliGenerationStateTests.OldGeneration_UpdateCannotOverwriteReplacement`.
- `CliGenerationStateTests.OldGeneration_ClearCannotDeleteReplacement`.
- `CliGenerationStateTests.ConcurrentReadNeverObservesPartialJson`.
- Source guards pin event-before-state publication, generation-qualified IPC, persisted identity use, mutex/atomic write and absence of unconditional cleanup.
- Controlled Windows characterization pins exact owner/child identity and retained native handles without touching VPN processes.

### Verification approach

Run focused CLI/process/state tests, full discovered tests, Release solution build, Windows CLI publish/characterization, grep guard and repeated race tests. The control host has no local .NET/PowerShell, so GitHub Actions is the mechanical oracle. No worker may start or stop the real VPN.

## Verification gate

- [ ] **Gate 1 — Build clean**: Release solution and Windows CLI publish complete with zero errors.
- [ ] **Gate 2 — Tests green**: focused state/process tests, repeated race tests and all discovered tests pass.
- [ ] **Gate 3 — Docs**: Outcome and `plans/OPEN-DEFECTS.md` updated; README/AGENTS unchanged unless public behavior requires it.
- [ ] **Gate 4 — Self-review**: distinct correctness, security/process-isolation and test/concurrency reviews; every claim lead-source-verified.
- [ ] **Gate 5 — UI verify**: N/A — no UI surface changes.
- [ ] **Gate 6 — Characterization diff**: N/A — not a god-file split; PR #211 behavior stays covered by its exact-handle tests.

## Outcome (filled before final handoff)

**Status**: IN PROGRESS  
**Commits**: brief commit pending  
**Pushed**: pending  
**Test deltas**: pending  
**Files changed**: pending

**Gate results:** pending.

**Surprises encountered**: pending.  
**Follow-ups spawned**: the accepted `ConfigGenerator.cs` mechanical split remains a separate task/PR.  
**Lessons for methodology doc**: pending.
