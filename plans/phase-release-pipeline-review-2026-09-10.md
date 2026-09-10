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

- Gate 1 — Build: PENDING, Release solution build on authorized exact-SHA worker/CI.
- Gate 2 — Tests: PENDING, focused release contracts, isolated behavior regressions, full discovered suite and applicable PowerShell syntax checks.
- Gate 3 — Documentation: PENDING, release skills/runbooks and README review; ledger and final outcome.
- Gate 4 — Independent review: IN PROGRESS, distinct workflow and verifier reviewers; final bug-hunt/security review required.
- Gate 5 — UI/remote release: N/A for current tooling-only implementation; real WINBRAT post-ship and release publication require separate authorization and are not claimed tested.
- Gate 6 — Integration: PENDING, mocked publication/recovery paths and workflow consistency; mechanical split characterization N/A.

## Baseline evidence

GitHub main matched base SHA. Paginated check-runs query returned successful test, characterization-windows, go-test-windows, grep and aggregate checks, no running/red entries. `.git-suggested-hash-bump.txt` absent. PowerShell is not present on control-plane PATH; methodology permits exact PR checks as remote substitute. Unrelated untracked `.dsh/performance-autoresearch/` and `vpn_issue_report.md` remain untouched.

## Outcome

IN PROGRESS. No implementation or release readiness claimed. Independent reviewers started; final evidence and PR pending.
