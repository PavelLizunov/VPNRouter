# Corp rule categories receiver

Owner requests shared editable rule categories and clearing all current notes,
with no compiled rebuilds. Keep metadata independent of signed policies and use
the existing interpreted receiver and exact browser auth/CSRF edge gate.

## Scope

Add categories.py, receiver dispatch and independent contract-derived tests.
No changes to client/engine, migrations, subscription authority or diagnostics
semantics. Request/response contract is retained in the matching vpnctl spec.

## Invariants and verification

Bounded strict JSON, safe identity, lock/CAS/atomic state; unknown/malformed input
fails closed, old reports untouched. Narrow Python tests and independent review
only; no .NET/Cargo builds or full suites. Archive complete service config and
old source before a single receiver restart; analyzer and vpnctld stay running.

## Six gates

1. Build: N/A, Python interpreted service; compilation forbidden by owner.
2. Tests: focused independent CategoryStore contracts pending.
3. Documentation: intent and matched metadata contract recorded before edits.
4. Review: independent diff review pending.
5. UI/live: metadata auth/CAS and shared category browser flow pending.
6. Integration: current rules/routes/provisioning/reports preserved.

## Rollback

Restore prior receiver drop-in/source and edge config; retain new metadata file
and original signed policy snapshot. No database or client rollback required.

## Outcome

Pending focused tests, review and live activation.

The independent 15-test contract suite found one revision-overflow status
ambiguity: implementation returned409 for an attempted increment beyond the
safe integer limit, while the spec-derived boundary test expects400. Clarify
that this malformed new revision is400; preserve409 for stale expected_revision.
Recorded before the narrow correction; all other14 tests passed.
