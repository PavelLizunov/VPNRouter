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
2. Tests: all 15 focused independent CategoryStore contracts pass.
3. Documentation: intent and matched metadata contract recorded before edits.
4. Review: independent receiver/edge diff review returned no findings.
5. UI/live: metadata auth/CAS and cross-session category flow pass.
6. Integration: current rules/routes/provisioning/reports preserved.

## Rollback

Restore prior receiver drop-in/source and edge config; retain new metadata file
and original signed policy snapshot. No database or client rollback required.

## Outcome

Receiver 40b3fc26 and edge bridge are live. Browser category create/rename/move/
delete and incognito persistence pass; stale 409, invalid-CSRF 403 and missing-
session 401 fail closed. Test metadata removed: revision 6, default categories,
no explicit assignments. Current comments cleared: main 8/33, phone 7/5, all
addresses/actions preserved. Site static release 192dd795 was activated by
Caddy reload without restarting vpnctld or rebuilding any binary. Final
fresh-page evidence and full recovery archives are retained privately under
corp-rule-categories-20261117. No build-triggering PR was opened under the
owner's explicit no-build instruction.

The independent 15-test contract suite found one revision-overflow status
ambiguity: implementation returned 409 for an attempted increment beyond the
safe integer limit, while the spec-derived boundary test expects 400. Clarify
that this malformed new revision is 400; preserve 409 for stale expected_revision.
Recorded before the narrow correction; all other 14 tests passed.
