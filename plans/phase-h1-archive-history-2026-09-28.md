# H-1: move repository history out of main

## Why

Main carries 726 files under `plans/` plus `design/`, `opendesign/` and lab
scripts: dated plans, reports, evidence and superseded designs whose relevance is
unconfirmed. They steer agents toward stale guidance and cannot fit in context.
The owner approved a hard cleanup on 2026-09-28 and accepts minor negative
effects on Markdown-only material.

## What

Input: H-0 sorting of 977 non-code files at `6491be4c` (script scan plus
per-file classification; kept outside the repository).

- Remove from main about 816 historical files: all of `plans/` except
  `OPEN-DEFECTS.md` and `AGENTS.md`, all of `design/` and `opendesign/`, unused
  root icons, the abandoned winget manifest, and unconsumed lab scripts
  (`README-VM.md`, `setup-vm.ps1`, `update-helper.cmd`,
  `tools/pve-guest-exec.ps1`, `tools/android-e2e-test.sh`) and similar items.
- Move 10 still-live documents to `docs/` (release strategy, cut-stable
  checklist, SignPath runbook, interaction contracts, execution methodology,
  project cheatsheet, Android keystore backup and development methodology).
- Fix references that would break: `AgentContextContractTests`,
  `tools/check-methodology.sh`, contract zone table, README links, skill and
  template paths, `CURRENT_STATE.md`, `docs/tag-retention-policy.md`.
- `plans/AGENTS.md` records where history lives: every removed file stays
  readable with `git show 6491be4c:<path>`.

## Invariants

- No product code or behavior change. Code comments that cite archived plans
  stay as they are; they are handled in the later code cleanup.
- `tools/zapret/` is untouched (contract-protected; owner question pending).
- Legal files, shipped profiles and samples, diagnostics scripts, workflows'
  behavior and everything read by tests, CI or build stay or have their consumer
  updated in the same change.
- No archive branch or tag is pushed; `AGENTS.local.md` allows only the task
  branch. History remains in Git at `6491be4c`.

## Verification

Diff shows only deletions, the listed moves and reference fixes; no remaining
file references a removed path except code comments and dated ledger entries;
exact-head PR CI green (`test` runs `AgentContextContractTests`). A short
independent re-check of the result follows as the owner requested.

## Rollback

Revert the PR; all content is in Git history.

## Outcome

- Removed 816 files (about 120k lines): 714 under `plans/`, 29 `design/`
  (including its AGENTS.md), 59 `opendesign/`, 4 winget manifest files, 3 lab
  scripts under `tools/`, 6 root files (lab VM docs and scripts, unused icons,
  update helper) and the unused `.dsh` quick-win brief template. `plans/` now
  holds `AGENTS.md`, `OPEN-DEFECTS.md` and this brief; the tree has 874 tracked
  files instead of 1689.
- Moved 10 live documents to `docs/` and indexed them in `docs/AGENTS.md`;
  rewrote every reference to their old paths (skill, hook comment, PR template,
  workflow comment, scripts, test comments, ledger).
- `tools/check-methodology.sh` now reads `docs/android-development-methodology.md`.
- `AgentContextContractTests` lists updated (moved files, removed
  `README-VM.md`, host workflow plan and `design/AGENTS.md`); contract zone row
  for `design/` removed in the same change.
- README, `.github/SECRETS.md` and 19 ledger links now point to
  `git show 6491be4c:plans/<file>` instead of missing files.
- Left as history pointers (non-breaking, explained in `plans/AGENTS.md`):
  code comments, workflow and script comments, `.gitignore` rules for removed
  files, and "see also" lines inside moved documents. Handled in later stages.
- Checks: line endings preserved; no relative Markdown link is broken; the
  open P0/P1 gate set is unchanged (21 lines). Exact-head CI at `5b1fde17`
  passed `test`, `characterization-windows`, `go-test-windows` (run 36460047556)
  and `grep` (run 36460047847); `test` includes `AgentContextContractTests`.
- Post-move script check: no history-like file remains outside code projects
  except this brief.
- `tools/zapret/` untouched pending the owner decision.
