# H-2b: consolidate active instructions

## Why

After H-2a the instructions are accurate, but several files still duplicate the
contract or each other, and two skills break the project's English-only rule.
Duplicates drift and give agents two sources for one rule. The owner approved
H-2b on 2026-09-28.

## What

- Remove `AGENTS.local.md`; move its two rules not already in the contract
  (push only the current task branch; never add or rewrite remotes) into
  `docs/agent-contract.md`, and drop the root bootstrap pointer to it.
- Remove `docs/project-cheatsheet.md` (Russian duplicate of the contract, root
  AGENTS and zone table, already stale).
- Remove the `update-readme-versions` skill; fold its one step (sync README build
  examples in both languages) into `ship-rolling-candidate` and `cut-stable`.
- Rewrite `audit-overflow-fix` in concise English and verify its facts.
- Replace the duplicated WINBRAT rule in `tools/AGENTS.md` with a link to the
  contract; drop the historical v3.0 trigger from the root bootstrap.
- `.gitignore`: remove re-include rules for deleted scripts and a pointer to a
  removed plan.
- Update `AgentContextContractTests` lists (skills, active files) in the same
  change.

## Invariants

- No safety, authority or WINBRAT rule is lost; each removed duplicate is
  already owned by the contract or moved there.
- No product code or behavior change. Code comments are H-3.

## Verification

Diff review against this list, links resolve, skill frontmatter keeps three
keys, exact-head PR CI green.

## Rollback

Revert the PR.

## Outcome

- Removed `AGENTS.local.md`, `docs/project-cheatsheet.md` and the
  `update-readme-versions` skill (3 files). The contract now owns "push only the
  current task branch" and "never add or rewrite remotes"; both release skills
  carry the README build-example sync step.
- `audit-overflow-fix` rewritten in English (143 -> 70 lines); its width facts,
  token names, `radio-card` styles and test classes were checked against source.
- `tools/AGENTS.md` links the WINBRAT rule to the contract instead of copying it;
  root bootstrap drops the local overlay pointer and the v3.0 trigger.
- `.gitignore` loses re-include rules for three deleted scripts and a pointer to
  a removed plan; artifact rules asserted by tests are unchanged.
- `AgentContextContractTests`: skill list 9 -> 8; active-file list drops the two
  removed files. Root bootstrap stays within the 8-25 line pin (22 lines).
- Checks: every edit matched exactly once; no broken relative Markdown link; no
  reference to removed files outside historical plans; exact-head CI on PR #329.
