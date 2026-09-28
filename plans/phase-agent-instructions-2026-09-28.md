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
2. Tests: PASS static path, command/test-name, frontmatter and diff checks;
   no VPN or remote-worker scenario is needed for this task.
3. Docs: PASS English/plain-Markdown consistency and completed Outcome.
4. Review: PASS coordinator source/diff review. Independent review is not
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

Implemented on the owner-selected main base. PR: https://github.com/PavelLizunov/VPNRouter/pull/323.

- Root AGENTS.md is a 22-line English entry point. Canonical navigation now includes
  the existing Core Services, Core Platform and App ViewModels subzones.
- Related guidance follows global model-route permissions, preserves required
  independent-review limitations and distinguishes review from repair authority.
- Removed decorative punctuation from edited instructions and stale Android hash
  coverage claims. Versions point to owning build files rather than one shared pin.
- Documented the existing no-PowerShell PR-check path and the non-native post-push
  watcher. Hook code, release rules and application code are unchanged.
- The three task findings are resolved in source in OPEN-DEFECTS.md, explicitly
  unmerged and documentation-only. Existing product findings remain unchanged.

### Verification evidence

- `git diff --check`: exit 0.
- One-off stdlib check: `python3 /tmp/vpnrouter-agent-instructions-20260928-check.py /var/lib/dsh/Project/VPNRouter-agent-instructions`: exit 0. Checked 21 agent files, 13 changed active documents, 54 relative Markdown links, 9 skill frontmatters and 17 distinct test filters against source classes. Checked ASCII/control characters, bootstrap length, existing AgentContextContractTests text pins and absence of unmerged architecture imports. This is static evidence, not test discovery or runtime execution.
- Reviewed the complete changed instruction diff against global AGENTS.md, current
  source references and AgentContextContractTests.cs. No product changes or new
  dependencies. The pre-existing test suite is not edited to accommodate the docs.
- Brief commit `c0cacb7ff1437b9ab38b08104f6b2fb5897b34b3` passed all four PR checks
  before implementation: test, characterization-windows, go-test-windows and grep.
- Final implementation CI must be observed after push. Its exact-head receipt will
  be posted to PR #323; earlier green checks are not acceptance of the new diff.
- Independent reviewer execution: NOT VERIFIED; no authorized explicit worker
  route is exposed here. No model, provider or Harness policy was changed to obtain one.
- Full local build, VPN/GUI execution and WINBRAT: not run; documentation-only scope.

### Delivery Gate Verification (Prose)

- PASS: claims are tied to inspected source or executed checks; no fabricated evidence.
- PASS: concise English instructions, direct actions and no decorative punctuation in edited active documents.
- PASS: entry points link to owning rules; historical maps are not presented as current behavior.

### Handoff

Retain the isolated task worktree for review. The original Omarchy branch, its
untracked files and global Harness configuration are untouched. The temporary
validator is task-owned and may be removed after delivery; it is not a shipped
utility. No merge, release, deployment or runtime verification is claimed.
