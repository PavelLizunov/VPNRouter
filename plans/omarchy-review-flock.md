# Independent Security & Correctness Review: Linux TUN Ownership & flock Mutex

**Document:** `plans/omarchy-review-flock.md`  
**Date:** 2026-09-17  
**Scope:** `VPNRouter.Core/Services/LinuxTunOwnership.cs`, `VPNRouter.Core/Services/TunOwnershipLock.cs`, `VPNRouter.Headless/Lifecycle/LinuxOwnershipGuard.cs`, `VPNRouter.Headless/RouterSession.cs`, and `VPNRouter.Headless.Tests/LifecycleChecks.cs`.  
**Review Type:** Read-only Independent Security & Correctness Audit  
**Author:** DeepSeek Harness Independent Security Reviewer (Gemini 3.8 Flash High)

---

## 1. Executive Summary & Review Scope

This review evaluates the implementation of Linux TUN and `sing-box` cross-process mutual exclusion introduced to resolve defect `OMARCHY-OWNERSHIP` (`plans/OPEN-DEFECTS.md:29`). The subsystem replaces the legacy Windows named semaphore (`Global\VPNRouter-SingBox-Owner`), which previously failed open on Unix, with an advisory kernel file lock (`flock`) on a verified, owner-private runtime path.

### Verified Architecture & Trust Boundary
- **Threat Model & Trust Boundary**: Exact threat scope is **same Unix UID** cooperating processes: GUI (`VPNRouter.App`), CLI (`VPNRouter.CLI`), and Headless (`VPNRouter.Headless`). Cross-UID (multi-tenant) attack scenarios are explicitly outside the scope of user-private runtime paths (`/run/user/<uid>` or `~/.config/vpnrouter`). Because any process running under the same UID already possesses the authority to signal, ptrace, or modify user files, findings are evaluated for concurrency, deadlock/hang, denial-of-service, and state divergence between cooperating components rather than inflated to cross-boundary privilege escalations.
- **Scope Compliance**: Product changes are strictly avoided. No live agents, test runners, or product code modifications were enacted during this review.
- **Summary of Findings**: Exactly 6 source-verified findings were identified across the reviewed files, categorized from Medium to Low severity, accompanied by concrete minimal fixes.

---

## 2. Deep-Dive Architectural & Subsystem Analysis

### 2.1 Linux `statx` Binary Layout Verification (x64 / ARM64)
- **Source Under Review**: `LinuxTunOwnership.cs:406-452` (`StatxTimestamp` and `StatxStruct`).
- **Binary Compatibility Check**: Verified against the Linux kernel UAPI header `<linux/stat.h>`.
  - `sizeof(struct statx)` is exactly **256 bytes** on both x86_64 and aarch64 (ARM64).
  - Offsets: `stx_mask` (0), `stx_blksize` (4), `stx_attributes` (8), `stx_nlink` (16), `stx_uid` (20), `stx_gid` (24), `stx_mode` (28), `stx_ino` (32), `stx_atime` (64), `stx_btime` (80), `stx_ctime` (96), `stx_mtime` (112), `stx_rdev_major` (128), and spare padding (144–256).
  - Fixed-size types (`uint`, `ulong`, `ushort`, `long`) in C# map 1:1 onto `__u32`, `__u64`, `__u16`, and `__s64`.
  - `AT_FDCWD` (`-100`), `AT_SYMLINK_NOFOLLOW` (`0x100`), `AT_EMPTY_PATH` (`0x1000`), `O_NOFOLLOW` (`0x20000`), `O_CLOEXEC` (`0x80000`), and `flock` constants (`LOCK_SH=1`, `LOCK_EX=2`, `LOCK_NB=4`, `LOCK_UN=8`) are binary-identical on x86_64 and ARM64.
- **Verdict**: **Correct.** The struct layout is binary-exact across x86_64 and ARM64.

### 2.2 Fallback Lock-Path Resolution & Divergence
- **Source Under Review**: `LinuxTunOwnership.cs:262-338` (`ResolveRuntimeDirectory`).
- **Mechanism**:
  1. Primary: `/run/user/<uid>` (systemd XDG runtime dir, validated for UID and mode `0700`).
  2. Fallback: `AppPaths.DataDir` (defaults to `~/.config/vpnrouter` via `XDG_CONFIG_HOME`).
- **Failure Modes**:
  - **Per-process divergence**: `AppPaths.OverrideDataDir` (wired in `VPNRouter.Headless/Program.cs:96-99` via `--data-dir`) mutates `AppPaths.DataDir` per-process. If `/run/user/<uid>` is unavailable (e.g. headless SSH without `pam_systemd`, Docker container, WSL, or cron), Process A (GUI/CLI with default path) and Process B (Headless with `--data-dir /custom`) resolve distinct directories and lock separate files (`~/.config/vpnrouter/vpnrouter-tun.lock` vs `/custom/vpnrouter-tun.lock`), breaking mutual exclusion on the single host `VPNRouter-TUN` device.
  - **`/run/user/<uid>` Disappearance**: When a graphical session logs out under systemd without linger, `/run/user/<uid>` is unmounted. If a daemon held the lock prior to unmount, subsequent processes will fall back to `AppPaths.DataDir`, permitting a second instance to acquire the fallback lock concurrently.

### 2.3 Symlink Ancestor Handling, TOCTOU & FIFO Open Hang
- **Source Under Review**: `LinuxTunOwnership.cs:77-104` (`TryAcquire`), lines 204-259 (`ProbeOwnership`), and lines 340-377 (`TryValidateDirectory`).
- **Analysis**:
  - **FIFO Open Hang**: `ProbeOwnership()` opens existing lock files via `Open(lockPath, O_RDONLY | O_NOFOLLOW | O_CLOEXEC, 0)` without `O_NONBLOCK`. In POSIX/Linux, opening a FIFO with `O_RDONLY` without `O_NONBLOCK` blocks unconditionally in the kernel until a writer connects. If a FIFO exists at `lockPath`, the calling thread hangs indefinitely in `open()`.
  - **TOCTOU Gap**: `ProbeOwnership()` inspects `Statx(AT_FDCWD, lockPath, AT_SYMLINK_NOFOLLOW)` before `Open()`, but **does not** verify the opened file descriptor (`fstatx` / `statx(fd, "", AT_EMPTY_PATH)`) after opening. If the path is unlinked and recreated between `Statx` and `Open`, the check is bypassed.
  - **Symlink Ancestor**: `AT_SYMLINK_NOFOLLOW` only inspects the terminal directory component; intermediate path components (e.g. `~/.config`) are traversed.

### 2.4 Ownership Publication Exactness & Interaction with `runtime-owner.json`
- **Source Under Review**: `TunOwnershipLock.cs:193-277`, `LinuxOwnershipGuard.cs:57-73, 75-165`, and `ProcessOwnership.cs:388-420`.
- **Analysis**:
  - `TunOwnershipLock` runs `StartOwnerRecordMonitor` which writes `runtime-owner.json` to `AppPaths.DataDir` once the child sing-box process is detected.
  - When another instance calls `LinuxOwnershipGuard.CheckOwnership()`, Step 1 (`TunOwnershipLock.ProbeOwnership()`) returns `TunOwnershipStatus.Owned`. Because the caller is not the lock owner, it immediately returns `HeldByAnother(0, "VPNRouter (TunLock)")`.
  - Step 2 (reading `runtime-owner.json`) is **never reached**. As a result, the active owner PID and child PID are suppressed, and callers always receive an uninformative `PID 0` conflict.
  - `runtime-owner.json` is never unlinked or tombstoned on clean `Release()`.

### 2.5 Locking Instance Thread Safety, Disposal & Re-Arming
- **Source Under Review**: `LinuxTunOwnership.cs:40-43, 58-173` and `TunOwnershipLock.cs:79-191`.
- **Analysis**:
  - `LinuxTunOwnership` has **zero synchronization** (no locks or barriers).
  - In `TunOwnershipLock.cs`, while `TryAcquireExclusive()` locks `lock (InstanceGate)`, `TryAcquire()` and `Release()` do not.
  - Concurrent `Release()` or `Dispose()` calls can race on `_heldFd`, resulting in double-closing or inadvertently closing a newly opened file descriptor allocated to another thread (file descriptor recycling race).
  - **Disposal re-arming defect**: Calling `TryAcquire()` on a disposed `TunOwnershipLock` instance re-instantiates `_linuxLock` and acquires the lock, but leaves `_disposed = true`. Any subsequent call to `Dispose()` immediately early-returns (`if (_disposed) return;`), permanently leaking the file descriptor and holding the kernel flock until process termination.

### 2.6 Windows and macOS Non-Regression Verification
- **Source Under Review**: `git diff origin/main VPNRouter.Core/Services/TunOwnershipLock.cs`.
- **Analysis**:
  - Every new Linux code path is guarded behind `OperatingSystem.IsLinux()`.
  - Windows continues to use named semaphores (`Global\VPNRouter-SingBox-Owner`).
  - macOS continues to fall through to named semaphores, catching exceptions and returning fail-open, preserving existing behavior.
- **Verdict**: **Zero regressions on Windows and macOS.**

### 2.7 Separate-Process Tests, `ModuleInitializer`, and SIGTERM Preservation
- **Source Under Review**: `VPNRouter.Headless.Tests/LifecycleChecks.cs:714-728, 787-818, 893-924`.
- **Analysis**:
  - `WorkerModuleInit()` is marked with `[System.Runtime.CompilerServices.ModuleInitializer]`. In .NET 10, module initializers run immediately upon assembly load before `Main`.
  - It inspects `Environment.GetCommandLineArgs()` for `--tun-lock-worker`, invokes `RunWorkerAction()`, and exits via `Environment.Exit()`, bypassing test runner execution entirely.
  - `WorkerModuleInit` registers no signal handlers, preserving the OS default disposition for SIGTERM.
  - In `CheckSeparateProcessLockCrashAsync`, process termination is verified via `procB.Kill()`, confirming that the Linux kernel reliably and automatically releases advisory `flock` locks upon process termination.

---

## 3. Source-Verified Findings & Minimal Concrete Fixes

### Finding 1: `ProbeOwnership()` Omits `O_NONBLOCK` and Lacks `fstatx` Verification on Opened Descriptor (FIFO Hang & TOCTOU)
- **Severity**: **MEDIUM**
- **Location**: `VPNRouter.Core/Services/LinuxTunOwnership.cs:204-245`
- **Preconditions**: Same-UID environment where `vpnrouter-tun.lock` is a FIFO (named pipe) or is raced/replaced between `statx` and `open`.
- **Impact**: Opening a FIFO with `O_RDONLY` without `O_NONBLOCK` blocks synchronously in kernel `sys_openat`, causing the caller thread (e.g. GUI UI thread or Headless dispatcher) to hang permanently and uninterruptibly.
- **Minimal Concrete Fix**:
```csharp
// In LinuxTunOwnership.cs:
private const int O_NONBLOCK = 0x0800;

public static TunOwnershipStatus ProbeOwnership()
{
    var (dir, isValid, _) = ResolveRuntimeDirectory(forCreate: false);
    if (!isValid || string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        return TunOwnershipStatus.Free;

    var lockPath = Path.Combine(dir, LockFileName);
    uint currentUid = Geteuid();

    // Open existing file non-blocking with O_NOFOLLOW
    int fd = Open(lockPath, O_RDONLY | O_NONBLOCK | O_NOFOLLOW | O_CLOEXEC, 0);
    if (fd < 0)
    {
        int err = Marshal.GetLastPInvokeError();
        return err == ENOENT ? TunOwnershipStatus.Free : TunOwnershipStatus.Unavailable;
    }

    try
    {
        // Verify opened fd directly to prevent TOCTOU and reject FIFOs/symlinks
        int statRes = Statx(fd, "", AT_EMPTY_PATH, STATX_BASIC_STATS, out var statxBuf);
        if (statRes != 0 || (statxBuf.stx_mode & S_IFMT) != S_IFREG || statxBuf.stx_uid != currentUid)
            return TunOwnershipStatus.Unavailable;

        int flockRes = Flock(fd, LOCK_EX | LOCK_NB);
        if (flockRes == 0)
        {
            Flock(fd, LOCK_UN);
            return TunOwnershipStatus.Free;
        }

        int err = Marshal.GetLastPInvokeError();
        return (err == EWOULDBLOCK || err == EAGAIN) ? TunOwnershipStatus.Owned : TunOwnershipStatus.Unavailable;
    }
    finally
    {
        Close(fd);
    }
}
```

---

### Finding 2: `LinuxTunOwnership` Lacks Thread Synchronization; Concurrent `Release()` / `Dispose()` Can Cause FD Recycling Races
- **Severity**: **MEDIUM**
- **Location**: `VPNRouter.Core/Services/LinuxTunOwnership.cs:40-43, 118-128, 151-173`, `VPNRouter.Core/Services/TunOwnershipLock.cs:79-96, 140-169`
- **Preconditions**: Concurrent calls to `TryAcquire()`, `Release()`, or `Dispose()` across multiple threads in the same process.
- **Impact**: `_heldFd` check-then-close is not atomic. Thread A and Thread B can both observe `_heldFd >= 0`, leading to double-close or closing an unrelated file descriptor opened concurrently by another component. In `TunOwnershipLock.TryAcquire()`, `_linuxLock ??= new LinuxTunOwnership(...)` without synchronization can leak an instantiated lock instance and its held descriptor.
- **Minimal Concrete Fix**:
```csharp
// In LinuxTunOwnership.cs:
private readonly object _gate = new();

public bool TryAcquire()
{
    lock (_gate)
    {
        if (_owned && _heldFd >= 0) return true;
        // ... execute acquisition ...
    }
}

public void Release()
{
    lock (_gate)
    {
        if (!_owned && _heldFd < 0) return;
        try
        {
            if (_heldFd >= 0)
            {
                int fd = _heldFd;
                _heldFd = -1;
                Flock(fd, LOCK_UN);
                Close(fd);
            }
        }
        finally
        {
            _owned = false;
            _heldLockPath = null;
        }
    }
}
```

---

### Finding 3: Post-Disposal `TryAcquire()` Re-Arms `_linuxLock` on Disposed Instance, Leaking Lock FD on Subsequent `Dispose()`
- **Severity**: **LOW**
- **Location**: `VPNRouter.Core/Services/TunOwnershipLock.cs:79-86, 171-188`
- **Preconditions**: A caller retains a reference to an instantiated `TunOwnershipLock` (e.g. in test fixtures, background services, or lifecycle restarts) and invokes `TryAcquire()` after `Dispose()`.
- **Impact**: `TryAcquire()` acquires a new `_linuxLock` and opens an `flock` descriptor, but `_disposed` remains `true`. When the caller calls `Dispose()`, it early-returns (`if (_disposed) return;`) without releasing `_linuxLock`, permanently locking the TUN resource for the remaining lifetime of the process.
- **Minimal Concrete Fix**:
```csharp
// In TunOwnershipLock.cs:
public bool TryAcquire()
{
    ObjectDisposedException.ThrowIf(_disposed, this);
    if (_owned) return true;
    // ... continue acquisition ...
}
```

---

### Finding 4: Fallback Directory Divergence Under Per-Process `AppPaths.OverrideDataDir` When `/run/user/<uid>` is Unavailable
- **Severity**: **LOW**
- **Location**: `VPNRouter.Core/Services/LinuxTunOwnership.cs:306-338`, `VPNRouter.Headless/Program.cs:96-99`
- **Preconditions**: Non-systemd environment (or missing `/run/user/<uid>`) where `VPNRouter.Headless` is invoked with `--data-dir <path>`.
- **Impact**: `ResolveRuntimeDirectory` falls back to `AppPaths.DataDir`. Process A (GUI/CLI with default path) resolves `~/.config/vpnrouter/vpnrouter-tun.lock`, while Process B (Headless with custom `--data-dir`) resolves `<custom>/vpnrouter-tun.lock`. Both acquire locks independently and attempt concurrent access to the host `VPNRouter-TUN` device.
- **Minimal Concrete Fix**:
```csharp
// In LinuxTunOwnership.cs ResolveRuntimeDirectory:
// When /run/user/<uid> is unavailable, anchor fallback to the canonical default directory
// rather than per-process AppPaths.DataDir overrides:
var canonicalFallbackDir = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
    ".config",
    "vpnrouter");
```

---

### Finding 5: Uncreated Fallback Directory Probe Fails Due to Group/World Permissions on `~/.config` (Mode 0755)
- **Severity**: **LOW**
- **Location**: `VPNRouter.Core/Services/LinuxTunOwnership.cs:330-337, 369-373`
- **Preconditions**: Fresh installation where `AppPaths.DataDir` does not exist yet and `/run/user/<uid>` is absent.
- **Impact**: `ResolveRuntimeDirectory(forCreate: false)` validates `parent = Path.GetDirectoryName(appDataDir)` (`~/.config`). `TryValidateDirectory` checks `(statxBuf.stx_mode & 0077) != 0` and rejects `~/.config` because its standard distribution permissions are `0755`. As a result, `ProbeOwnership()` fails closed and returns `TunOwnershipStatus.Unavailable` instead of `TunOwnershipStatus.Free`.
- **Minimal Concrete Fix**:
```csharp
// In LinuxTunOwnership.cs TryValidateDirectory:
// Add an allowGroupOtherRead parameter for parent directory validation:
private static bool TryValidateDirectory(string dirPath, uint expectedUid, bool allowGroupOtherRead, out string? failureReason)
{
    // ...
    uint mask = allowGroupOtherRead ? 0022u : 0077u; // Only disallow group/world write if read is allowed
    if ((statxBuf.stx_mode & mask) != 0)
    {
        failureReason = $"Directory permissions are not private (mode 0{Convert.ToString(statxBuf.stx_mode & 0777, 8)})";
        return false;
    }
    // ...
}
```

---

### Finding 6: `LinuxOwnershipGuard` Suppresses `runtime-owner.json` PID Publication During Lock Contention (Returns Generic PID 0)
- **Severity**: **LOW**
- **Location**: `VPNRouter.Headless/Lifecycle/LinuxOwnershipGuard.cs:57-67, 74-165`
- **Preconditions**: Another VPNRouter instance currently holds the TUN `flock`.
- **Impact**: Step 1 detects `lockStatus == TunOwnershipStatus.Owned` and immediately returns `OwnershipCheckResult.HeldByAnother(0, "VPNRouter (TunLock)")`. It never inspects `runtime-owner.json` (Step 2) to extract the active owner PID or sing-box child PID. Callers and error logs receive an unhelpful `PID 0` conflict.
- **Minimal Concrete Fix**:
```csharp
// In LinuxOwnershipGuard.cs:
var lockStatus = TunOwnershipLock.ProbeOwnership();
if (lockStatus == TunOwnershipStatus.Owned)
{
    var isOwnedBySelf = TunOwnershipLock.Instance().HasOwnership;
    if (!isOwnedBySelf)
    {
        // Enrich conflict details with durable record if available
        var ownerRead = ProcessOwnership.ReadRuntimeOwnerRecord(Path.Combine(AppPaths.DataDir, RuntimeOwnerFileName));
        int conflictingPid = 0;
        string procName = "VPNRouter (TunLock)";
        if (ownerRead.Kind == RuntimeOwnerRecordKind.CurrentV2 && ownerRead.Record is { } v2 && v2.OwnerPid > 0)
        {
            conflictingPid = v2.OwnerPid;
            procName = "VPNRouter";
        }
        logger?.Information("[OwnershipGuard] TunOwnershipLock is owned by PID {Pid}", conflictingPid);
        return OwnershipCheckResult.HeldByAnother(conflictingPid, procName, "TunOwnershipLock is owned by another process.");
    }
}
```

---

## 4. Summary Matrix

| Finding ID | Severity | Component | Flaw Summary | Minimal Fix Summary |
|---|---|---|---|---|
| **F-01** | Medium | `LinuxTunOwnership.cs` | `ProbeOwnership()` omits `O_NONBLOCK` on `Open`, causing FIFO hang; lacks `fstatx` on fd. | Add `O_NONBLOCK`; perform `Statx` on opened fd. |
| **F-02** | Medium | `LinuxTunOwnership.cs` | Missing thread synchronization; concurrent `Release`/`Dispose` can close recycled fds. | Add instance gate lock; atomically clear `_heldFd` before closing. |
| **F-03** | Low | `TunOwnershipLock.cs` | Post-disposal `TryAcquire` re-arms `_linuxLock`, leaking fd on subsequent `Dispose`. | Guard `TryAcquire` with `ObjectDisposedException.ThrowIf`. |
| **F-04** | Low | `LinuxTunOwnership.cs` | Fallback directory diverges with per-process `AppPaths.OverrideDataDir` when `/run/user` is missing. | Anchor fallback to canonical user config path. |
| **F-05** | Low | `LinuxTunOwnership.cs` | `ProbeOwnership` on uncreated directory rejects `~/.config` due to mode `0755`. | Relax parent directory validation to disallow group/world write only (`0022`). |
| **F-06** | Low | `LinuxOwnershipGuard.cs` | Early return on held lock suppresses active owner PID from `runtime-owner.json` (returns PID 0). | Read `runtime-owner.json` to enrich `HeldByAnother` payload with owner PID. |

---

## 5. Pre-Delivery Verification & Checklist

- [x] Read-only audit conducted: no product source files modified.
- [x] Exact threat model honored: same-UID cooperating processes; no cross-UID privilege escalation inflation.
- [x] Linux `statx` struct layout and constants verified against Linux kernel headers on x86_64 and ARM64.
- [x] Windows and macOS code paths verified free of behavioral regressions or changes.
- [x] Separate-process test invocation verified: `[ModuleInitializer]` executes prior to `Main`, preserves SIGTERM, and handles exits cleanly.
- [x] Exactly 6 source-verified findings persisted with concrete minimal fixes to `plans/omarchy-review-flock.md`.
