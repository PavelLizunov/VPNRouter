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
- [ ] **Gate 3 — Docs**: DSH hierarchy, worker contract and this Outcome agree.
- [ ] **Gate 4 — Independent review**: verification swarm plus SOL review leave no unverified claims.
- [ ] **Gate 5 — Remote safety**: read-only worker identity/resource checks only; no VPN/UI/deploy mutation.
- [ ] **Gate 6 — Legacy removal**: no tracked `CLAUDE*.md`, `.claude/` or `.agents/`; active references are clean.

## Outcome

**Status**: IN PROGRESS
**Commits**: pending
**Pushed**: pending
**Test deltas**: pending
**Files changed**: pending

**Gate results:** pending

**Surprises encountered**:
- Live probes found no .NET SDK on the three test workers; they must not be
  described as generic build machines.
- The macOS worker had limited free disk at probe time, so heavy work requires a
  fresh preflight and explicit cleanup approval rather than automatic pruning.

**Follow-ups spawned**: none yet
