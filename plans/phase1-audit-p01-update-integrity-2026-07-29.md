# Phase 1 Audit Remediation — P01 Update Integrity

**Owner**: Qwen Code (implementation engine); orchestrator handles Git
**Branch**: `codex/qwen-audit-p01-update-integrity-2026-07-29` (off current `origin/main`)
**Audit source**: `plans/qwen-full-app-audit-2026-07-28/RESULTS.md` (PR #48)
**Adjudication**: `plans/qwen-audit-independent-verification-2026-07-28.md` (P00, commit `b39a28c3`)
**Effort**: ~2-3 h
**Risk**: MEDIUM (update path is user-facing; SHA gate re-activation must not break staging)
**Blast radius**: 2 Core product files + 1 Go file + tests + 1 optional CI step
**Rollback**: `git revert <commit>` / branch delete

## Findings in scope

| ID | Orig | P00 Verdict | Final | Confidence |
|---|---|---|---|---|
| UPD-1 | P0 | CONFIRMED | **P1** | High |
| UPD-2 | P1 | CONFIRMED | **P1** | High |

Explicitly NOT in scope: any other finding. UPD-1 was downgraded P0 → P1 by P00
(the `.sha256` sidecar shares the release TLS trust root → defense-in-depth loss,
not a takeover). Do not re-litigate severity here.

## Execution constraint (overrides methodology gates)

All implementation is performed through Qwen Code. Qwen may read/search/edit code
and write tests, but MUST NOT run local builds, tests, applications, binaries,
services, installers, package restore, VM/WinRM/ADB/MCP/live checks, downloads,
or platform mutations. Validation happens ONLY in remote GitHub CI after the
orchestrator pushes the branch. **Qwen MUST NOT commit or push** — the orchestrator
reviews the diff and handles Git.

## Why

Two distinct update-integrity defects:

- **UPD-1** — the desktop in-app update fetches the `.sha256` sidecar but discards
  it before the download gate, so the normal desktop path never hash-verifies the
  downloaded asset. This violates the `IUpdateSource.DownloadAsync` MUST-validate
  contract that the Android `SideloadSource` already honors. User impact: a desktop
  update can download/extract/apply an asset without hash verification; loss of
  defense-in-depth corruption detection (and a broken interface contract).
- **UPD-2** — the shipped `VPNRouter.GUI.exe` repair stub downloads `install.ps1`
  and dot-executes it via an inline PowerShell `-Command`, exactly the shape that
  trips Defender `Trojan:Win32/ClickFix.DCW!MTB`. The app's `SelfRepair.cs` was
  already migrated to a temp-`.ps1` + `-File` pattern; `repair.go` was not. User
  impact: the repair path can be flagged/blocked by Defender heuristics.

## Current root cause (verified against current code)

### UPD-1
- [FACT] `VPNRouter.Core/Services/UpdateSources/GitHubReleaseSource.cs` — `CheckAsync`
  fetches the `.sha256` companion, normalizes to 64 lowercase hex, and stores it
  (`AssetSha256: sha`, ~:172). `DownloadAsync` (~:184) delegates to
  `_installer.DownloadAndStageAsync(info, progress, ct)` with `info.AssetSha256` in scope.
- [FACT] `VPNRouter.Core/Services/UpdateChecker.cs` — the explicit `IDesktopInstaller`
  impl (~:99) adapts `UpdateSourceInfo` → legacy `UpdateInfo` (~:109) and sets
  `FullChecksumUrl = null` (~:119) but never carries `info.AssetSha256` into any field.
  **This adapter is the single shared root-cause point.** Legacy `DownloadAndStageAsync`
  (~:168) computes `checksumUrl = info.FullChecksumUrl` (~:173, null) and gates the SHA
  block on `if (!string.IsNullOrEmpty(checksumUrl))` (~:251) → SHA never verified. The
  size guard (~:246) and `ValidateExtractedContent` (~:335) still run.
- [FACT] Contract: `VPNRouter.Core/Services/UpdateSources/IUpdateSource.cs:59-66` —
  `DownloadAsync` MUST validate against `AssetSha256` (when non-null) before returning;
  cannot defer to caller. `AssetSha256` doc (:153-168): null → size-only fallback.
- [FACT] Android sibling validates: `SideloadSource.cs:189-205` (hash compare :198,
  delete+throw :200-205). Desktop is the outlier.
- [FACT] The legacy URL-fetch SHA path is effectively dead in production (only producer
  of `UpdateInfo` is the adapter, which sets `FullChecksumUrl = null`).

### UPD-2
- [FACT] `VPNRouter.GUI/repair.go` — `RunRepair` (~:37) builds an inline bootstrap
  (`Invoke-WebRequest … -OutFile $tmp … & $tmp`, ~:50-56) launched via
  `exec.Command("powershell.exe", …, "-Command", bootstrap)` (~:58-62).
- [FACT] Safe pattern documented+used in `VPNRouter.App/Services/SelfRepair.cs`
  (ClickFix comment ~:122-126; temp `.ps1` + `-File` ~:130-154).
- [FACT] Reachable from `VPNRouter.GUI/main.go:132` (single caller). Shipped via
  `build.ps1:201-214` (Go stub build) and `:581-586` (placed at ZIP root).

## What

### Minimal expected file list
- `VPNRouter.Core/Models/UpdateInfo.cs` — add one init property `FullChecksumSha256`.
- `VPNRouter.Core/Services/UpdateChecker.cs` — adapter threads `info.AssetSha256`;
  SHA gate prefers the inline digest, falls back to legacy URL fetch, then compares.
- `VPNRouter.GUI/repair.go` — extract pure `repairScript(prerelease)` + `repairArgs(scriptPath)`;
  write temp `.ps1`, launch with `-File`, clean up.
- `VPNRouter.Tests/UpdateCheckerChecksumTests.cs` (new).
- `VPNRouter.Tests/IUpdateSourceContractTests.cs` (add 1 end-to-end thread test).
- `VPNRouter.GUI/repair_test.go` (new).
- `.github/workflows/test.yml` — **enabling, flagged**: add a `go test ./VPNRouter.GUI/...`
  step (with SHA-pinned `actions/setup-go`) so UPD-2's regression test is CI-gated.
  Without this, the Go test runs locally only. Flag for owner decision.

### Explicit non-goals
- Do NOT change `IUpdateSource`/`IDesktopInstaller` signatures.
- Do NOT re-fetch the digest over HTTP on the desktop path (inline only).
- Do NOT touch `SideloadSource.cs`/Android updater beyond keeping tests green.
- Do NOT add code-signing/authenticode/signature verification or a new hash algorithm.
- Do NOT refactor the lite-update path or add a lite inline-digest field (no producer).
- Do NOT change the `install.ps1` download URL or the inner dot-execute behavior in repair.go.
- Do NOT alter `helper.cmd`/`ApplyUpdate` dispatch.

## How (ordered; fix each shared root cause once)

### UPD-1
1. `UpdateInfo.cs`: add `public string? FullChecksumSha256 { get; init; }` next to the
   existing checksum fields (already-normalized inline digest). No lite field needed.
2. `UpdateChecker.cs` adapter (~:109-120): set `FullChecksumSha256 = info.AssetSha256`
   in the synthesized `UpdateInfo`; update the :119 comment.
3. `UpdateChecker.cs` `DownloadAndStageAsync(UpdateInfo,…)` (~:173/:251): introduce
   `string? expectedSha = useLite ? null : info.FullChecksumSha256;`. If empty AND
   `checksumUrl` present, run the existing URL fetch+normalize into `expectedSha`
   (preserves legacy behavior; no re-fetch when inline present). If `expectedSha`
   non-empty, run the existing 64-hex validate + hash-compare + delete-on-mismatch +
   throw, operating on `zipPath` BEFORE extraction (~:290) and before `ApplyAsync`.
   Reuse the existing normalize/compare code verbatim.
4. Missing-digest policy (chosen, forced by contract + Android sibling): **fail-closed
   on mismatch; size-only skip when digest absent.** When both `AssetSha256` and
   `FullChecksumUrl` are null → no SHA gate; existing size guard remains.

### UPD-2
1. `repair.go`: extract `repairScript(prerelease bool) string` (the download+dot-execute
   logic with `\r\n` lines, `$ErrorActionPreference='Stop'`, TLS12 line; append
   ` -Prerelease` to `& $tmp` when true) and `repairArgs(scriptPath string) []string`
   (`{"-NoProfile","-WindowStyle","Hidden","-ExecutionPolicy","Bypass","-File", scriptPath}`;
   path as a single argv element → spaces preserved; NO `-Command`).
2. Rewrite `RunRepair`: write `repairScript(prerelease)` to a unique temp `.ps1`
   (`filepath.Join(os.TempDir(), fmt.Sprintf("vpnr-trampoline-repair-%d.ps1", time.Now().UnixNano()))`)
   via `os.WriteFile`; `defer os.Remove(scriptPath)`; `exec.Command("powershell.exe", repairArgs(scriptPath)...)`;
   keep existing `SysProcAttr`, `done` channel, deadline/kill logic unchanged. Add `os`, `path/filepath`.
3. `repair_test.go` (new, `package main`, table-driven like `integrity_test.go`).

## Callers / consumers to preserve

UPD-1:
- `VPNRouter.Core/Platform/PlatformServices.cs:CreateUpdateSource` (~:128, desktop branch
  `new GitHubReleaseSource(...)` ~:152) — wiring unchanged.
- `VPNRouter.App/ViewModels/UpdateNotificationViewModel.cs` — `_updateSource` set ~:117;
  download/apply ~:248 (`DownloadAsync(_pendingUpdate, …)` → `ApplyAsync`). `_pendingUpdate`
  carries `AssetSha256`. Existing `catch` (~:274) already surfaces failure.
- `VPNRouter.CLI/Commands/TestUpdateCommand.cs:185` — synthesizes `UpdateSourceInfo` with
  `AssetSha256: null` for `--staged-dir`, calls `ApplyAsync` directly (no `DownloadAsync`) — unaffected.
- `VPNRouter.Android/AndroidApp.AutoUpdate.cs:92` — uses `CreateUpdateSource` → `SideloadSource` — must stay green.
- Legacy `DownloadAndStageAsync(UpdateInfo,…)` direct callers: the adapter (`UpdateChecker.cs:139`)
  and `VPNRouter.Tests/UpdateCheckerStagingTests.cs:61-62` — preserved.

UPD-2:
- `VPNRouter.GUI/main.go:132` — single `RunRepair` caller; signature unchanged.
- `build.ps1:201-214,:581-586` — Go stub build/ship unchanged.

## Regression tests (exact)

UPD-1 — new `VPNRouter.Tests/UpdateCheckerChecksumTests.cs` (mirror `UpdateCheckerStagingTests.cs`:
real `UpdateChecker`, `FakeHttpClient.SetupStream`, `MinimalUpdateZip`; compute the zip's real SHA via `SHA256.HashData`):
- `DownloadAndStageAsync_InlineShaMatch_StagesSuccessfully` — `FullChecksumSha256 = correctSha`;
  assert staged dir exists+populated; assert NO HTTP request to any `.sha256` URL (proves no re-fetch).
- `DownloadAndStageAsync_InlineShaMismatch_RefusesAndDeletesAsset` — `FullChecksumSha256 = new string('b',64)`;
  assert `Assert.ThrowsAsync<InvalidOperationException>` (message contains "checksum mismatch");
  assert the downloaded zip no longer exists; extraction never reached.
- `DownloadAndStageAsync_MissingDigest_FallsBackToSizeOnly_Stages` — `FullChecksumSha256=null`,
  `FullChecksumUrl=null`, `SizeBytes=0`; assert stages, no throw.
- `DownloadAndStageAsync_InlineShaMalformedLength_Throws` — `FullChecksumSha256="abc"`; assert `InvalidOperationException`.

Extend `VPNRouter.Tests/IUpdateSourceContractTests.cs`:
- `GitHubReleaseSource_DownloadAsync_ShaMismatch_ThrowsBeforeApply` — construct `GitHubReleaseSource`
  with a REAL `UpdateChecker` as `IDesktopInstaller` and `FakeHttpClient.SetupStream(assetUrl, zipBytes)`;
  `UpdateSourceInfo` with `AssetSha256 = wrongSha`; assert `DownloadAsync` throws `InvalidOperationException`
  and apply is never reached. (Pins that `AssetSha256` is threaded through the adapter into the gate.)

Must stay green: `IUpdateSourceContractTests.SideloadSource_DownloadAsync_ShaMatch_ReturnsPath`,
`…_ShaMismatch_ThrowsAndDeletesFile`, `AndroidSideloadCallerTests.cs`, `UpdateCheckerStagingTests.cs`.

UPD-2 — new `VPNRouter.GUI/repair_test.go` (`package main`, table-driven):
- `TestRepairArgs_UsesFileNotCommand` — `repairArgs("C:\\x\\y.ps1")` contains `-File`, path is final element, no `-Command`.
- `TestRepairArgs_PathWithSpaces_PreservedAsSingleArg` — `C:\Users\Some User\...\vpnr.ps1` appears as exactly one element immediately after `-File`.
- `TestRepairScript_DownloadsAndDotExecutes` — `repairScript(false)` contains `Invoke-WebRequest`,
  `https://vpn.ninitux.com/install.ps1`, `-OutFile`, `& $tmp`; does NOT contain `-Prerelease`.
- `TestRepairScript_Prerelease_AddsFlag` — `repairScript(true)` contains `& $tmp -Prerelease`.

Must stay green: `VPNRouter.GUI/integrity_test.go` (all).

## Risks

- **Security**: UPD-1 restores defense-in-depth corruption detection only (sidecar shares
  release TLS trust root → no authenticity gain; correctly P1). UPD-2 reduces AMSI/ClickFix
  heuristic surface (`-File` vs inline `-Command`); same UX.
- **Compatibility**: UPD-1 `UpdateInfo.FullChecksumSha256` is additive (init-only, defaults
  null); legacy URL-fetch path preserved; `UpdateCheckerStagingTests` (`FullChecksumUrl=null`) still stages.
  UPD-2 `RunRepair` signature unchanged; temp-script cleanup best-effort (`defer os.Remove`).
- **Rollback**: both changes isolated; per-file revert. No schema/migration/state.
- **Cross-platform**: UPD-1 is cross-platform Core (Win/Mac/Linux flow through
  `UpdateChecker.DownloadAndStageAsync`); BCL `SHA256` only. UPD-2 is Windows-only Go; new pure tests are OS-agnostic (ubuntu CI).
- **CI-gate (AGENTS.md #11/#15)**: after push the orchestrator runs `tools/verify-last-commit-ci.ps1`;
  keep the `test` job green. Adding the Go step to `test.yml` must not break the existing .NET filter.

## Dependencies and file overlap with the other seven packages

- **No product-file conflict** with P02/P05/P06/P07/P08/P09/P10.
- **P07 (cli/android)**: both touch the Android *project* (P01 → `AndroidApp.AutoUpdate.cs`/`TestUpdateCommand.cs`;
  P07 → `VpnRouterService.java`). Different files; sequence to avoid concurrent edits. UPD-1 keeps `TestUpdateCommand.cs:185` working.
- **`.github/workflows/test.yml`** is a shared CI surface; only P01 edits it here (P08 edits `build-linux.yml`).
  Flag the enabling `go test` step for owner decision/ownership.
- No blocking dependency on any other package.

## Zone CLAUDE.md constraints

- `VPNRouter.Core/CLAUDE.md`: Core is a pure C# library (no UI deps); tests live as per-class files;
  `InternalsVisibleTo VPNRouter.Tests` configured. No emoji (AGENTS.md #9).
- `packaging/CLAUDE.md`: documents the `.sha256` sidecar format (`HASH` or `HASH filename`) the gate parses; no packaging change.
- `.github/workflows/CLAUDE.md`: actions pinned to full SHAs (Dependabot); job id `test` is a required check — do NOT rename; any `setup-go` addition pins a full SHA.
- `VPNRouter.App/CLAUDE.md`: the `-File` temp-script pattern + ClickFix rationale lives in `SelfRepair.cs` — UPD-2 mirrors it, does not diverge.

## Verification gate (remote-only, tailored)

- [ ] **Gate 1 — Build (remote CI only)**: orchestrator pushes branch; GitHub CI `build`/`test`
      jobs compile the solution 0 errors. Qwen does NOT build locally.
- [ ] **Gate 2 — Tests (remote CI only)**: new `UpdateCheckerChecksumTests`, the new
      `IUpdateSourceContractTests` case, and (if the enabling step is added) `VPNRouter.GUI/repair_test.go`
      run green in CI; full existing suite stays green (Android sideload + staging tests included).
- [ ] **Gate 3 — Docs**: this brief's Outcome section filled after CI; no README change expected
      (no user-facing surface change beyond repair behavior).
- [ ] **Gate 4 — Self-review**: Qwen static self-review of the diff; **security-review** of the
      SHA-gate change and the PowerShell invocation change (security-relevant). Record result in Outcome.
- [ ] **Gate 5 — UI/live**: DEFERRED by explicit owner constraint (no local launch/MCP/VM). Do NOT
      fake PASS. Note "deferred — update apply path not live-verified" in Outcome.
- [ ] **Gate 6 — Characterization**: N/A (no god-file split; no MVM surface change).

## Outcome (PENDING — fill after remote GitHub CI)

**Status**: PENDING
**Commits**: <orchestrator fills>
**Pushed**: <orchestrator fills>
**Test deltas**: +<new> / -<removed>
**Files changed**: <count> · <total LOC delta>

**Gate results:**
- [ ] Gate 1 build (remote CI): <output>
- [ ] Gate 2 tests (remote CI): <output>
- [ ] Gate 3 docs: <output>
- [ ] Gate 4 self-review / security-review: <output>
- [-] Gate 5 UI/live: deferred (owner constraint) — not live-verified
- [-] Gate 6 characterization: N/A

**Surprises encountered**: <fill>
**Follow-ups spawned**: <fill>
**Rollback**: `git revert <hash>` / branch delete
