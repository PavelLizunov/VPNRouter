# Phase 2 - Safe desktop version rollback

**Owner**: Codex session 2026-08-15
**Branch**: `codex/version-rollback-ui`
**Roadmap ref**: user-requested update recovery follow-up after v2.49.3
**Effort**: 1 day
**Risk**: HIGH
**Blast radius**: desktop update discovery, installer receipt, Settings > Updates UI
**Rollback**: revert the implementation commit and delete the task branch

## Why

A bad stable release can prevent VPN startup before the user has a practical way
to recover. The existing updater only discovers versions newer than the running
build, so recovery requires manually finding and installing an older archive.
The application should expose a small, verified rollback path without creating a
general-purpose package manager or weakening the existing update trust boundary.

## What

- List the current and three most recent stable desktop releases in Settings >
  Updates.
- Mark the running version as installed and allow selecting only an older stable
  release that has an exact platform asset and valid published SHA-256 sidecar.
- Reuse the existing download, checksum, snapshot, apply and restart pipeline.
- Before a downgrade, preserve a timestamped private copy of `config.yaml`.
- Suppress the legacy forward-update receipt for a downgrade because old clients
  interpret an equal/older receipt as a failed forward update.
- Keep Android and component-level Zapret/TgProxy/sing-box rollback out of scope.

## How

1. Extend the update-source contract with a bounded stable-release catalogue.
2. Refactor GitHub release parsing so normal update discovery and history use the
   same asset/SHA selection and semver rules.
3. Add history state, selection, confirmation and apply commands to
   `UpdateNotificationViewModel`.
4. Add a compact inline history panel to Settings > Updates with RU/EN strings.
5. Add regression tests for filtering, checksum fail-closed behavior, selection,
   config backup and downgrade receipt compatibility.

### Tests written

- `IUpdateSourceContractTests` - stable ordering, bounded results and strict
  platform asset/SHA filtering.
- `UpdateNotificationViewModelTests` - load/select/cancel/confirm rollback flow.
- `UpdateChecker*Tests` - downgrade backup and receipt compatibility.
- Headless Settings screenshot/binding coverage at the supported narrow width.

### Verification approach

Focused updater and headless UI tests first, then Release solution build and the
full discovered suite. Run adversarial `bug-hunt` after implementation. UI
verification is performed only on fixed WINBRAT after a candidate is explicitly
authorized and shipped; local headless screenshots cover this PR.

## Risk

**HIGH** because this changes executable installation behavior. Mitigations are a
strict recent-stable limit, mandatory SHA-256, exact platform asset selection,
confirmation, config backup, existing binary snapshot rollback and no component
mix-and-match.

## Verification gate

- [x] **Gate 1 - Build clean**: `dotnet build VPNRouter.sln -c Release` has 0 errors.
- [x] **Gate 2 - Tests green**: focused updater/UI tests and full suite pass.
- [x] **Gate 3 - Docs**: README EN/RU and this Outcome describe the recovery path.
- [x] **Gate 4 - Self-review**: Qwen design review plus `bug-hunt` correctness,
  tests and security lenses have no surviving P0/P1.
- [x] **Gate 5 - UI verify**: headless RU narrow screenshot passes; WINBRAT is
  mandatory after an explicitly authorized candidate ship.
- [x] **Gate 6 - Characterization**: N/A, this is not a mechanical god-file split.

## Outcome

**Status**: READY FOR PR CI
**Commits**: plan `6388505`; implementation commit follows this outcome
**Test deltas**: 30 focused updater/UI tests; full suite 2829 passed, 4 platform skips
**Files changed**: update source/installer, update VM/UI/localization, tests and EN/RU docs

**Gate results:** Release build 0 errors; 30/30 focused tests; 2829/2829
executed full-suite tests passed; RU 400 px rollback confirmation screenshot inspected;
bug-hunt P0/P1 survivors: none.

**Surprises encountered**:

- Existing forward-update receipts intentionally store the pre-update version;
  an older target would interpret that receipt as a failed update.
- Settings schema is forward-migrated but older builds ignore unknown YAML keys,
  so an explicit pre-downgrade config backup is required.
- Bug-hunt found and closed metadata swapping during an async download, mismatched
  tag/asset acceptance, mislabeled `-rN` candidates, stale receipts and frozen
  history localization before commit.

**Follow-ups spawned**:

- A remotely maintained unsafe-version blocklist is deferred until a real revoked
  release exists; the recent-three stable bound is sufficient for the first cut.
