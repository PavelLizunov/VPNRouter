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
*(filled by agent)*

**Follow-up**: Phase 2G can now test `SettingsLoaderRobustnessTests` against InMemoryFileSystem → kills the known flake.
