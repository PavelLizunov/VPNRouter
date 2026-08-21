# Phase 2 - Port useful changes from stale PRs 96, 103, and 118

## Why

The three draft branches are far behind `main` and cannot be merged safely, but
they contain compact changes that remain useful: two pinned sing-box backports,
clear Serilog sink ownership, complete removal of a live test-subscription URL
from tracked evidence, and a fail-closed UIA `ScrollIntoView` operation.

## Scope

- Reapply the immutable sing-tun NAT and DNS single-flight backports to both
  sing-box-lx build scripts with contract tests.
- Move Serilog sink packages to executable composition roots.
- Replace every tracked occurrence of the test-subscription URL with a neutral
  placeholder; credential rotation remains an owner-side action.
- Add `ScrollIntoView` to the current WINBRAT verifier and synchronize both
  post-ship skill copies.
- Do not import stale audit findings, superseded verifier code, or historical
  refactor-backlog churn.

## Verification gates

- Release solution build and full test suite.
- Focused build-script, verifier-contract, and diagnostics/security tests.
- Independent Ox Alpha bug-hunt with survivors fixed or recorded.
- Green GitHub checks on the current `main` merge result.

## Outcome

Implemented on the consolidation branch for PR #170.

- Both sing-box-lx build scripts fetch and apply the two immutable upstream
  backports in one fail-closed cherry-pick sequence, with source-contract tests.
- Serilog sinks now belong to the executable composition roots that use them;
  Ox Alpha independently checked every logger construction site and reported no
  findings.
- Tracked test-subscription URLs and partial URL fragments were replaced with a
  reserved neutral placeholder. Provider-side rotation remains recorded as an
  open P1 follow-up because a normal PR cannot revoke data from Git history.
- WINBRAT UIA gained semantic `ScrollIntoView`, including off-screen target
  acquisition, a visible-state postcondition, synchronized skill docs, and
  contract coverage.
- `dotnet build VPNRouter.sln -c Release`: 0 warnings, 0 errors.
- Full test suite: 2851 passed, 4 skipped, 0 failed (2855 total).
- Focused backport/verifier contracts: 7 passed, 0 failed.
- Three Ox Alpha review lenses found no P0 blockers. Reported P2 robustness
  issues were fixed; the provider-side credential action was recorded in
  `plans/OPEN-DEFECTS.md`.
- After merging dependency PR #159, the full solution gate caught that its
  Spectre.Console 0.57.2 / Spectre.Console.Cli 0.55.0 pair breaks all existing
  CLI command overrides. Only that incompatible pair was restored to 0.49.1;
  the remaining green dependency updates were retained.
