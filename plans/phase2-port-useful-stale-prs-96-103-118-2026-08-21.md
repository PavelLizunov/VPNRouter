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

Pending implementation and verification.
