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

- [ ] Workflow parses as YAML without errors.
- [ ] Diff contains indentation changes only.
- [ ] Commit hooks pass without bypass.
- [ ] GitHub PR checks are green.

## Risk

**Justification**: the correction changes no shell command or release policy.
**Mitigation**: inspect the exact diff and validate YAML before push.
**Detection**: Dependabot and normal PR checks must accept the workflow.

## Outcome

Pending implementation and verification.
