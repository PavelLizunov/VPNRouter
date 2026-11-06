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

1. Build: PASS on implementation 952102cb. Hosted Release builds and Android
   Release APK compilation/inventory succeeded; both APKs contain arm64-v8a only.
   No local SDK/build or private installer rebuild.
2. Tests: PASS on implementation 952102cb. Linux 3523 passed / 79 skipped;
   Windows 3693 / 21; Business 285 / 1. Headless publish/contracts, Arch packaging,
   Go Windows, receiver and UI MCP checks also succeeded. No tests removed.
3. Docs: PASS locally. Current symbols, linked release owner and paths verified;
   Outcome records observed implementation checks and the evidence-only follow-up.
4. Review: source triage completed; independent cross-family review N/A by owner
   choice. This small deletion/correction batch introduces no feature or API.
5. UI/live: N/A. No reachable UI flow changes and no new binary is shipped.
6. Characterization: PASS locally. An independent retained-text comparison with
   e5ea6553 confirms exactly 29 member lines deleted and every retained byte
   preserved in all three source files. No extraction or rewrite; compilation
   remains a separate gate.

GitHub-hosted runners are the build surface. Existing workflow preflight output
is evidence only for the jobs that emit it; no control-plane build is permitted.

## Outcome

- Status: PASS for implementation 952102cb; evidence-only follow-up, then observe
  final PR-head CI before handoff. Product source is unchanged by this follow-up.
- Changed scope: ten approved files plus this brief and ledger; three whole
  private members removed, four instruction maps corrected, three CI routes fixed.
- Git: https://github.com/PavelLizunov/VPNRouter/pull/650 targets business;
  PR #646 remains at e5ea6553. Bootstrap commit: 8249d9a49b34b8e2372d82e0f56ac66ccf21cb54.
- Bootstrap checks: T3 wake reports all 12 passed on 8249d9a (10 applicable
  successes / two expected private skips); no Android source changed in that block.
  Direct/API and connector requests initially failed with TLS/transport errors.
  A temporary SSH transport restored the GitHub API read; full PR head and all
  10 successes/two expected skips were then independently confirmed by API.
- Local checks: git diff --check, retained-text comparison, source/path/anchor and
  YAML route assertions pass. C# dead-name rescan after documented callback/
  entry-point exclusions returns zero candidates; the ledger also mentions removed
  names, so this result is bounded and does not establish whole-program reachability.
  A targeted source scan confirms the removed identifiers are absent from code.
- Review: existing exclusions and callers rechecked by the lead. No external model
  used, and no independent cross-family review is claimed.
- Implementation checks: GitHub API confirms full head
  952102cb89c0ed40beaa2b79cefe054b8f3ef8e0 with exactly 11 SUCCESS and two expected
  private-job SKIPPED. Recorded aggregate counts come from the actual job logs.
  Runs: 42052486462, 42052486591, 42052486614, 42052486903.
- RH-01 through RH-08 are resolved in source/CI in PR #650. The follow-up changes
  only this record and the ledger; its exact-head checks must also pass. No live
  behavior, installer, deployment or merge acceptance is claimed.
- Follow-ups: unknown history and foreign refs/worktrees retained. Existing memory,
  live dataplane and macOS runtime coverage limitations are not resolved here.
- Cleanup: protected outside-repository audit artifacts intentionally retained.
