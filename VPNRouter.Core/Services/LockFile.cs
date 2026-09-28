#nullable enable
using System.Diagnostics;
using Serilog;

namespace VPNRouter.Core.Services;

public sealed class LockFile
{
    private static readonly TimeSpan AcquireTimeout = TimeSpan.FromMilliseconds(500);

    private readonly IFileSystem _fs;
    private readonly string _lockPath;
    private readonly object _gate = new();
    private IDisposable? _heldLock;

    private static readonly LockFile DefaultInstance = new(new RealFileSystem(), DefaultLockPath());

    private static string DefaultLockPath() => Path.Combine(AppPaths.DataDir, "running.lock");

    public LockFile(IFileSystem? fileSystem = null, string? lockPath = null)
    {
        _fs = fileSystem ?? new RealFileSystem();
        _lockPath = lockPath ?? DefaultLockPath();
    }

    public void AcquireInstance(ILogger? logger = null)
    {
        try
        {
            lock (_gate)
            {
                if (_heldLock != null) return;
            }

            var dir = Path.GetDirectoryName(_lockPath);
            if (!string.IsNullOrEmpty(dir))
                _fs.CreateDirectory(dir);

            _fs.WriteAllText(_lockPath,
                $"{Environment.ProcessId}\n{DateTime.UtcNow:o}\n{Environment.ProcessPath}\n");

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
            if (_fs.FileExists(_lockPath))
                _fs.DeleteFile(_lockPath);
        }
        catch (Exception ex)
        {
            logger?.Debug(ex, "[LockFile] Could not delete {Path}", _lockPath);
        }
    }

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

            Process? proc = null;
            try { proc = Process.GetProcessById(pid); } catch { }

            if (proc == null)
            {
                var timestamp = contents.Length >= 2 ? contents[1].Trim() : "unknown time";
                return $"Previous run (PID {pid}, started {timestamp}) did not shut down cleanly. " +
                       "Check logs for details; consider running with --safe if the app keeps crashing.";
            }

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

    public static void Acquire(ILogger? logger = null) => DefaultInstance.AcquireInstance(logger);

    public static void Release(ILogger? logger = null) => DefaultInstance.ReleaseInstance(logger);

    public static string? DetectPreviousCrash(ILogger? logger = null)
        => DefaultInstance.DetectPreviousCrashInstance(logger);
}
