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

- [x] Gate 1 — Build clean: Release build and CLI publish pass with zero errors.
- [x] Gate 2 — Tests green: all unit and characterization tests pass on Linux and Windows (2,904 tests passed, 0 failed, 0 errors, 0 warnings).
- [x] Gate 3 — Docs: outcome recorded and OPEN-DEFECTS updated.
- [x] Gate 4 — Adversarial review: Opus swarm review confirmed all bare `sc`/`sc.exe` invocations replaced, `ArgumentList` tokenization verified, and `postrm` scoped to `/opt/vpnrouter/` and `/usr/local/vpnrouter/`.
- [x] Gate 5 — Public API surface: MainWindowViewModel public surface hash unchanged.

## Outcome

**Status**: READY FOR OWNER REVIEW / MERGE — PR #224
**Commits**: `cdedb5bd` (brief); `5c0fe2a8` (implementation & tests); pending follow-up commit (adversarial test hardening)
**Pushed**: `origin/dsh/fix-inbox-tool-resolution-and-postrm`; PR #224 — https://github.com/PavelLizunov/VPNRouter/pull/224
**Files changed**:
- `VPNRouter.App/ViewModels/MainWindowViewModel.Connection.cs`: replaced bare `"sc.exe"` with `WindowsServiceCommand.GetSystemScPath()` and structured `ArgumentList` for SCM stop calls.
- `VPNRouter.Core/Services/HealthCheck.cs`: replaced `"sc.exe"` with `WindowsServiceCommand.GetSystemScPath()` and structured `ArgumentList` in SCM queries.
- `VPNRouter.Core/Services/Diagnostics/DiagnosticsExporter.cs`: routed all Windows SCM diagnostic queries through `WindowsServiceCommand.GetSystemScPath()`.
- `VPNRouter.Core/Services/ZapretActions.cs`: resolved `ScExecutablePath` via `WindowsServiceCommand.GetSystemScPath()` on Windows.
- `VPNRouter.Core/Services/ZapretUpdater.cs`: replaced bare `"sc"` and string interpolation with `WindowsServiceCommand.GetSystemScPath()` and `ArgumentList`.
- `packaging/linux/postrm`: scoped `pkill -f` to `/opt/vpnrouter/` and `/usr/local/vpnrouter/` for both `sing-box` and `VPNRouter.App`.
- `VPNRouter.Tests/ReleaseToolingContractTests.cs`: added `LinuxPostrm_ScopesProcessTermination_NoBarePkill`.
- `VPNRouter.Tests/WindowsServiceCommandTests.cs`: added `GetSystemScPath_ResolvesSystem32OrThrowsOnNonWindows` and `WindowsInboxTool_AuditedSourcesUseSystemScPath_NoBareSc`.
- `plans/OPEN-DEFECTS.md`: updated SU-3-6 and SU-3-3 follow-ups to RESOLVED in PR #224.

**Gate results**: All 5 verification gates passed cleanly in workflow `33761230101`. All 2,904 tests passed.
