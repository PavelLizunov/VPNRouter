# .githooks zone guidelines

`.githooks/` contains Git hook scripts for repository quality enforcement, pre-commit validation, and pre-push CI gates.

## Hook scripts

- `pre-commit`: Enforces clean Release builds for C#/UI changes, targeted unit test suites, phase brief checks for Core service modifications, staged garbage filtering, handle leak prevention, and UTF-16 BOM validation in `.sha256` files.
- `commit-msg`: Rejects subjects over 72 characters and warns (does not block) on a non-conventional prefix.
- `pre-push`: Checks previous-commit CI only for pushes to `main`; task-branch pushes return before this check and watcher launch. This is not permission to push to protected `main`.
- `post-push`: Optional wrapper script that launches `tools/watch-after-push.ps1`. Git has no native post-push hook, so ordinary task-branch pushes do not start it. Verify exact-head PR checks explicitly as required by the canonical contract.
- Root `Setup-Hooks.ps1`: PowerShell helper that configures `git config core.hooksPath .githooks`.

## Safety and bypass policy

- Hook bypass via `--no-verify` requires explicit user/owner authorization per canonical project contract.
- Fix failing gates instead of bypassing hooks.

## Zone checks

```bash
bash -n .githooks/commit-msg && bash -n .githooks/pre-commit && bash -n .githooks/pre-push && bash -n .githooks/post-push
```
