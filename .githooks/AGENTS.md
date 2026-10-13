# .githooks zone guidelines

`.githooks/` contains Git hook scripts for repository quality enforcement, pre-commit validation, and pre-push CI gates.

## Hook scripts

- `pre-commit`: Enforces clean Release builds for C#/UI changes, targeted unit test suites, phase brief checks for Core service modifications, staged garbage filtering, handle leak prevention, and UTF-16 BOM validation in `.sha256` files.
- `commit-msg`: Rejects subjects over 72 characters and warns (does not block) on a non-conventional prefix.
- Root `Setup-Hooks.ps1`: PowerShell helper that configures `git config core.hooksPath .githooks`.

## Zone checks

```bash
bash -n .githooks/commit-msg && bash -n .githooks/pre-commit && bash -n .githooks/pre-push && bash -n .githooks/post-push
```
