# Phase N — <Task name>

**Owner**: Claude session-id <id>
**Branch**: claude/phN-<slug>-<seq>
**Roadmap ref**: plans/v3.0-refactor-roadmap.md §<section>
**Effort**: <X min/hour/day>
**Risk**: LOW / MEDIUM / HIGH
**Blast radius**: <files touched> · <LOC delta> · <runtime impact>
**Rollback**: `git revert <commit>` / branch delete

## Why
<one paragraph: what problem this solves, what value it adds>

## What
<concrete change list — files, line ranges, before/after sketches>

```diff
- /* before snippet */
+ /* after snippet */
```

## How
<step-by-step plan>

1. Step 1
2. Step 2
3. ...

### Tests written
- `<TestClass>.<TestMethod>` — what it verifies
- `<TestClass>.<TestMethod>` — edge case X

### Verification approach
<how we know it works: full test suite, characterization snapshot, remote brat UIA verify/screenshot, etc.>

## Verification gate
Check off each as you complete:

- [ ] **Gate 1 — Build clean**: `dotnet build VPNRouter.sln -c Release` → 0 errors. (For Android: also `-p:EnableAndroidTarget=true`.)
- [ ] **Gate 2 — Tests green**: full suite passes (current count: 765). New tests included.
- [ ] **Gate 3 — Docs**: brief Outcome filled. README + CLAUDE.md updated if user-facing / architecture change.
- [ ] **Gate 4 — Self-review**: `simplify` skill ran (if diff >100 LOC) AND/OR `security-review` ran (if security-relevant). Note "N/A" if not applicable.
- [ ] **Gate 5 — Remote brat UI verify**: if UI changed, run `tools/brat-verify.ps1` (`-Action uia` / `-Action screenshot`) against WINBRAT @ 192.168.0.106; screenshots under `artifacts/brat-verify/` (attach reference, e.g. `artifacts/brat-verify/<task>.png`). VM/WinRM unavailable → BLOCKED, no local fallback. "N/A" if no UI surface.
- [ ] **Gate 6 — Characterization diff**: pre-split snapshot matches post-split (god-file splits only). "N/A" otherwise.

## Outcome (filled after merge)

**Status**: PASS / PARTIAL / BLOCKED
**Commits**: `<hash1>` `<hash2>`
**Pushed**: github + origin commit `<hash>`
**Test deltas**: +<new> / -<removed>
**Files changed**: <count> · <total LOC delta>

**Gate results:**
- [x] Gate 1: <output e.g. "0 errors, 140 warnings (pre-existing)">
- [x] Gate 2: <e.g. "765/765 passing">
- [x] Gate 3: <e.g. "README updated, CLAUDE.md unchanged">
- [x] Gate 4: <e.g. "simplify clean / security-review N/A — no security surface touched">
- [-] Gate 5: <e.g. "N/A — Core-only change">
- [-] Gate 6: <e.g. "N/A — not a god-file split">

**Surprises encountered**:
- <list non-obvious finds>

**Follow-ups spawned**:
- <task chip refs or new plans/ entries>

**Lessons for methodology doc** (if any):
- <suggested updates to v3.0-execution-methodology.md>
