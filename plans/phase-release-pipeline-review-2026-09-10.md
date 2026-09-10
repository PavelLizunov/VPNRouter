# Release pipeline review and repairs

- Owner: current DSH session; maintainer approved Micro-Spec on 2026-09-10.
- Branch: `dsh/release-pipeline-review-20260910`.
- Accepted base: `43138f7c36e8dbe42aa2eb47d8e2216eb120707f`, confirmed against GitHub main.
- Risk: HIGH (release provenance, distribution channels and signing).
- Blast radius: release scripts, workflows, release instructions and regression tests; no intended application behavior change.
- Rollback: revert only task commits through reviewed PR; never rewrite published tags.

## Why

Previous documentation-only inspection incorrectly claimed release readiness. Source reinspection found unconditional Latest publication for candidates, insufficient explicit tag-to-SHA binding and contradictory draft/prepublication gates. Review the complete release path and repair confirmed defects without performing a release.

## What (approved Micro-Spec)

Audit unsigned/signed rolling candidates and stable promotion: version/SHA, builds, signing, artifact upload, workflow triggers, CI provenance, exact 16 assets and hashes, post-ship/updater gates, cleanup and recovery. Register source-confirmed findings in `plans/OPEN-DEFECTS.md` before implementation. Preserve interfaces unless safety requires changes; use existing platform features and dependencies.

Invariants: candidates never become stable/Latest; source, tag and binaries match the accepted SHA/version; published tags remain immutable; absent/failed checks, signatures or artifacts cannot count as success. No silent signing downgrade. Preserve unrelated user files.

No release publication, remote release tags, merge, deployment, VPN/UI scenario, credentials modification or infrastructure provisioning is authorized. Task-branch commits/pushes and a PR are within scope.

## How

1. Record baseline and independent read-only workflow/verification reviews.
2. Push this brief and wait for exact-head PR checks before implementation.
3. Confirm findings, record their evidence and implement minimal repairs plus regressions.
4. Reconcile skills/runbooks with executable behavior and review both owning READMEs.
5. Run isolated tests with publication commands mocked, PowerShell parse checks, contract tests and full build/test gates on authorized preflighted workers or applicable CI.
6. Independently review correctness/security, fix survivors, record observed outcomes and publish task PR evidence. External release-only gates remain explicitly unexecuted.

## Tests planned

Candidate/stable channel selection; wrong version/SHA/tag; malformed/missing/extra artifacts; hash/signature failure; partial publication and retry; exact workflow identity and failed/skipped/missing CI. Tests must avoid real releases, remote tags and deployments.

## Six gates

- Gate 1 — Build: PARTIAL, CI Release test graph build zero errors and Windows package build passed; full solution command not run, exact local SDK unavailable.
- Gate 2 — Tests: PASS for applicable CI selection: 154 Windows contracts; 2993 Ubuntu tests passed, 57 skipped; updater and Go passed. Unfiltered visual suite not run.
- Gate 3 — Documentation: PASS, release skills/runbooks and both README sections reconciled; ledger preserves external acceptance follow-ups.
- Gate 4 — Independent review: PASS within source scope, separate correctness/security/test reviewers; confirmed integration defects corrected, corrective patch independently reviewed.
- Gate 5 — UI/remote release: N/A for current tooling-only implementation; real WINBRAT post-ship and release publication require separate authorization and are not claimed tested.
- Gate 6 — Integration: PASS for isolated gate fixtures and Windows packaged updater; live release/signing/distribution deliberately unexecuted. Mechanical split characterization N/A.

## Baseline evidence

GitHub main matched base SHA. Paginated check-runs query returned successful test, characterization-windows, go-test-windows, grep and aggregate checks, no running/red entries. `.git-suggested-hash-bump.txt` absent. PowerShell is not present on control-plane PATH; methodology permits exact PR checks as remote substitute. Unrelated untracked `.dsh/performance-autoresearch/` and `vpn_issue_report.md` remain untouched.

## Outcome

PARTIAL acceptance / implementation ready for owner review at PR #255. Brief `5f3d37e2`, implementation `c7e3043b`, corrective green snapshot `26b0bdb8`; all pushed only to task branch. First CI exposed two stale assertions and an existing installer PowerShell5.1 encoding defect; corrected without disabling tests. No merge/tag/release/deployment performed.

Primary outputs: draft-only exact-SHA staging and no unsigned fallback under SignPath configuration; fail-closed platform prerequisites; canonical current CI evidence with pagination; all-asset integrity before parsing and fixed asset identity; disabled legacy Android signing; validated stable-only APT; safer WINBRAT ownership/cleanup and soak oracle; source-aligned runbooks and regression fixtures.

Detailed checks, independent review, environment preflights, remaining owner-authorized gates and rollback context: `plans/release-pipeline-review-evidence-2026-09-10.md`. Full solution/visual gate and live release checks remain explicit limitations, not silently passed requirements. Rollback uses reviewed revert of task commits only; never published-tag rewrite.
