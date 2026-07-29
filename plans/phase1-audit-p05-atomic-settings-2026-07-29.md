# Phase 1 Audit Remediation — P05 Atomic Settings Persistence

**Owner**: Qwen Code (implementation engine); orchestrator handles Git
**Branch**: `codex/qwen-audit-p05-atomic-settings-2026-07-29` (off current `origin/main`)
**Audit source**: `plans/qwen-full-app-audit-2026-07-28/RESULTS.md` (PR #48)
**Adjudication**: `plans/qwen-audit-independent-verification-2026-07-28.md` (P00, commit `b39a28c3`)
**Effort**: ~1-2 h
**Risk**: MEDIUM (persistence path used by every save; must not lose data or break watchers)
**Blast radius**: 1 Core product file (`SettingsLoader.cs`) + 1 test file
**Rollback**: `git revert <commit>` / branch delete

## Findings in scope

| ID | Orig | P00 Verdict | Final | Confidence |
|---|---|---|---|---|
| DATA-1 | P1 | CONFIRMED | **P1** | High |

ONLY DATA-1. Explicitly NOT in scope: DATA-3 (MTU migration, P2), DATA-4 (free-config
dedupe, P2), DATA-6 (`FreeConfigCache` delete-then-move, P2) — separate packages.

## Execution constraint (overrides methodology gates)

All implementation is performed through Qwen Code. Qwen may read/search/edit code
and write tests, but MUST NOT run local builds, tests, applications, binaries,
services, installers, package restore, VM/WinRM/ADB/MCP/live checks, downloads,
or platform mutations. Validation happens ONLY in remote GitHub CI after the
orchestrator pushes the branch. **Qwen MUST NOT commit or push** — the orchestrator
reviews the diff and handles Git.

## Why

`SettingsLoader.Save` writes `config.yaml` with `File.WriteAllText`, which truncates
the destination on open then writes. A crash or power loss between truncate and
write-complete of a POPULATED `config.yaml` leaves a zero-length/partial file → loss
of ALL settings including VLESS credentials, subscriptions, and CustomConfigs. There
is no temp+flush+rename. (The secondary "defaults written over zero-length file"
sub-claim is NOT a data-loss vector — those paths are backup-guarded and the source
carries no data.)

## Current root cause (verified against current code)

- [FACT] `VPNRouter.Core/Services/SettingsLoader.cs:509` — `internal static void Save(AppSettings settings, string? path = null)`.
- [FACT] `:536` — `File.WriteAllText(configPath, serializer.Serialize(settings));` (truncate-then-write).
- [INFER] `serializer.Serialize(settings)` evaluates to a full in-memory string BEFORE `WriteAllText`
  touches disk, so a mid-serialize throw happens pre-truncate (not a loss window). The only loss window
  is the truncate→write-complete gap inside `WriteAllText`. The fix needs only atomic replace.
- [FACT] No atomic helper / `File.Replace` in the file; the only `File.Move` calls are backup renames
  at :180 (`.unloadable-{ts}`) and :217 (`.invalid-{ts}`), unrelated to the write path.
- [FACT] Correct atomic-replace pattern already exists inline at `FreeConfigPoolFetcher.cs:125,141`
  (`var tmp = _cachePath + ".tmp"; … File.Move(tmp, _cachePath, overwrite: true);` with a `finally`
  best-effort tmp cleanup :148-150). Same idiom duplicated in `ServerHealthStore.cs:152`,
  `ZapretProbeCache.cs:371-379` (private `WriteAtomic`), `RemoteVersionChecker.cs:205`, `ProcessOwnership.cs:361`.
- [FACT] There is NO shared atomic-write helper (`ZapretProbeCache.WriteAtomic` is `private static`).

## What

### Minimal expected file list
- `VPNRouter.Core/Services/SettingsLoader.cs` — only the `Save` body (:509-537).
- `VPNRouter.Tests/SettingsLoaderAtomicSaveTests.cs` (new test class).

### Explicit non-goals
- Do NOT fix DATA-6 (`FreeConfigCache.cs:128-132` delete-then-move) — separate P2 package.
- Do NOT touch DATA-3 / DATA-4 (P2, separate packages).
- Do NOT extract a shared atomic-write helper / refactor the 5 other inline atomic movers
  (would balloon scope; the local idiom is the smallest fix).
- Do NOT alter Load-path backup logic (`.unloadable-*`/`.invalid-*`), the "defaults over zero-length"
  behavior, serializer config, or `SafeMode` handling.

## How (ordered; fix the shared root cause once)

Per guidance, the minimal fix is a LOCAL temp+flush+rename inside `Save`, mirroring the
`FreeConfigPoolFetcher`/`ZapretProbeCache` idiom. Replace :536 with:
1. `var tmp = configPath + ".tmp";` (sibling — same `DataDir`, guaranteed same volume →
   rename is atomic; `ConfigYamlPath = Path.Combine(DataDir, "config.yaml")`, `AppPaths.cs:45`).
2. Write via `FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None)`, write the
   serialized bytes, then `Flush(flushToDisk: true)` for durability before rename (survives power
   loss of the OS write cache; stronger than `WriteAllText`).
3. `File.Move(tmp, configPath, overwrite: true)` — atomic replace.
4. `finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } }` — best-effort leftover
   cleanup (matches `FreeConfigPoolFetcher.cs:148-150`).

Preserve `Directory.CreateDirectory(Path.GetDirectoryName(configPath)!)` (:521) and the
`SafeMode.Enabled` early-return (:518). Preserve all backup behavior (:180,:217) — untouched.

Cross-platform atomic-rename: `File.Move(tmp, path, overwrite:true)` maps to `MoveFileEx`/`ReplaceFile`
on Windows (NTFS atomic in-place replace) and `rename(2)` on POSIX (atomic directory-entry swap). The
`ZapretProbeCache.cs:374-378` comment already documents this rationale. Same-directory temp is required
(cross-FS `rename` would fail with EXDEV on Linux/macOS).

## Callers / consumers to preserve

Single choke point: every production writer routes through `ISettingsStore.Save` → `RealSettingsStore.Save`
(`ISettingsStore.cs:127-128`) → `SettingsLoader.Save`. Production callers:
- `VPNRouter.App/ViewModels/MainWindowViewModel.cs:3005` (best-effort `try{Save}catch{}`), `:3973,:3980` (`SaveSettings`).
- `VPNRouter.App/ViewModels/FreeConfigs/FreeConfigsPageViewModel.cs:1529,:1548`.
- `VPNRouter.Core/Services/AutoFailoverEngine.cs:13,39` (failover persists via `_store`).
- `VPNRouter.Core/Services/StartupPipeline.cs:301`.
- `VPNRouter.Service/VPNRouterService.cs:29`.
- CLI commands (`StartCommand.cs`, `ProfilesCommand.cs`) via injected `ISettingsStore`.

In-file internal callers of `Save` (all benefit automatically; none rely on partial-write semantics):
`:227` validation-fail reset; `:251` clash_api secret persistence; `:450` schema-migration re-save;
`:472` placeholder-prune re-save; `:653` `ResetToDefaults`; `:755` `WriteExample`.

[FACT] `SettingsLoader.Save` is the ONLY production writer of the settings YAML. Other `File.WriteAllText`
hits write sing-box runtime JSON, not `config.yaml`. No bypass exists. [INFER] All callers treat Save as
all-or-nothing best-effort; atomic replace preserves the observable contract (success → new content; failure → old content survives).

File-watcher: `StartWatching` (:584+) reacts to `Changed/Created/Renamed`; atomic rename fires `Renamed`
(already handled), debounce (:629, 2s) absorbs it. [INFER] Service hot-reload now sees one clean `Renamed`
event with a complete file (improvement).

## Regression tests (exact)

New `VPNRouter.Tests/SettingsLoaderAtomicSaveTests.cs` — match `SettingsLoaderRobustnessTests.cs` conventions:
`[Collection(SafeModeStateCollection.Name)]`, `IDisposable` with unique temp dir, `PathFor(filename)` helper,
call `SettingsLoader.Save/Load` directly (internal static via `InternalsVisibleTo`). Round-trip precedent:
`Save_ThenLoad_PersistsExcludedApps` (:451-471).

- `Save_ThenLoad_RoundTripsIdenticalSettings` — build populated `AppSettings` (Vless.Servers entry, a
  subscription, ExcludedApps); `Save(s, path); var reloaded = Load(path);` assert server count/fields,
  subscription count, ExcludedApps equal originals. Pins the happy path after the rewrite.
- `Save_InterruptedWrite_LeavesPreviousConfigIntact` — **core DATA-1 pin.** `Save(goodSettings, path)`;
  capture `originalBytes = File.ReadAllBytes(path)`. Simulate an interrupted save by making the target
  directory read-only / target file locked so the temp write or `File.Move` throws; wrap the failing `Save`
  in `Assert.ThrowsAny<Exception>` (or `Record.Exception`). Assert `File.ReadAllBytes(path)` equals
  `originalBytes` byte-for-byte and `Load(path)` returns the good settings.
- `Save_NoTempLeftover_OnSuccess` — `Save(s, path)`; assert `!File.Exists(path + ".tmp")` (pins the `finally`
  cleanup; mirrors `RuleSetCacheManagerTests.cs:205`).

Implementer note: a deterministic "throw mid-write" is hard to inject without a seam because `Serialize`
precedes I/O; the read-only-dir / locked-target approach exercises the real failure path and is the
highest-fidelity test available without adding a product abstraction (deliberately avoided). If read-only-dir
is non-throwing on a given OS, fall back to asserting original bytes survive any `Save` that throws.

## Risks

- **Cross-platform atomic rename**: safe on NTFS and POSIX; requires same-directory temp (satisfied).
  [INFER] If `DataDir` is ever a symlink/bind-mount spanning filesystems, POSIX `rename` could `EXDEV`;
  acceptable — `DataDir` is a real directory, and the failure mode (throw, old file intact) is strictly safer than today.
- **File permissions / ACL interaction with P09 SEC-2**: P09 plans to ACL-restrict the data dir. A freshly
  created `config.yaml.tmp` inherits the parent-directory ACL on Windows; `File.Move(overwrite:true)` preserves
  the TARGET's existing ACL. **Coordinate**: confirm with P09 that the temp file needs no explicit restrictive ACL
  before rename (Windows inherited dir ACL covers it; POSIX gets default umask, rename keeps target mode). P09's
  design grants the current user Modify (see P09 brief) so the temp create + rename works.
- **Compatibility**: `File.Move(tmp, path, overwrite:true)` requires .NET Core 3.0+; project is .NET 10 — fine.
  `FileStream.Flush(true)` is cross-platform.
- **Rollback**: isolated to one method body; revert restores `File.WriteAllText`. No schema/migration/wire-format change.

## Dependencies and file overlap with the other seven packages

- **`SettingsLoader.cs`** owned solely by P05 here.
- **P06 (FLOW-1)** also calls `SaveSettings` → `ISettingsStore.Save` → this method; P05 changes the IMPLEMENTATION
  not the signature/contract, so P06 is unaffected and benefits. Sequence-independent. (P06 edits `SimpleMode.cs`, a different file.)
- **P09 SEC-2 (data-dir ACLs)** — coordinate per Risks. P05's same-dir temp + target-preserving rename is designed
  to stay compliant; P09 grants the user Modify. Verify ordering with P09.
- **Test project**: new file only; uses existing `InternalsVisibleTo` access to `internal static Save`.
- No other overlap.

## Zone CLAUDE.md constraints (`VPNRouter.Core/CLAUDE.md`)

- Pure C# business-logic library, no UI; consumed by App/CLI/Service/Android — fix must stay platform-neutral
  (no Windows-only P/Invoke). The `FileStream`+`File.Move` approach is fully managed/cross-platform.
- `SettingsLoader.cs` documented as "YAML load/save через YamlDotNet. Auto-create defaults." — atomic save is in-zone.
- `InternalsVisibleTo VPNRouter.Tests` configured — internal `Save` testable directly.
- No emoji (AGENTS.md #9). Keep tests deterministic and parallel-safe (unique temp dir per case;
  `[Collection(SafeModeStateCollection.Name)]` to avoid the documented SafeMode-flip flake).

## Verification gate (remote-only, tailored)

- [ ] **Gate 1 — Build (remote CI only)**: orchestrator pushes branch; CI compiles 0 errors. Qwen does NOT build locally.
- [ ] **Gate 2 — Tests (remote CI only)**: new `SettingsLoaderAtomicSaveTests` green in CI; full existing suite stays green.
- [ ] **Gate 3 — Docs**: brief Outcome filled after CI; no README change expected.
- [ ] **Gate 4 — Self-review**: Qwen static self-review of the diff (persistence/file-IO change → review durability + cleanup).
- [ ] **Gate 5 — UI/live**: DEFERRED by explicit owner constraint (no local launch/MCP/VM). Do NOT fake PASS.
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
- [ ] Gate 4 self-review: <output>
- [-] Gate 5 UI/live: deferred (owner constraint) — not live-verified
- [-] Gate 6 characterization: N/A

**Surprises encountered**: <fill>
**Follow-ups spawned**: <fill>
**Rollback**: `git revert <hash>` / branch delete
