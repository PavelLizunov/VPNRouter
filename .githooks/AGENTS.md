# .githooks zone guidelines

`.githooks/` contains Git hook scripts for repository quality enforcement, pre-commit validation, and pre-push CI gates.

## Hook scripts

- `pre-commit`: Enforces clean Release builds for C#/UI changes, targeted unit test suites, phase brief checks for Core service modifications, staged garbage filtering, handle leak prevention, and UTF-16 BOM validation in `.sha256` files.
- `commit-msg`: Validates subject line length (max 72 characters) and conventional commit prefix formatting.
- `pre-push`: Verifies that the previous commit's CI check passed before allowing pushes to `main`.
- `post-push`: Launches `tools/watch-after-push.ps1` for cross-commit CI drift watching.
- Root `Setup-Hooks.ps1`: PowerShell helper that configures `git config core.hooksPath .githooks`.

## Safety and bypass policy

- Hook bypass via `--no-verify` requires explicit user/owner authorization per canonical project contract.
- Fix failing gates instead of bypassing hooks.

## Zone checks

```bash
bash -n .githooks/commit-msg && bash -n .githooks/pre-commit && bash -n .githooks/pre-push && bash -n .githooks/post-push
```
