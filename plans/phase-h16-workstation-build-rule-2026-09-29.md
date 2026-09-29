# H-16: make the no-local-build rule and the worker load preflight explicit

## Why

During H-14 an agent installed .NET, JDK and the Android SDK on the development workstation
and ran a local Android build, reading "you can do the build" as permission. The rule already
existed in `docs/test-workers.md`, but the root `AGENTS.md` only linked to it, so it was easy
to skip.

## What

- Root `AGENTS.md`: new "Builds and SDKs" bullet under Task routing (the file is pinned to 25 lines; no SDK install or build on the
  workstation or `harness-test`; workers or CI only; load preflight first; missing SDK is a
  blocker; an ambiguous owner remark does not name the machine).
- `docs/test-workers.md`: preflight step 3 lists what to measure (CPU load and running build
  processes, RAM and swap, disk, SDK) and says to record the numbers and stop when headroom is
  unclear; the mutation limits forbid local substitutes for a worker.

## Verification

Existing `AgentContextContractTests` pins are unchanged; exact-head CI.

## Outcome

Pending CI.
