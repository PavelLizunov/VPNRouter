# H-2a: correct false statements in active instructions and docs

## Why

The H-0 audit of active instructions (snapshot `6491be4c`) found statements that
contradict current source: a release-skill step that always fails, a wrong
in-flight candidate in `CURRENT_STATE.md`, incomplete secrets and legal notices,
stale README claims, and pointers to history removed in H-1. Agents follow these
files, so each false line is a live risk. The owner approved H-2 on 2026-09-28.

## What

- Fix verified false or stale statements in: `ship-rolling-candidate` and
  `cut-stable` skills (unsigned Windows path), `CURRENT_STATE.md`,
  `.github/SECRETS.md`, `NOTICE.md`, `PRIVACY.md`, `README.md`/`README.ru.md`,
  `.github/pull_request_template.md`, `.github/workflows/AGENTS.md`,
  `phase-task-launcher`, `post-ship-mcp-verify`, `diagnose-config`,
  `audit-overflow-fix`, `VPNRouter.Core/AGENTS.md`, `.githooks/AGENTS.md`,
  `CONTRIBUTING.md`, `docs/test-workers.md`, `.gitattributes`,
  `samples/rules/README.md`, `profiles/default-android.json` comment, and the
  `tools/zapret/` wording in the contract and `tools/AGENTS.md`.
- Replace non-code pointers to removed history with `git show 6491be4c:<path>`
  or drop them.
- Ledger: record NEW-1 and the six imported map hypotheses as unverified,
  non-gating entries, plus the instruction findings with their disposition.

## Invariants

- Every edit is checked against source at the branch base; no claim is changed
  from memory. Unverifiable items are left and listed, not guessed.
- No product code or behavior change; `tools/zapret/` files untouched.
- Rules are corrected, not weakened: safety, authority and WINBRAT constraints
  stay intact. Consolidation of duplicated rules is H-2b.
- Test pins in `AgentContextContractTests` and other contract tests stay green.

## Verification

Scoped diff review, links resolve, cut gate set unchanged except the new
non-gating entries, exact-head PR CI green.

## Rollback

Revert the PR.

## Outcome

- 37 files corrected, each against source at `015bece0`: release skills and
  `docs/release-strategy.md` now use `build.ps1 -Version VERSION -Upload`
  (verified: `-Upload` downloads the pinned `sing-box-vpnctl` release with SHA256
  and implies the split-tunnel driver; `-SingBoxPath` is rejected with `-Upload`);
  `CURRENT_STATE.md` names v2.50.0-r9 as the latest published prerelease with
  unreleased fixes on main; `.github/SECRETS.md`, `NOTICE.md`, `PRIVACY.md`, both
  READMEs, PR template, skills, zone docs, `CONTRIBUTING.md`, `.gitattributes`,
  samples README and the Android profile comment fixed; contract and
  `tools/AGENTS.md` describe `tools/zapret/` accurately without deleting it; the
  contract drops the historical v3.0 trigger.
- 26 non-code pointers to removed history now read `git show 6491be4c:plans/...`.
- Not changed after verification: the 14-source Free Configs count is correct;
  MaxMind was already in NOTICE; dated worker observations in
  `docs/test-workers.md` stay as dated observations.
- Ledger: new section with two resolved documentation entries, owner decisions
  for `tools/zapret/` and possibly sensitive archived evidence, the PR #296 map
  note, NEW-1 and six UNVERIFIED IMPORTED HYPOTHESIS entries; none carries a
  bold P0/P1 marker, and the open P0/P1 gate set is unchanged (21 lines).
- Checks: all edits matched exactly once; line endings preserved; workflow YAML
  parses; no broken relative Markdown link; exact-head CI recorded on PR #328.
