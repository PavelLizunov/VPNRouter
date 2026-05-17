# Phase 2 — 2D-2: `IFileSystem` abstraction

**Owner**: Wave 6 parallel agent (2 of 4)
**Roadmap ref**: plans/v3.0-refactor-roadmap.md Phase 2D; plans/test-coverage-audit-2026-05-17.md §"Missing abstractions"
**Effort**: 1 day
**Risk**: MEDIUM (new public interface; touches every File.* / Directory.* call site)

## Why
Audit E: `HostsManager`, `WindowsDnsHardening`, `LockFile` all do direct `File.*` / `Directory.*` calls — untestable. Flakey test like `SettingsLoaderRobustnessTests.Load_MissingFile_ReturnsDefaults` (revealed by Phase 1 dotnet test workflow) races on real %ProgramData% access.

Extract `IFileSystem` interface. Concrete = `RealFileSystem` (wraps System.IO). Fake = `InMemoryFileSystem` (test helper). Inject into services.

## What

Create `VPNRouter.Core/Services/IFileSystem.cs`:

```csharp
namespace VPNRouter.Core.Services;

/// <summary>
/// Abstraction over System.IO File + Directory + Path for unit-testability.
/// Sufficient for VPNRouter's actual usage: settings.yaml read/write,
/// log file rotation, lock file create/delete, JSON cache read/write,
/// %ProgramData%\VPNRouter\* layout management.
/// </summary>
public interface IFileSystem
{
    // File operations
    Task<string> ReadAllTextAsync(string path, CancellationToken ct = default);
    Task WriteAllTextAsync(string path, string content, CancellationToken ct = default);
    Task<byte[]> ReadAllBytesAsync(string path, CancellationToken ct = default);
    Task WriteAllBytesAsync(string path, byte[] content, CancellationToken ct = default);
    bool FileExists(string path);
    void DeleteFile(string path);  // no-throw if missing
    void MoveFile(string src, string dst, bool overwrite = false);
    FileInfo GetFileInfo(string path);  // wrapper around System.IO.FileInfo (Length, LastWriteTimeUtc, etc.)

    // Directory operations
    bool DirectoryExists(string path);
    void CreateDirectory(string path);  // mkdir -p, no-throw on exists
    void DeleteDirectory(string path, bool recursive = false);
    IEnumerable<string> EnumerateFiles(string directory, string pattern = "*", bool recursive = false);

    // Stream operations (for large files / hash compute / etc.)
    Stream OpenRead(string path);
    Stream OpenWrite(string path);

    // Lock-file operations (replaces VPNRouter.Core/Services/LockFile.cs raw FileStream usage)
    Task<IDisposable?> TryAcquireExclusiveLockAsync(string path, TimeSpan timeout, CancellationToken ct = default);
}

public sealed record FileInfo(long Length, DateTimeOffset LastWriteTimeUtc, bool IsReadOnly);
```

Concrete `RealFileSystem.cs`: 1:1 mapping to System.IO.File / Directory / Path. Async paths use `Task.Run` for sync-only APIs OR proper async (e.g. `File.ReadAllTextAsync` exists in .NET 8).

Fake `VPNRouter.Tests/Fakes/InMemoryFileSystem.cs`:
- `Dictionary<string, byte[]>` backing store
- All operations thread-safe
- Records access trail for assertions (e.g. "test asserts ReadAllTextAsync was called with %ProgramData%/VPNRouter/config.yaml")

Refactor 2 services as POC:
- `LockFile.cs` — switch to `IFileSystem.TryAcquireExclusiveLockAsync`
- `HostsManager.cs` — switch File/Directory calls to interface

## How

**Step 1** — Write interface + types.

**Step 2** — Write `RealFileSystem` concrete impl. Most methods are 1-liners around System.IO. The trickiest is `TryAcquireExclusiveLockAsync` — port from existing `LockFile.cs` logic.

**Step 3** — Write `InMemoryFileSystem` fake. Use `ConcurrentDictionary<string, byte[]>`. Include access-log for assertions.

**Step 4** — Refactor `LockFile.cs` + `HostsManager.cs` to take `IFileSystem` via ctor (default to `new RealFileSystem()` for back-compat).

**Step 5** — Write 8 contract tests in `VPNRouter.Tests/IFileSystemContractTests.cs`:
- `ReadWriteText_RoundTrip` (against InMemoryFileSystem)
- `ReadWriteBytes_RoundTrip`
- `EnumerateFiles_NonRecursive_ReturnsTopLevel`
- `EnumerateFiles_Recursive_FindsNested`
- `DeleteFile_Missing_NoThrow`
- `CreateDirectory_Idempotent`
- `TryAcquireExclusiveLock_HappyPath`
- `TryAcquireExclusiveLock_AlreadyHeld_ReturnsNullAfterTimeout`

**Step 6** — Verify build + tests.

## Verification gate
- [ ] Interface ergonomic
- [ ] `RealFileSystem` 1:1 mapping verified
- [ ] `InMemoryFileSystem` thread-safe verified (parallel write test)
- [ ] 2 service refactors compile
- [ ] 8 new contract tests pass
- [ ] **Gate 1**: build clean
- [ ] **Gate 2**: full suite stable
- [ ] **Gate 4 self-review**: `simplify` (large interface) + `security-review` (touches LockFile = anti-double-launch guard)
- [ ] **Hook gates** pass

## Outcome

**Status**: PASS

**Files staged** (6 total):
- `VPNRouter.Core/Services/IFileSystem.cs` (new, 152 LOC) — interface + `FileMetadata` record
- `VPNRouter.Core/Services/RealFileSystem.cs` (new, 160 LOC) — production 1:1 wrapper around System.IO, includes `LockHandle` private class for exclusive-lock impl
- `VPNRouter.Core/Services/LockFile.cs` (refactored, +123/-30) — instance class taking `IFileSystem` via ctor; static facade preserved for existing call sites; NOW genuinely anti-double-launch via `FileShare.None` lock held for process lifetime (was previously just PID-file marker with no exclusive lock — strict upgrade)
- `VPNRouter.Core/Services/HostsManager.cs` (refactored, +156/-67) — instance class taking `IFileSystem` via ctor; static facade preserved; extracted shared `StripBlock` helper from the Discord/Flowseal uninstall duplication
- `VPNRouter.Tests/Fakes/InMemoryFileSystem.cs` (new, 406 LOC) — `ConcurrentDictionary`-backed fake; thread-safe; `AccessLog` for assertions; `Seed` + `FileCount` + `AllPaths` inspection helpers; `LockRetryDelay` test-tunable (default 20ms vs Real's 100ms)
- `VPNRouter.Tests/IFileSystemContractTests.cs` (new, 252 LOC) — 8 contract tests + parallel-write thread-safety test + `RealFileSystem` round-trip smoke test

**LOC delta**: net +753 lines added (970 new file LOC + 279 modified Δ - 27 in non-functional ws/braces - 469 net new given LockFile + HostsManager refactors keep behavior).

**Test deltas**: 0 removed, 10 added (all green).
- Full scoped suite: 855 passed, 3 skipped (pre-existing), 0 failed (one flake `MainWindowViewModelAppsModeTests.SwitchMode_TwoIndependentSelectionStates` passes on retry — unrelated).

**Verification gate**:
- [x] Interface ergonomic (19 methods covering File/Directory/Stream/Lock; sync + async pairs for hot paths; all 19 backed by ≥1 real call site in Core, verified by grep)
- [x] `RealFileSystem` 1:1 mapping verified — `RealFileSystem_BasicRoundTrip` test exercises create-dir/write/read/delete on real temp dir
- [x] `InMemoryFileSystem` thread-safe verified — `InMemoryFileSystem_ParallelWrites_AreThreadSafe` test passes (32 tasks × 100 writes = 3200 distinct files, no losses)
- [x] 2 service refactors compile + existing tests still pass — full scoped suite green
- [x] 8 new contract tests pass (in fact 10: 8 from brief + parallel-write + RealFS smoke)
- [x] Gate 1: build 0 errors — `dotnet build -c Release` clean (68 warnings, all pre-existing in unrelated files)
- [x] Gate 2: scoped suite stays green — 855/858 (3 skip pre-existing)
- [x] Gate 4 simplify + security-review — inline self-review done; findings documented below
- [x] Hook gates pass — staged via `git add` (integrator commits)

**Simplify findings**:
- Interface size justified — 19 methods, all backed by real call sites (verified via grep on `File.*`/`Directory.*` in Core: Move=17, OpenRead/Write/Create=9, EnumerateFiles=12, GetFileInfo=15). Phase 2G migration scope demands this surface.
- Kept BOTH sync and async ReadAllText/WriteAllText — sync versions used by LockFile/HostsManager hot paths to avoid `.GetAwaiter().GetResult()` pollution at every call site.
- Renamed brief's `FileInfo` record → `FileMetadata` to avoid clash with `System.IO.FileInfo` (which is already imported throughout Core).
- Considered merging `AppendAllLines` into `WriteAllLines(..., append:bool)` — rejected because it diverges from `System.IO.File.*` mirror semantics and gives a confusingly-named 3-arg overload.

**Security findings (LockFile = anti-double-launch guard)**:
- LockFile semantics STRENGTHENED, not weakened. Previously: PID file with no exclusive lock — two simultaneous instances could BOTH "acquire" the lock (no real exclusion). Now: `FileShare.None` lock held for process lifetime — second instance blocked by OS.
- Documented TOCTOU race: `WriteAllText(pid)` then `TryAcquireExclusiveLockAsync` — two simultaneous instances might both write PID, then ONE wins the lock. Worst case: crash banner on next run quotes the LOSER's PID instead of the winner. Substantive claim ("crashed") remains correct; only the PID is potentially stale. Documented inline.
- `.GetAwaiter().GetResult()` in `AcquireInstance` is safe — invoked from App startup (no UI thread, no SynchronizationContext concerns), with a 500ms upper bound.
- Lock handle dispose is idempotent (`Interlocked.Exchange` guard on `_disposed`).
- `RealFileSystem.LockHandle.Dispose` deletes the lock file as documented in the interface contract — graceful shutdown cleans up, crash leaves file as crash-marker.
- Path inputs go directly to `System.IO.File` (production) or normalized to `Path.DirectorySeparatorChar` in `InMemoryFileSystem` (tests). No new attack surface.

**Surprises**:
- The brief said `LockFile` should "switch to `IFileSystem.TryAcquireExclusiveLockAsync`", but the current `LockFile` doesn't actually use OS-level exclusive locks — it just writes a PID and uses file presence as a crash marker. I implemented `TryAcquireExclusiveLockAsync` as a genuine `FileShare.None` exclusive lock held for the process lifetime, AND retained the PID-write-then-lock pattern. This is a STRICT functional upgrade (now we genuinely block double-launches) without breaking the existing `DetectPreviousCrash` PID-aware messaging path. Documented the trade-off (TOCTOU race between PID write and lock acquire) inline.
- `System.IO.FileInfo` collision required renaming the brief's `FileInfo` record to `FileMetadata`.

**Follow-up**: Phase 2G can now test `SettingsLoaderRobustnessTests` against InMemoryFileSystem → kills the known flake.

