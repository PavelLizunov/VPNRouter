# Business repository hygiene

- Branch: `dsh/business-repo-hygiene-20261105`
- Accepted base and PR target: `business`, `e5ea65534480e68409365b8dc3471750839c2a16`.
- Owner scope: separate PR into Business; preserve PR #646 and its source ref.
- Risk: LOW. Whole unused private members, navigation corrections and CI routing.
- Rollback: revert this task's commits; the delivered installer is unchanged.

## Why

The published Business branch needs the regression and fingerprint workflows that
currently target only main/v3.0-prep. The UI workflow's PR path filter also misses
App/Core changes. A source audit found three unused private members and stale
instruction facts, recorded as RH-01 through RH-08 in OPEN-DEFECTS.md.

## What

Apply the owner's ten-file cleanup batch, plus this brief and ledger updates:

- Delete ResetExportOverlay, SetChevronOpen and IsSilentWrapper as whole members.
- Correct Core/Android symbol maps and obsolete packaging/release guidance.
- Add business to relevant CI branch filters and App/Core to the UI PR paths.

Keep callbacks, entry points, tests, history, binaries, public APIs and routing
behavior. Do not remove the related internal strategy constant without a separate
consumer check. No installer rebuild, VM/backend mutation, merge, tag or release.

## How

1. Commit this brief, findings and CI bootstrap before product-source edits.
   This first block enables checks for the owner-selected PR base.
2. Wait for exact-head applicable checks through the PR watch.
3. Apply the remaining seven-file cleanup, verify retained source against the base,
   rescan names and observe Android compilation plus the regression gates.
4. Update this record and ledger with actual evidence. External models are excluded
   by the owner's choice; no independent cross-family review is claimed.

## Audit baseline

The owner's repo-hygiene 1.0.0 toolkit scanned all 1481 tracked files: 1417 text,
64 binary/oversized, 241306 text lines. Decisions: 35 active, 1263 reference,
183 unknown, all retained. Static README checks report zero FAIL for both languages.
Python/XML/shell/JSON/YAML parse checks passed; these do not prove compilation.
Of five C# name candidates, three private helpers are unused; the Android listener
callback and Windows support executable entry point remain. All 21 Python candidates
are framework/test entry points and remain. No new actual secret leak was confirmed;
generic scanning is not an all-history or known-secret guarantee.

Baseline e5ea6553 has 11 successful checks and two expected private-job skips.
The audit's full census, decision table and proposed patch remain outside the
repository; current Business delivery evidence and existing memory findings remain.

## Verification gates

1. Build: PENDING hosted Release regression builds and, after Android edits,
   the existing unsigned Android Release APK compile/inventory job. No local SDK.
2. Tests: PENDING Linux/Windows, Headless, packaging and affected Business gates.
   No tests are removed or rewritten for this cleanup.
3. Docs: PENDING source-checked symbol/path/link corrections and final Outcome.
4. Review: source triage completed; independent cross-family review N/A by owner
   choice. This small deletion/correction batch introduces no feature or API.
5. UI/live: N/A. No reachable UI flow changes and no new binary is shipped.
6. Characterization: PENDING exact retained-line comparison after whole-member
   deletions. No extraction or rewrite; compilation remains a separate gate.

GitHub-hosted runners are the build surface. Existing workflow preflight output
is evidence only for the jobs that emit it; no control-plane build is permitted.

## Outcome

- Status: IN PROGRESS; initial CI bootstrap prepared, source cleanup not applied.
- Git: separate PR to business pending; PR #646 remains at e5ea6553.
- Checks: base checks verified via GitHub API; no new-head success claimed.
- Follow-ups: unknown history and foreign refs/worktrees retained. Existing memory,
  live dataplane and macOS runtime coverage limitations are not resolved here.
- Cleanup: protected outside-repository audit artifacts intentionally retained.
