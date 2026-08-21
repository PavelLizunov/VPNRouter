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

Implemented the reviewed union of PRs 162, 164, 165, 167, and 168. The
consolidation excludes the unrelated `global.json` downgrade and `.jules`
journals, bounds backup export to the five newest files, and adds fail-closed
coverage for null outbound collections.

- `dotnet build VPNRouter.sln -c Release`: PASS, 0 errors.
- Full `VPNRouter.Tests`: PASS, 2833 passed, 4 skipped.
- Scoped pre-commit suite: PASS, 191 passed.
- Ox Alpha bug-hunt: four minor findings found and fixed (bounded backups,
  null-outbound guard, current defect anchor, and missing branch coverage).
- GitHub checks on the current-main merge result: PASS (`test`, `grep`,
  `go-test-windows`, `characterization-windows`).
