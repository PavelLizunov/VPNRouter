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

- Seven `CliGenerationStateCharacterizationTests` cover legacy reads, matching update/clear, stale-generation update and clear refusal, monotonic child replacement, malformed-state refusal, random exclusive temp creation, and concurrent read/write integrity.
- `ProcessOwnershipTests` cover generic exact snapshots and the independent v2 runtime-owner owner/child pair.
- Source guards pin both stop events before state publication, generation-qualified IPC, current-user/session scopes, exact identities, mutex/atomic write, and absence of unconditional cleanup.
- Windows characterization pins retained process handles and proves that an event created with .NET 10 current-user scoping remains discoverable by the legacy name-only client.

### Verification approach

Run focused CLI/process/state tests, all discovered tests, affected Release builds, Windows CLI publish/characterization, grep guard and repeated race tests. The control host has no local .NET/PowerShell and the registered workers have no .NET SDK, so GitHub Actions is the mechanical oracle. No worker may start or stop the real VPN.

## Verification gate

- [x] **Gate 1 — Build clean**: affected Release graph built and Windows CLI publish completed with zero errors in workflow `33642120623`; the untouched PoolAggregator-only remainder of `VPNRouter.sln` was not separately rebuilt.
- [x] **Gate 2 — Tests green**: baseline `2832 total / 2775 executed` became `2844 total / 2787 executed`, all passed with zero test warnings; Windows characterization passed `28/28` in workflow `33642120623`.
- [x] **Gate 3 — Docs**: this outcome and `plans/OPEN-DEFECTS.md` were updated; README/AGENTS remain unchanged because command syntax and public setup did not change.
- [x] **Gate 4 — Self-review**: four independent correctness/concurrency/security/test bug-hunt lanes plus two Opus reasoning lanes were lead-source-verified; all P0/P1 findings were fixed or refuted against the Windows-only target.
- [x] **Gate 5 — UI verify**: N/A — no UI surface changes.
- [x] **Gate 6 — Characterization diff**: N/A — not a god-file split; PR #211's exact-handle characterization remains green and the old-Stop/new-Start bridge gained a Windows test.

## Outcome

**Status**: READY FOR OWNER REVIEW — PR remains open and unmerged
**Commits**: `ff1b1ac9` brief; `f9462248` implementation; `bc057f65` adversarial race fixes; `0658b2c7` current-user named-handle scopes; `8a4eca55` legacy bridge characterization
**Pushed**: `origin/dsh/cli-run-generation`; PR #214 — https://github.com/PavelLizunov/VPNRouter/pull/214
**Test deltas**: +12 discovered and executed tests versus the brief-only baseline; `2844 total / 2787 executed / 2787 passed / 0 failed / 0 warning`
**Files changed**: Start/Stop/StateFile, AppPaths private-file mode seam, ProcessOwnership exact authority checks, linked state/context test configuration, three regression test files, this brief, and the defect ledger

**Gate results**: affected builds, Windows CLI publish, Ubuntu suite, Windows characterization, Go test and grep checks passed on implementation head `8a4eca55` and remained green through the outcome update; exact handoff links are maintained in PR #214.

**Surprises encountered**: reviewers found and the implementation fixed a lost restart/publication race, ambiguous owner-liveness fallback, unreadable-state deletion, fixed-temp link exposure, stale runtime authority, and old-Stop/new-Start compatibility. Search-first verdict: **Adopt** .NET 10 `NamedWaitHandleOptions`; it scopes events to the current user/session and the mutex to the current user across sessions without adding the disallowed package dependency. An unexplained workspace actor committed and pushed `bc057f65`; no content was lost, exact-head CI passed, and candidate incident `INC-1334` records it.

**Transition limitation**: a pre-generation CLI process already running before upgrade still contains its old unlocked callback and unconditional clear; new binaries cannot retroactively constrain that code. New Start accepts legacy Stop through a separately registered PID-only event, while destructive new Stop treats legacy state as status-only and refuses it.

**Follow-ups spawned**: the accepted `ConfigGenerator.cs` mechanical split remains a separate task/PR.
**Lessons for methodology doc**: persisted process authority needs generation, exact process identity, atomic conditional mutation, native-handle lifetime, and transition behavior reviewed as one protocol rather than isolated PID fixes.
