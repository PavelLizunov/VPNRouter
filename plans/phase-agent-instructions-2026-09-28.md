# Agent instruction refresh

## Why

Project instructions lag global Harness routing and mix navigation with repeated
policy. The owner requested concise English instructions, using the global
AGENTS.md style and the project map as references, and explicitly selected main
as the implementation base.

## What

- Base: main `ab97905d2455f767357ed71ef09b56fb6b545f5d`.
- Branch: `dsh/agent-instructions-2026-09-28`, in an isolated worktree.
- Update root AGENTS.md and only directly related contracts, zone guidance,
  review/phase skill instructions and methodology needed to remove conflicts.
- Record source-confirmed findings in OPEN-DEFECTS.md before applying fixes.
- Use concise English, plain Markdown and ASCII punctuation in edited active
  instructions; retain technical identifiers and necessary safety constraints.
- Do not import Headless, Omarchy or the snapshot-bound agent map from PR #296.
  The map was inspected as a reference, not accepted as current-main evidence.
- Preserve release authorization, fixed-WINBRAT identity, secret handling,
  protected-main rules, existing defect statuses and worker preflight requirements.
- Exclude application code, workflows, hook implementation, releases, global
  Harness configuration, historical plan rewrites and unrelated working files.

## How

1. Compare global guidance with current-main instructions and source references.
2. Commit this brief and verified findings, push and wait for exact-head PR checks.
3. Rewrite the entry point; remove confirmed instruction conflicts without
   copying volatile versions, host inventories or the unmerged architecture map.
4. Check navigation targets, test names, skill frontmatter, text encoding,
   formatting and policy consistency. Review the complete scoped diff.
5. Update Outcome, commit, push and observe final exact-head checks.

## Risk and rollback

Documentation-only, but wrong instructions can misroute future agents. Keep
operational safety gates intact. Revert only task commits if needed; do not reset
other work or alter remotes. No merge or deployment is authorized.

## Verification gates

1. Build: N/A locally for documentation-only edits; observe applicable PR CI.
2. Tests: pending static path, command/test-name, frontmatter and diff checks;
   no VPN or remote-worker scenario is needed for this task.
3. Docs: pending English/plain-Markdown consistency and completed Outcome.
4. Review: coordinator source/diff review pending. Independent review is not
   available through a permitted explicit model route in this session; generic
   subagent tools inherit Astra, and workflow orchestration is not authorized.
   Do not present self-review as independent acceptance.
5. UI/WINBRAT: N/A; no UI, binary or deployment changes.
6. Characterization: N/A; no code extraction or public API change.

## Material unknowns and environment

- Source branch `dsh/omarchy-plugin-2026-09-17` and its untracked files stay intact.
- Neither powershell nor pwsh is available here. Use exact-head GitHub PR checks
  as allowed by plans/v3.0-execution-methodology.md; do not install an SDK/shell.
- Main baseline checks were observed successful before edits.
- Historical maps and defect prose are not current runtime guarantees.

## Outcome

Implementation pending. No release, merge, remote execution or independent-review
claim. Task instructions and verification evidence will be recorded here.
