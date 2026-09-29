# H-30: record the PageScreenshot experiments and the emulator feasibility check

## Why

Two investigations of 2026-09-29 produced facts that are only in the session: the narrowed cause of the
six failing `PageScreenshotTests`, and whether the Windows worker can host Android emulators.

## What

- Ledger PAGESCREENSHOT-RENDER-INVALIDATION: the four experiments and the common denominator.
- Ledger: close four entries that are stale (a removed script, a removed source-text test, the removed
  `WgturnUpdater`, and the test-harness hermeticity entry that `TestEnvironmentSafety` already fixed).
- `docs/test-workers.md`: one dated observation about nested virtualization and the disabled Windows
  Hypervisor Platform feature.

## Verification

Only documentation changes; exact-head CI.

## Outcome

Pending CI.
