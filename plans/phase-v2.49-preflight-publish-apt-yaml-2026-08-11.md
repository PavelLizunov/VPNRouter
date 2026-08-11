# v2.49 preflight — restore Dependabot workflow parsing

**Owner**: Codex session 2026-08-11
**Branch**: `codex/fix-publish-apt-yaml`
**Roadmap ref**: root `AGENTS.md` CI status hygiene gate before v2.49 work
**Effort**: 15 minutes
**Risk**: LOW
**Blast radius**: one GitHub Actions workflow · indentation-only correction · no release execution
**Rollback**: revert the implementation commit or close the branch

## Why

The latest `main` commit has a failed Dependabot check because
`.github/workflows/publish-apt.yml` is not parseable. Project methodology blocks
new v2.49 product work while any of the latest seven commits has a red check.

## What

Restore the YAML block-scalar indentation of the extended APT artifact retry
loop introduced by `7cf39ec6`. Preserve the intended twelve attempts and
six-minute timeout exactly.

## How

1. Correct only the four under-indented lines in the shell block.
2. Parse the workflow locally with an existing YAML parser.
3. Commit and push the isolated fix, then open a PR to `main`.
4. Require the PR checks to pass before beginning v2.49 product code.

## Verification gate

- [x] Workflow parses as YAML without errors.
- [x] Diff changes only block indentation and its explanatory comment.
- [x] Commit hooks pass without bypass.
- [x] GitHub PR checks are green.

## Risk

**Justification**: the correction changes no shell command or release policy.
**Mitigation**: inspect the exact diff and validate YAML before push.
**Detection**: Dependabot and normal PR checks must accept the workflow.

## Outcome

**Status**: PASS
**Commits**: `0166fad3` (brief), `e65babc6` (workflow correction)
**Test deltas**: none
**Files changed**: 2 · workflow +5/-6 before this Outcome update
**Verification gate results**:
- [x] YAML parse: PyYAML `safe_load` accepted the workflow and found the publish job.
- [x] Diff review: twelve attempts, thirty-second delay, and six-minute failure remain unchanged.
- [x] Hooks: pre-commit, commit-msg, and pre-push passed without bypass.
- [x] PR #130 CI: `test`, `go-test-windows`, and `grep` passed; the product-only
  `characterization-windows` job skipped as expected.
**Surprises encountered**: the latest `main` workflow was accepted by the stable
release merge but rejected by Dependabot because five shell-block lines had lost
their YAML indentation.
**Follow-ups spawned**: begin v2.49 connection-stability work from this green head.
**Rollback**: revert `e65babc6` or close PR #130.
