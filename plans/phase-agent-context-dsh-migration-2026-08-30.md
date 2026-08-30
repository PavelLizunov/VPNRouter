# Agent context — native DSH migration

**Owner**: DSH session `goal-c308114a`
**Branch**: `dsh/agent-context-migration`
**Roadmap ref**: repository trust/context follow-up
**Effort**: one focused migration
**Risk**: MEDIUM
**Blast radius**: agent instructions, project skills, hooks, documentation and contract tests; no product runtime behavior
**Rollback**: revert the migration commits

## Why

The repository currently loads two divergent Claude/Codex instruction trees.
DSH supports hierarchical `AGENTS.md` files and project-native `.dsh/skills`, so
keeping legacy mirrors wastes context, permits drift and exposes stale machine and
release guidance.

## What

- Replace root and zone `CLAUDE.md` files with verified `AGENTS.md` files.
- Consolidate project skills under `.dsh/skills` and remove `.agents`/`.claude`.
- Add current worker/resource guidance based on live read-only probes and the
  canonical homelab inventory.
- Update active documentation, hooks and contract tests to DSH terminology.
- Preserve historical plans and design transcripts as historical evidence.

## How

1. Use disjoint Gemini workers to draft scoped instruction and skill groups.
2. Integrate and remove legacy trees only after their durable rules are retained.
3. Run an independent read-only swarm over every new instruction and skill.
4. Perform a final SOL source check for every factual claim.
5. Run focused contracts, Release build, full tests and documentation checks.

### Tests written

- Update `AgentContextContractTests` to require native DSH paths, valid skill
  frontmatter, current zones/workers and absence of tracked legacy trees.

### Verification approach

Use repository searches, DSH discovery documentation, live read-only worker
preflights, contract tests, `git diff --check`, Release build and the full suite.
No application, VPN, VM lifecycle or infrastructure mutation is required.

## Verification gate

- [ ] **Gate 1 — Build clean**: `dotnet build VPNRouter.sln -c Release` returns zero errors.
- [ ] **Gate 2 — Tests green**: focused agent-context tests and the full discovered suite pass.
- [x] **Gate 3 — Docs**: DSH hierarchy, worker contract and this Outcome agree.
- [x] **Gate 4 — Independent review**: verification swarm plus SOL review leave no unverified claims.
- [x] **Gate 5 — Remote safety**: read-only worker identity/resource checks only; no VPN/UI/deploy mutation.
- [x] **Gate 6 — Legacy removal**: no tracked `CLAUDE*.md`, `.claude/` or `.agents/`; active references are clean.

## Outcome

**Status**: IMPLEMENTED — awaiting PR CI
**Commits**: `1b777df5` (brief); migration commit pending
**Pushed**: branch/PR update pending
**Test deltas**:
- Replaced the agent-context contract suite with native DSH hierarchy,
  frontmatter, worker-resource, release-authority and legacy-absence checks.
- Migrated post-ship/BRAT contract tests from dual-tree mirror assertions to the
  single native `.dsh` skill tree.
- Removed broken legacy-bootstrap references from active test comments and
  operational scripts.

**Files changed**:
- Added root/local plus scoped `AGENTS.md` files for every semantic zone and nine
  native project skills under `.dsh/skills/`.
- Removed the complete `.agents/`, `.claude/`, root/zone `CLAUDE*.md` trees and
  obsolete provider-specific hook/bootstrap guidance.
- Updated the canonical contract, worker contract, methodology, contributor/VM
  docs, hooks, release plans, tests and active code comments.

**Gate results:**
- Gate 1/2: local build/test unavailable by contract because `harness-test` has
  no .NET/Go/PowerShell SDKs; no SDK was provisioned. GitHub PR CI is the
  executable build/test oracle and remains pending for the migration commit.
- Gate 3: PASS — active Markdown links, skill YAML and hierarchy assertions pass.
- Gate 4: PASS — six independent reviewers re-opened the integrated workspace;
  every source-verified finding was fixed and all final rechecks returned READY.
- Gate 5: PASS — only read-only identity/resource observations were used; no
  install, VPN/UI scenario, deployment, cleanup or infrastructure mutation ran.
- Gate 6: PASS — legacy trees/files are absent and active DSH guidance passes the
  forbidden-path/unavailable-tool scan.
- Mechanical checks: `git diff --check`, all four hook `bash -n` checks, native
  skill YAML parsing, active-link validation and contract-assertion emulation pass.

**Surprises encountered**:
- Live probes found no .NET SDK on the three test workers; they must not be
  described as generic build machines.
- The macOS worker had limited free disk at probe time, so heavy work requires a
  fresh preflight and explicit cleanup approval rather than automatic pruning.
- Independent review found that `tools/zapret/` is a tracked payload, that the
  tools-zone `AGENTS.md` needed a `.gitignore` exception, and that all four Git
  hooks lacked executable mode; the contract, ignore rules, setup helper and
  tracked modes were corrected.

**Follow-ups spawned**: none
