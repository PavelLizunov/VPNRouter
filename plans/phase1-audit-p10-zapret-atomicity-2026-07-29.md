# Phase 1 Audit Remediation — P10 Zapret Update Atomicity

**Owner**: Qwen Code (implementation engine); orchestrator handles Git
**Branch**: `codex/qwen-audit-p10-zapret-atomicity-2026-07-29` (off current `origin/main`)
**Audit source**: `plans/qwen-full-app-audit-2026-07-28/RESULTS.md` (PR #48)
**Adjudication**: `plans/qwen-audit-independent-verification-2026-07-28.md` (P00, commit `b39a28c3`)
**Effort**: ~1-2 h
**Risk**: LOW (isolated to the zapret update install path; fail-safe preserves prior version)
**Blast radius**: 1 Core product file (`ZapretUpdater.cs`) + tests
**Rollback**: `git revert <commit>` / branch delete

## Findings in scope

| ID | Orig | P00 Verdict | Final | Confidence |
|---|---|---|---|---|
| ZAP-1 | P1 | CONFIRMED | **P1** | High |

ONLY ZAP-1. Explicitly NOT in scope: ZAP-2 (emergency process disposal, P2),
ZAP-3 (wgturn atomic replacement, P2), PERF-1 (ETW monitor disposal, P3),
PERF-2 (free-config owned resources, REFUTED) — separate packages.

## Execution constraint (overrides methodology gates)

All implementation is performed through Qwen Code. Qwen may read/search/edit code
and write tests, but MUST NOT run local builds, tests, applications, binaries,
services, installers, package restore, VM/WinRM/ADB/MCP/live checks, downloads,
or platform mutations. Validation happens ONLY in remote GitHub CI after the
orchestrator pushes the branch. **Qwen MUST NOT commit or push** — the orchestrator
reviews the diff and handles Git.

## Why

A zapret update copies files from the extracted archive into the install
directory, but swallows per-file copy failures for locked files (e.g., an
in-use `winws.exe` or `WinDivert64.sys`). Regardless of skipped files, it then
writes `version.txt` with the new version. The result: a mixed
old-driver/new-executable installation is reported as current. Subsequent update
checks see the new version and never retry, leaving a silently-broken zapret
install that the user cannot fix by re-updating.

## Current root cause (verified against current code)

- [FACT] `VPNRouter.Core/Services/ZapretUpdater.cs:353` —
  `CopyDirectoryOverwrite(extractedRoot, ZapretDir, _logger)` inside
  `DownloadAndExtractAsync`.
- [FACT] `:619-636` — `CopyDirectoryOverwrite` implementation: iterates files,
  catches per-file exceptions (`catch (Exception ex)` at `:627`), logs
  `"Skipped locked file: {File}"` at `:631`, and CONTINUES. Returns `void`
  (no success/failure indicator).
- [FACT] `:370` — `var version = ParseVersionFromServiceBat() ?? tagName;`
- [FACT] `:371` — `try { File.WriteAllText(VersionFilePath, version); } catch { }`
  — writes the version marker REGARDLESS of skipped files.
- [FACT] `VersionFilePath` = `Path.Combine(ZapretDir, "version.txt")` (`:79`).
- [FACT] `GetLocalVersion()` (`:131`) reads `version.txt` to determine the
  installed version. Update checks compare this against the remote tag.
- [FACT] `ParseVersionFromServiceBat()` (`:380+`) parses `LOCAL_VERSION=...`
  from `service.bat` as a fallback.
- [FACT] `CopyDirectoryOverwrite` is called ONLY from `DownloadAndExtractAsync`
  (grep confirms single caller).
- [INFER] Critical lockable files: `bin/winws.exe` (the zapret engine),
  `bin/WinDivert64.sys` (kernel driver, loaded by winws). When the zapret
  service is running, these files are locked by the OS. `StopWinDivertService()`
  (`:349`) attempts to stop the service before copy, but the stop may not
  complete before the copy starts, or the driver may remain loaded.
- [INFER] The fix must gate the version marker on complete replacement. If any
  required file fails to copy, retain the prior version and allow retry.

## What

### Minimal expected file list
- `VPNRouter.Core/Services/ZapretUpdater.cs` — make `CopyDirectoryOverwrite`
  report failures; gate `version.txt` on complete copy.
- `VPNRouter.Tests/ZapretUpdaterAtomicityTests.cs` (new test class).

### Explicit non-goals
- Do NOT fix ZAP-2 (emergency process disposal, P2) — separate package.
- Do NOT fix ZAP-3 (wgturn atomic replacement, P2) — separate package.
- Do NOT fix PERF-1 (ETW monitor disposal, P3) — separate package.
- Do NOT change the download, extract, or retry logic.
- Do NOT change `StopWinDivertService` or the service stop timing.
- Do NOT add a new atomic-write helper or refactor the 5 other inline atomic
  movers in the codebase (the local fix is smallest).
- Do NOT change the `ZapretDownloadException` category enum.

## How (ordered; fix the shared root cause once)

1. Change `CopyDirectoryOverwrite` to return a failure indicator. Minimal:
   change the return type from `void` to `bool` (or `int` for skipped count).
   In the per-file catch block (`:627-631`), increment a `skippedCount` counter
   instead of silently continuing. Return `skippedCount == 0` (true = all files
   copied, false = some skipped).

2. At the call site (`:353`), capture the result:
   ```csharp
   var allCopied = CopyDirectoryOverwrite(extractedRoot, ZapretDir, _logger);
   ```

3. Gate the version marker on complete copy:
   ```csharp
   if (allCopied)
   {
       var version = ParseVersionFromServiceBat() ?? tagName;
       try { File.WriteAllText(VersionFilePath, version); } catch { }
       _logger.Information("[ZapretUpdater] Installed version {Version}", version);
       StatusChanged?.Invoke($"Installed {version}");
   }
   else
   {
       _logger.Warning("[ZapretUpdater] Some files were locked — version NOT updated. " +
           "Stop zapret/winws and re-run the update to complete installation.");
       StatusChanged?.Invoke("Partial install — some files locked. Stop zapret and retry.");
   }
   ```

4. Preserve the existing `StopWinDivertService()` call at `:349` (it runs before
   the copy; the fix does not change the stop timing). The fix ensures that if
   the stop did not release all locks, the version marker reflects the partial
   state and the next update check will retry.

Why minimal/correct: the root cause is that the version marker is written
unconditionally. Gating it on complete copy is a one-condition change. The
`CopyDirectoryOverwrite` return type change is additive (void → bool). No new
abstractions, no new dependencies. The retry path is automatic: the next
`DownloadAndExtractAsync` call sees the old version in `version.txt`, downloads
the new release again, and retries the copy (which succeeds if the service is
stopped).

## Callers / consumers to preserve

| What | Where | Note |
|---|---|---|
| `DownloadAndExtractAsync` | `ZapretUpdater.cs:155` | the fixed method; single public entry |
| `CopyDirectoryOverwrite` | `ZapretUpdater.cs:619` | changed return type; single caller |
| `GetLocalVersion` | `ZapretUpdater.cs:131` | reads `version.txt`; unchanged |
| `ParseVersionFromServiceBat` | `ZapretUpdater.cs:380+` | fallback version source; unchanged |
| `VersionFilePath` | `ZapretUpdater.cs:79` | `version.txt` path; unchanged |
| `StopWinDivertService` | `ZapretUpdater.cs:349` | pre-copy service stop; unchanged |
| `IsInstalled` | `ZapretUpdater.cs:128` | checks `winws.exe` exists; unchanged |
| GUI callers | `MainWindowViewModel.cs` (ZapretOneClickAsync) | calls `DownloadAndExtractAsync`; receives `StatusChanged` events |
| CLI callers | none (CLI does not call ZapretUpdater directly) | |
| `RemoteVersionChecker.GetLatestTagAsync` | used by MVM for update detection | reads remote tag; unchanged |

Existing helpers to reuse: `CopyDirectoryOverwrite` (modified in place);
`_logger` (existing Serilog logger); `StatusChanged` event (existing UI feedback).

## Regression tests (exact)

New `VPNRouter.Tests/ZapretUpdaterAtomicityTests.cs` — match existing test
conventions (temp directory, `InternalsVisibleTo`):

- `CopyDirectoryOverwrite_AllFilesCopied_ReturnsTrue` — create a source
  directory with 3 files. Call `CopyDirectoryOverwrite` to a temp target.
  Assert returns `true`. Assert all 3 files exist in the target.

- `CopyDirectoryOverwrite_LockedFile_ReturnsFalse` — create a source directory
  with 2 files. Lock one target file (open with `FileShare.None`). Call
  `CopyDirectoryOverwrite`. Assert returns `false`. Assert the unlocked file
  was copied; the locked file was skipped (old content preserved).

- `DownloadAndExtract_LockedRequiredFile_VersionNotUpdated` — **core ZAP-1 pin.**
  Use `FakeHttpClient` to serve a minimal ZIP containing `bin/winws.exe` +
  `service.bat`. Pre-create `ZapretDir/bin/winws.exe` locked. Call
  `DownloadAndExtractAsync`. Assert `version.txt` does NOT contain the new
  version (it retains the old value or does not exist). Assert `StatusChanged`
  fired with a "partial install" message.

- `DownloadAndExtract_AllFilesCopied_VersionUpdated` — same setup but no locked
  files. Assert `version.txt` contains the new version. Assert `StatusChanged`
  fired with "Installed".

Must stay green: existing `ZapretUpdater` tests (if any), `ZapretProbeCacheTests`
(if any).

## Risks

- **Security**: no security impact. The fix prevents a silently-broken zapret
  install (correctness/safety).
- **Compatibility**: `CopyDirectoryOverwrite` return type changes from `void`
  to `bool`. Single caller (within the same file). No external API change.
  `DownloadAndExtractAsync` public signature unchanged. `StatusChanged` event
  gains a new message string (additive).
- **Cross-platform**: `ZapretUpdater` is Windows-only (zapret/winws is a
  Windows DPI bypass tool). The `CopyDirectoryOverwrite` change is OS-agnostic
  (standard `File.Copy`). Tests use temp directories (cross-platform safe).
- **Rollback**: single-file product change; trivial revert. No schema/migration.
- **Retry behavior**: after a partial install, the next update check sees the
  old version and retries. The user can also manually stop zapret and re-run
  the update. The `StatusChanged` message guides them.

## Dependencies and file overlap with the other seven packages

- **P01-P09**: no file overlap. `ZapretUpdater.cs` is owned solely by P10.
- **P05 (DATA-1)**: P05's brief notes the atomic-write pattern in
  `ZapretProbeCache.WriteAtomic` (`:371-379`). P10 does NOT touch
  `ZapretProbeCache.cs`. No overlap.
- **ZAP-3 (P2, future)**: the wgturn atomic replacement fix will touch
  `WgturnUpdater.cs`, a different file. No overlap.
- No blocking dependency on any other package.

## Zone CLAUDE.md constraints (`VPNRouter.Core/CLAUDE.md`)

- Core is a pure C# library; `ZapretUpdater` is a Core service.
- `InternalsVisibleTo VPNRouter.Tests` configured; internal members testable.
- `ZapretProbeCache.cs` documented as having a `WriteAtomic` helper — P10 does
  NOT extract or reuse it (the fix is a conditional gate, not an atomic write).
- No emoji (AGENTS.md #9).
- `ZapretUpdater` uses `IHttpClient` seam for testability (`FakeHttpClient`).

## Verification gate (remote-only, tailored)

- [ ] **Gate 1 — Build (remote CI only)**: orchestrator pushes branch; CI compiles 0 errors. Qwen does NOT build locally.
- [ ] **Gate 2 — Tests (remote CI only)**: new `ZapretUpdaterAtomicityTests` green in CI; full existing suite stays green.
- [ ] **Gate 3 — Docs**: brief Outcome filled after CI; no README change expected.
- [ ] **Gate 4 — Self-review**: Qwen static self-review of the diff (update-atomicity change → review the gate condition and retry path).
- [ ] **Gate 5 — UI/live**: DEFERRED by explicit owner constraint (no local launch/MCP/VM). Do NOT fake PASS.
- [ ] **Gate 6 — Characterization**: N/A (no god-file split; no MVM surface change).

## Outcome

**Status**: IMPLEMENTED / REMOTE CI GREEN
**Commits**: `b6aa4cca` (fix(core): gate zapret version on complete copy)
**Pushed**: draft PR #58, branch `codex/qwen-audit-p10-zapret-atomicity-2026-07-29`
**Test deltas**: +106 / -0 (1 new test file: `ZapretUpdaterAtomicityTests.cs` +106)
**Files changed**: 2 · +135 / -8

**Gate results:**
- [x] Gate 1 build (remote CI): PASS — dotnet test run 30446264235 SUCCESS
- [x] Gate 2 tests (remote CI): PASS — run 30446264235 SUCCESS; new `ZapretUpdaterAtomicityTests` green; full existing suite stayed green
- [x] Gate 3 docs: PASS — Outcome filled; no README change needed
- [x] Gate 4 self-review: PASS — static self-review performed during implementation; gate condition and retry path reviewed
- [-] Gate 5 UI/live: deferred (owner constraint) — ProgramData/process live validation deferred
- [-] Gate 6 characterization: N/A

**Local build/test**: NOT run. The mandatory git hook attempted SDK resolution and found SDK 10.0.301 absent; this is not a pass.
**Surprises encountered**: none
**Follow-ups spawned**: none
**Rollback**: `git revert b6aa4cca` / branch delete
