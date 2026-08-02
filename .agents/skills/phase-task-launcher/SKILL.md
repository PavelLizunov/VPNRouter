---
name: phase-task-launcher
description: Use this skill at the START of any v3.0 refactor task (or any task that touches VPNRouter.Core/Services, VPNRouter.App/ViewModels, VPNRouter.Android/AndroidApp.*, or any other surface listed in `plans/v3.0-refactor-roadmap.md`). It wraps the per-task lifecycle defined in `plans/v3.0-execution-methodology.md`: brief → branch → impl → verify → outcome → commit. Use it whenever the user says "start Phase 1 task X", "begin v3.0 work on Y", "run task <task-id>", "do the <feature> refactor", or whenever you're about to make a >30-line code change. Skill blocks the work from proceeding if any of the 6 verification gates fail, and writes the post-mortem outcome section automatically once gates pass. Use it for refactor work, audit follow-ups, and any internal cleanup the user has approved. Do NOT use it for user-reported bug hotfixes that bypass v3.0 (those go through ship-rolling-candidate instead) or for one-line typo fixes (overkill for those).
---

# phase-task-launcher

Wraps the VPNRouter v3.0 per-task workflow so methodology can't be silently skipped.

## When this skill fires

This is the **front door** for every Phase 1/2/3 task tracked in `plans/v3.0-refactor-roadmap.md`. If you're about to:

- Spawn an agent to do a Phase 1 quick win
- Start a god-file split
- Introduce a new abstraction (`IProcessRunner`, `IFileSystem`, etc.)
- Touch the auto-update / placeholder defense / firewall path
- Run a Phase 2D refactor on `MainWindowViewModel.cs`, `AndroidApp.axaml.cs`, `VpnEngine.cs`, or any file flagged as a god-class in `plans/v3.0-architecture-roadmap.md` §2

...invoke this skill FIRST. It walks the 7-step lifecycle and refuses to skip steps.

## Why this exists (the honest version)

The user asked "how do I know you'll follow the methodology?" The methodology doc is 583 lines — easy to forget under load. Hooks catch some violations at commit time (build red, missing trailer, garbage files), but they can't enforce **process discipline** (was a brief written? was the Outcome section filled? did `simplify` run on a >100 LOC change?).

This skill is that process-discipline gate. It says no when something is missing. If the user is reading the transcript later and asks "did you follow methodology?", they should see this skill firing at the start of each task and producing a brief + verification record.

## The 7-step lifecycle

For every qualifying task:

### Step 1 — Locate or write the brief

Briefs live at `plans/phaseN-<task-slug>-<YYYY-MM-DD>.md`. Use the template from methodology §2 (reproduced below as a reference doc — see `references/brief-template.md`).

**If a brief already exists for this task** (the user pre-wrote it, or a prior session did): read it. Verify the "Why / What / How / Verification gate" sections are non-empty. If they are empty or sparse, **stop and surface the brief to the user** with a request to fill in the gaps.

**If no brief exists**: write one. Use the template. Fill in:
- "Why" — one paragraph: what problem this solves, value delivered
- "What" — concrete file list, line ranges, before/after sketch
- "How" — step-by-step plan
- "Verification gate" — checklist tailored to this task type (see §2 below)
- "Risk" — LOW/MEDIUM/HIGH with one-line justification

Commit the brief BEFORE starting implementation. This is non-negotiable. The brief commit message starts with `docs(plan): brief — <task name>` and references the relevant `plans/v3.0-refactor-roadmap.md` section.

### Step 2 — Pick the branch

Per methodology §1:

- Phase 1 quick wins → directly on `main` (low risk, small)
- Phase 2 god-file splits → `Codex/ph2-<slug>-<seq>` feature branch off `v3.0-prep`
- Phase 2 abstractions (interfaces) → `Codex/ph2-<slug>-<seq>`
- Phase 3 Avalonia 11→12 → long-lived `v3.0-avalonia12` branch
- Phase 3 Newtonsoft→STJ → long-lived `v3.0-stj-migration` branch
- Other Phase 3 → short-lived feature branches off `v3.0-prep`

Create the branch. If working on `main`, skip this step.

### Step 3 — Implement

Make the code change(s). Follow §11 "Quality bars" from the methodology doc:

- Async-first for I/O
- `#nullable enable` in every new file
- `sealed` by default
- Records over classes for immutable DTOs
- No magic numbers — name constants at class top
- `[SupportedOSPlatform("windows")]` for Win-only services
- New tests written per §5 of methodology (test pyramid + characterization for splits)

### Step 4 — Run verification gates

The 6 gates from §3:

1. **Build clean**: `dotnet build VPNRouter.sln -c Release` → 0 errors (warnings tolerated only if predated change). For Android: include `-p:EnableAndroidTarget=true`.
2. **Tests green**: full suite — `dotnet test VPNRouter.Tests/VPNRouter.Tests.csproj -c Release --no-build`. If task is behavior-changing, the new tests must be IN this run.
3. **Docs**: brief Outcome section filled (Commits, Test deltas, Files changed, Surprises, Follow-ups). README updated if user-facing surface changed. AGENTS.md updated if architecture changed.
4. **Self-review**:
   - For diff >100 LOC OR touching public API → run `simplify` skill on the diff
   - For changes touching auth / TLS / process exec / file I/O / firewall / placeholder defense → run `security-review` skill
5. **Remote brat UI verify** (UI changes only): `tools/brat-verify.ps1` (`-Action uia` / `-Action screenshot`) against the fixed test VM WINBRAT (192.168.0.106), PASS/FAIL per UI feature point, screenshots under `artifacts/brat-verify/`, attach refs to brief Outcome. VM/WinRM unavailable → BLOCKED, never a local fallback.
6. **Characterization diff** (god-file splits only): pre-split snapshot test must match post-split snapshot test. Zero behavior drift allowed.

**If any gate fails: STOP.** Do not commit. Do not push. Surface the failure to the user with: (a) the gate that failed, (b) the diagnostic output, (c) recommended next step (fix the implementation, expand the test, document the deviation if intentional).

This is the refuse-to-proceed mechanism the user asked for.

### Step 5 — Fill the Outcome section

Append to the brief file:

```markdown
## Outcome (filled <YYYY-MM-DD>)

**Status**: PASS / PARTIAL / BLOCKED
**Commits**: <hash list>
**Test deltas**: +<new> / -<removed>
**Files changed**: <count> · <total LOC delta>
**Verification gate results**:
- [x] Gate 1 build: 0 errors
- [x] Gate 2 tests: 765/765 green (+X new)
- [x] Gate 3 docs: ...
- [x/-] Gate 4 self-review: simplify ran / security-review ran / N/A
- [x/-] Gate 5 remote brat UI verify: screenshot @ artifacts/brat-verify/<task>.png / N/A
- [x/-] Gate 6 characterization diff: snapshot match / N/A
**Surprises encountered**: <list>
**Follow-ups spawned**: <task chips OR plans/ entries>
**Rollback**: `git revert <hash>` / branch delete
```

### Step 6 — Self-review run (if §3 Gate 4 required it)

Invoke `simplify` skill or `security-review` skill via the `Skill` tool. Read the output. If issues found: address them, re-run gates, re-run skill. Only proceed when skill returns clean.

Record the skill invocation in the brief Outcome under "Verification gate results — Gate 4".

### Step 7 — Commit + push

- Commit message format from methodology §11: `<scope>: <subject>` (under 72 chars), body explains WHY, `Co-Authored-By` trailer mandatory.
- Push to both remotes: `git push github HEAD:<branch> && git push origin HEAD:<branch>`
- For feature branches: open PR (if repo uses PRs — VPNRouter does NOT, direct commit model) OR merge to v3.0-prep with `--no-ff`.
- After successful push: append "**Pushed**: github + origin commit `<hash>`" to brief Outcome.

The pre-commit hooks installed in `.githooks/` will enforce a subset of these gates automatically — but DO NOT rely on hooks alone. The hooks catch build + garbage files; this skill catches process gaps.

## Refuse-to-proceed conditions

Stop the workflow and surface to user when:

1. **Phase task without a brief** — methodology §2 violation
2. **Brief without "Why / What / How / Verification gate" sections filled** — methodology §2 template incomplete
3. **Implementation without `dotnet build` clean** — Gate 1 failed
4. **Implementation without `dotnet test` green** — Gate 2 failed
5. **Behavior change without new test** — methodology §5 violation
6. **God-file split without characterization snapshot** — Gate 6 cannot be checked
7. **UI change without remote brat UI verify screenshot** — Gate 5 missing (VM/WinRM unavailable = BLOCKED, no local fallback)
8. **Diff >100 LOC without `simplify` run** — Gate 4 missing
9. **Security-relevant change without `security-review` run** — Gate 4 missing
10. **Commit attempt without `Co-Authored-By` trailer** — hook will reject anyway, but flag upstream
11. **Commit message subject >72 chars** — hook will reject anyway

For each refuse-to-proceed, the message back to the user must include:
- Which gate / methodology section was violated
- What's needed to unblock
- The most-recent diagnostic output (build error, test output, etc.)

Never silently skip gates. Surfacing failures is the point of this skill.

## Quick-win exception path

For Phase 1 trivial tasks (< 5 lines changed, no behavior change, single-file scope, no test impact), the brief may use the abbreviated template at `references/brief-template-quickwin.md`. Still requires Why/What/Risk/Outcome, but Verification gate may be reduced to "build clean + commit-msg hooks pass".

## When NOT to use this skill

- One-line typo fixes in docs / comments
- User-reported P0 bug hotfix that needs immediate ship (use `ship-rolling-candidate` skill instead — bypass v3.0 prep)
- Branch-protection / hook config changes (meta-config; skill itself is the thing being installed)
- Pure agent-spawn coordination where no code changes (the integrator's role)

## References

- `plans/v3.0-execution-methodology.md` — the full 14-section methodology this skill enforces
- `plans/v3.0-refactor-roadmap.md` — Phase 1/2/3 task list with prioritized order
- `plans/v3.0-architecture-roadmap.md` — god-file split plans (Phase 2B/2C)
- `plans/dead-code-audit-2026-05-17.md` — deadwood removal candidates
- `plans/feature-catalog-2026-05-17.md` — feature flow + complexity matrix
- `plans/test-coverage-audit-2026-05-17.md` — untested services list
- `references/brief-template.md` — full brief template (this directory)
- `references/brief-template-quickwin.md` — Phase 1 quick-win abbreviated template
- `.githooks/pre-commit` — hard enforcement layer for build + garbage + sha256 BOM gates
- `.githooks/commit-msg` — hard enforcement for subject length + conventional prefix + Co-Authored-By trailer

## Diagnostic checklist (use when stuck)

If a task is blocked and you can't figure out which gate, walk through:

1. Did you write a brief?  → §2 if no
2. Is the brief complete?  → §2 sections filled?
3. Did `dotnet build -c Release` exit 0?  → §3 Gate 1
4. Did `dotnet test` show 100% pass?  → §3 Gate 2
5. Are docs updated for user-facing changes?  → §3 Gate 3
6. Did `simplify` run if diff > 100 LOC?  → §3 Gate 4
7. Did `security-review` run if security-relevant?  → §3 Gate 4
8. Did remote brat UI verify run if UI changed?  → §3 Gate 5
9. Did characterization snapshot match if god-file split?  → §3 Gate 6
10. Is Outcome section filled?  → §2 template

If any answer is "no" or "I don't know": fix that, then re-attempt.
