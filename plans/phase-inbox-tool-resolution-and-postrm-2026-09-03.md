# Phase — Harden Windows Inbox-Tool Path Resolution and Linux Uninstall Scoping

**Owner**: DSH session `session-b7bb95fc-bbc1-4f52-8dfd-3eea0fae24de`
**Branch**: `dsh/fix-inbox-tool-resolution-and-postrm`
**Accepted base**: `origin/main` head `6fdc81d0`
**Roadmap ref**: `plans/OPEN-DEFECTS.md` (SU-3-6 & SU-3-3 follow-ups)
**Effort**: 0.5 days
**Risk**: LOW
**Blast radius**: Windows service control calls (`MainWindowViewModel.Connection.cs`, `HealthCheck.cs`, `DiagnosticsExporter.cs`, `ZapretActions.cs`, `ZapretUpdater.cs`), Linux Debian post-removal script (`packaging/linux/postrm`), and contract tests in `VPNRouter.Tests`.
**Rollback**: revert branch commit; restore prior implementations

## Why

1. `SU-3-6`: elevated and privileged Windows paths previously executed bare `"sc"` or `"sc.exe"`, resolving through the current working directory or user PATH search instead of the inbox System32 binary. Additionally, commands were passed as concatenated strings rather than structured `ArgumentList` tokens.
2. `SU-3-3`: `packaging/linux/postrm` executed un-scoped `pkill -f VPNRouter.App`, risking termination of unrelated matching processes during package removal.

## What

- Use `WindowsServiceCommand.GetSystemScPath()` to resolve `%SystemRoot%\System32\sc.exe` across:
  - `VPNRouter.App/ViewModels/MainWindowViewModel.Connection.cs`
  - `VPNRouter.Core/Services/HealthCheck.cs`
  - `VPNRouter.Core/Services/Diagnostics/DiagnosticsExporter.cs`
  - `VPNRouter.Core/Services/ZapretActions.cs`
  - `VPNRouter.Core/Services/ZapretUpdater.cs`
- Tokenize arguments with `ProcessStartInfo.ArgumentList` instead of concatenated strings.
- Update `packaging/linux/postrm` to restrict `pkill -f` to `/opt/vpnrouter/VPNRouter.App` and `/usr/local/vpnrouter/VPNRouter.App`.
- Add contract tests in `VPNRouter.Tests` to verify:
  - No bare `"sc"` / `"sc.exe"` invocations remain in the audited files.
  - Linux `postrm` script scopes termination to installation paths.

## How

1. Commit phase brief and verify baseline.
2. Implement inbox-tool resolution and argument tokenization.
3. Update `postrm`.
4. Add unit contract tests.
5. Run 3-iteration verification (build/tests, Opus adversarial swarm review, GitHub Actions CI).
6. Record outcome, open PR, and squash-merge into `main`.

## Verification gate

- [ ] Gate 1 — Build clean: Release build passes with zero errors.
- [ ] Gate 2 — Tests green: all unit and characterization tests pass on Linux and Windows.
- [ ] Gate 3 — Docs: outcome recorded and OPEN-DEFECTS updated.
- [ ] Gate 4 — Adversarial review: Opus swarm review confirms no regressions or PATH leakages.
- [ ] Gate 5 — Public API surface: MainWindowViewModel public surface hash unchanged.

## Outcome

Pending execution.
