# Phase 1: update-shell and CLI publish repair

Date: 2026-09-01
Branch: `dsh/update-shell-cli-repair`
Accepted base: `origin/main` at `f797601540762ba42bc4e49b18de1f60f7140265`

## Why

Updating PR #196 to the accepted base exposed a Windows packaging failure at `dotnet publish VPNRouter.CLI`. The Spectre.Console dependency pair merged by #173 was not exercised by ordinary PR CI. A bot then rewrote #196 with a green commit that reverted unrelated changes already accepted on `main`; the exact-head merge guard prevented that tree from being merged.

Use a DSH-owned branch to preserve only the reviewed update hardening, restore the last packaged CLI dependency pair, and add the missing publish gate.

## What

- Replace interpolated process argument strings in Unix update paths with `ProcessStartInfo.ArgumentList`.
- Escape paths embedded in single-quoted POSIX helper scripts and remove the path from a double-quoted shell log line.
- Restore a macOS backup only after removing a partial failed target, and honor the documented 30-second Linux parent wait.
- Roll back `Spectre.Console` and `Spectre.Console.Cli` to the last packaged `0.49.1` pair.
- Add a Windows CLI publish step to PR CI and adversarial POSIX quoting coverage.

## Non-goals

- No updater architecture rewrite or new dependency.
- No unrelated dependency upgrades.
- No tag, release, deployment, stable cut, or installation.

## How

Reuse .NET `ArgumentList` for process argv boundaries. Keep the existing generated-script design and the standard POSIX single-quote escape (`'` becomes `'\''`) only where shell text is unavoidable. Retain the existing Windows packaging workflow as the end-to-end oracle.

## Risk and rollback

Risk is medium: update installation and rollback paths are release-critical and cross-platform. Revert the task's implementation commit to restore the previous behavior; the brief remains as the audit trail. A failed check blocks merge.

## Verification gates

1. **Scope gate:** PR net diff contains only this brief, update implementation/tests, CLI package pins, and the test workflow; no accepted `main` files disappear.
2. **Argument gate:** adversarial spaces, quotes, dollar expansion, command substitution, backticks, and newlines round-trip literally through `/bin/sh` on Unix CI.
3. **CLI gate:** `dotnet publish VPNRouter.CLI` succeeds on the Windows PR runner with the pinned package pair.
4. **Packaging gate:** `test-update` builds both Windows ZIP layouts and completes the updater integration path.
5. **Regression gate:** `test`, `grep`, `go-test-windows`, and `characterization-windows` are green on the exact reviewed head.
6. **Review gate:** distinct correctness, security, and compatibility reviewers have no surviving critical/important finding; no release action is performed.

## Outcome

- Implementation head `0e45dbc740e2383699f7ae46f3ce0b49ac0b57fd` changed four product/CI files: 80 insertions and 24 deletions; this brief is the fifth PR file.
- GitHub CI passed `test` (2,818 tests), `grep`, `go-test-windows`, `characterization-windows` (19 tests), and `test-update`. The update job successfully published the CLI, built both ZIP layouts, ran the staged update, and verified replacement files plus install receipt.
- Manual checks passed `git diff --check`, workflow YAML parsing, and an adversarial POSIX single-quote round-trip without command expansion. The control-plane checkout has neither `dotnet` nor `pwsh`; no SDK was installed and GitHub Actions remained the build oracle.
- Three independent correctness/security/compatibility reviews found a partial-target rollback defect, the missing CLI publish gate, and weak adversarial coverage. All blocking findings were source-verified and fixed; follow-up reviews returned no surviving critical/important issue.
- Surprise: the original bot PR #196 changed head during checks and reverted accepted `main` changes. Exact-head validation prevented merge; #196 was closed for this DSH-owned replacement and candidate incident `INC-1321` was recorded.
- Rollback: revert implementation commit `0e45dbc7`; no release, tag, deployment, or installation was performed.
