# Consolidate security PRs 162-168

## Why

PRs 162, 164, 165, 167, and 168 contain useful security and diagnostics fixes,
but their branches overlap or include unrelated generated/tooling files. Landing
them independently would create avoidable conflicts and preserve unrelated SDK
configuration churn.

## Scope

- Extend crash-report URI scrubbing with the union of the reviewed schemes.
- Export invalid and unloadable configuration backups through the diagnostics
  redactor.
- Validate WireGuard endpoint peers against the active subscription scope.
- Make unknown string scalars fail closed in diagnostics redaction.
- Preserve the existing SDK pin and omit `.jules` journal files.
- Reuse the tests and defect-ledger updates from the reviewed PRs.

## Verification gates

- Release solution build.
- Full VPNRouter.Tests suite.
- Focused diagnostics, crash reporter, and leak-protection tests.
- Independent bug-hunt review with all surviving findings resolved or recorded.
- Green GitHub PR checks before merge.

## Outcome

Pending implementation and verification.
