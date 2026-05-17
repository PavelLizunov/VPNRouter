#nullable enable
using System.Diagnostics;
using Serilog;

namespace VPNRouter.Core.Services;

/// <summary>
/// Anti-double-launch + crash-detection guard. At startup:
/// <list type="number">
/// <item>Writes PID + timestamp into <c>running.lock</c> under
///   <see cref="AppPaths.DataDir"/>.</item>
/// <item>Calls <see cref="IFileSystem.TryAcquireExclusiveLockAsync"/> on
///   the same path and holds the handle for the process lifetime so
///   another instance cannot start while we run.</item>
/// </list>
/// On graceful shutdown the handle is disposed (releases the OS lock and
/// deletes the file). If we crash, the OS releases the lock automatically
/// but the file stays on disk with the PID payload —
/// <see cref="DetectPreviousCrash"/> reads it on the next run to surface
/// a "previous run did not shut down cleanly" banner.
///
/// <para>
/// v2.23.0 self-healing. Phase 2D (v3.0) refactored to take
/// <see cref="IFileSystem"/> via ctor for testability while keeping the
/// static call sites untouched via the <see cref="DefaultInstance"/>
/// singleton. The historical "write-file-only" path now also holds a
/// real <see cref="FileShare.None"/> lock for the process lifetime.
/// </para>
/// </summary>
public sealed class LockFile
{
    /// <summary>How long to wait for the lock before giving up.</summary>
    private static readonly TimeSpan AcquireTimeout = TimeSpan.FromMilliseconds(500);

    private readonly IFileSystem _fs;
    private readonly string _lockPath;
    private readonly object _gate = new();
    private IDisposable? _heldLock;

    /// <summary>
    /// Default singleton wired to <see cref="RealFileSystem"/> and the
    /// production lock path. Used by the static facade methods so that
    /// existing call sites continue to work without modification.
    /// </summary>
    private static readonly LockFile DefaultInstance = new(new RealFileSystem(), DefaultLockPath());

    private static string DefaultLockPath() => Path.Combine(AppPaths.DataDir, "running.lock");

    /// <summary>
    /// Construct a <see cref="LockFile"/> backed by the supplied
    /// <see cref="IFileSystem"/>. Tests use this with
    /// <c>InMemoryFileSystem</c>; production code typically uses the
    /// static facade methods which dispatch to <see cref="DefaultInstance"/>.
    /// </summary>
    public LockFile(IFileSystem? fileSystem = null, string? lockPath = null)
    {
        _fs = fileSystem ?? new RealFileSystem();
        _lockPath = lockPath ?? DefaultLockPath();
    }

    /// <summary>
    /// Write current process PID into the lock file, then acquire an
    /// exclusive OS lock on it for the process lifetime. Best-effort —
    /// if either step fails we log a warning but do not throw, because
    /// the caller wants to start regardless and the crash-detection
    /// banner is a diagnostic, not a hard guarantee. Instance variant.
    ///
    /// <para>
    /// Order matters: we write the PID payload BEFORE acquiring the
    /// lock. After the exclusive open, the file remains open with
    /// <see cref="FileShare.None"/>; any subsequent
    /// <see cref="IFileSystem.WriteAllText"/> on the same path would
    /// fail. The PID-then-lock order keeps the payload available for
    /// the next run's crash detection while still giving real
    /// anti-double-launch semantics.
    /// </para>
    /// </summary>
    public void AcquireInstance(ILogger? logger = null)
    {
        try
        {
            lock (_gate)
            {
                // Already acquired by this instance? No-op (idempotent
                // so repeated calls during a noisy startup don't churn).
                if (_heldLock != null) return;
            }

            var dir = Path.GetDirectoryName(_lockPath);
            if (!string.IsNullOrEmpty(dir))
                _fs.CreateDirectory(dir);

            // 1) Write PID payload first — readable by the NEXT run if
            //    we crash before deleting it. (If a previous run left a
            //    stale file behind, DetectPreviousCrash should have
            //    already consumed it; if not, this overwrites cleanly.)
            //    Race-aware: two simultaneous instances might both
            //    WriteAllText (last-write-wins), then both attempt the
            //    lock — only one wins. The losing instance logs a
            //    warning and proceeds (it shouldn't have, but we don't
            //    abort startup). Worst-case downstream effect: the
            //    crash banner on a subsequent run might reference the
            //    losing PID instead of the winner. The "crashed" claim
            //    is still correct; only the PID is potentially stale.
            _fs.WriteAllText(_lockPath,
                $"{Environment.ProcessId}\n{DateTime.UtcNow:o}\n{Environment.ProcessPath}\n");

            // 2) Now acquire the exclusive lock so any second instance
            //    is blocked. .GetAwaiter().GetResult() is safe here:
            //    startup is single-threaded and the timeout is small.
            var handle = _fs.TryAcquireExclusiveLockAsync(_lockPath, AcquireTimeout)
                .GetAwaiter().GetResult();

            if (handle == null)
            {
                logger?.Warning("[LockFile] Could not acquire exclusive lock on {Path} within {Timeout} — another instance may be running",
                    _lockPath, AcquireTimeout);
                return;
            }

            lock (_gate)
            {
                _heldLock = handle;
            }
        }
        catch (Exception ex)
        {
            logger?.Warning(ex, "[LockFile] Could not write {Path}", _lockPath);
        }
    }

    /// <summary>
    /// Release the held exclusive lock (which also deletes the file via
    /// the handle's Dispose). Instance variant. Silent on failure —
    /// shutdown path should never bubble.
    /// </summary>
    public void ReleaseInstance(ILogger? logger = null)
    {
        IDisposable? toDispose;
        lock (_gate)
        {
            toDispose = _heldLock;
            _heldLock = null;
        }
        try
        {
            toDispose?.Dispose();
            // Defensive: if the abstraction's Dispose didn't delete the
            // file (different IFileSystem impl, or no lock was held in
            // the first place), nudge it.
            if (_fs.FileExists(_lockPath))
                _fs.DeleteFile(_lockPath);
        }
        catch (Exception ex)
        {
            logger?.Debug(ex, "[LockFile] Could not delete {Path}", _lockPath);
        }
    }

    /// <summary>
    /// Returns a human-readable summary if the previous session crashed
    /// (lock file exists but its PID is no longer alive, or the PID
    /// belongs to an unrelated process), otherwise null.
    /// Consumes (deletes) the stale lock file either way — leaving it
    /// around would surface the same warning on every subsequent run.
    /// Instance variant.
    /// </summary>
    public string? DetectPreviousCrashInstance(ILogger? logger = null)
    {
        try
        {
            if (!_fs.FileExists(_lockPath))
                return null;

            var contents = _fs.ReadAllText(_lockPath).Split('\n',
                StringSplitOptions.RemoveEmptyEntries);
            TryDeleteInstance(logger);

            if (contents.Length < 1 || !int.TryParse(contents[0].Trim(), out var pid))
            {
                return "Previous run did not shut down cleanly (lock file unreadable). Check logs for details.";
            }

            // If a process with that PID is still alive, either another
            // instance is running (user opened twice) or the PID has been
            // recycled. Either way we don't have strong evidence of a
            // crash — stay silent.
            Process? proc = null;
            try { proc = Process.GetProcessById(pid); } catch { /* dead */ }

            if (proc == null)
            {
                var timestamp = contents.Length >= 2 ? contents[1].Trim() : "unknown time";
                return $"Previous run (PID {pid}, started {timestamp}) did not shut down cleanly. " +
                       "Check logs for details; consider running with --safe if the app keeps crashing.";
            }

            // Alive — maybe another instance, maybe zombie. Either way
            // our fresh Acquire will overwrite the lock. No warning.
            try { proc.Dispose(); } catch { }
            return null;
        }
        catch (Exception ex)
        {
            logger?.Debug(ex, "[LockFile] DetectPreviousCrash error");
            return null;
        }
    }

    private void TryDeleteInstance(ILogger? logger)
    {
        try { _fs.DeleteFile(_lockPath); }
        catch (Exception ex) { logger?.Debug(ex, "[LockFile] Could not delete stale lock {Path}", _lockPath); }
    }

    // ── Static facade (backwards compatibility) ──

    /// <summary>
    /// Write current process PID into the lock file. Safe to call from
    /// app startup after <see cref="AppPaths.EnsureDirectories"/>. Silent
    /// if the write fails — we'd rather miss a crash detection than
    /// abort startup over a best-effort diagnostic file.
    /// </summary>
    public static void Acquire(ILogger? logger = null) => DefaultInstance.AcquireInstance(logger);

    /// <summary>
    /// Delete the lock file. Call on graceful shutdown (normal quit or
    /// clean Environment.Exit via the update flow). Silent on failure.
    /// </summary>
    public static void Release(ILogger? logger = null) => DefaultInstance.ReleaseInstance(logger);

    /// <summary>
    /// Returns a human-readable summary if the previous session crashed,
    /// otherwise null. Consumes (deletes) the stale lock file either way.
    /// </summary>
    public static string? DetectPreviousCrash(ILogger? logger = null)
        => DefaultInstance.DetectPreviousCrashInstance(logger);
}
