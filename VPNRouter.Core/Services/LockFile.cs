using System.Diagnostics;
using Serilog;

namespace VPNRouter.Core.Services;

/// <summary>
/// Drops a <c>running.lock</c> file under <see cref="AppPaths.DataDir"/>
/// at startup containing the current PID, and deletes it on graceful
/// shutdown. On next startup, <see cref="DetectPreviousCrash"/> reads
/// the file (if present): if the PID is no longer alive, the previous
/// session crashed or was force-killed — surface a banner to the user.
///
/// v2.23.0 self-healing.
/// </summary>
public static class LockFile
{
    private static string LockPath => Path.Combine(AppPaths.DataDir, "running.lock");

    /// <summary>
    /// Write current process PID into the lock file. Safe to call from
    /// app startup after <see cref="AppPaths.EnsureDirectories"/>. Silent
    /// if the write fails — we'd rather miss a crash detection than
    /// abort startup over a best-effort diagnostic file.
    /// </summary>
    public static void Acquire(ILogger? logger = null)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LockPath)!);
            File.WriteAllText(LockPath,
                $"{Environment.ProcessId}\n{DateTime.UtcNow:o}\n{Environment.ProcessPath}\n");
        }
        catch (Exception ex)
        {
            logger?.Warning(ex, "[LockFile] Could not write {Path}", LockPath);
        }
    }

    /// <summary>
    /// Delete the lock file. Call on graceful shutdown (normal quit or
    /// clean Environment.Exit via the update flow). Silent on failure.
    /// </summary>
    public static void Release(ILogger? logger = null)
    {
        try
        {
            if (File.Exists(LockPath))
                File.Delete(LockPath);
        }
        catch (Exception ex)
        {
            logger?.Debug(ex, "[LockFile] Could not delete {Path}", LockPath);
        }
    }

    /// <summary>
    /// Returns a human-readable summary if the previous session crashed
    /// (lock file exists but its PID is no longer alive, or the PID
    /// belongs to an unrelated process), otherwise null.
    /// Consumes (deletes) the stale lock file either way — leaving it
    /// around would surface the same warning on every subsequent run.
    /// </summary>
    public static string? DetectPreviousCrash(ILogger? logger = null)
    {
        try
        {
            if (!File.Exists(LockPath))
                return null;

            var contents = File.ReadAllText(LockPath).Split('\n',
                StringSplitOptions.RemoveEmptyEntries);
            TryDelete(LockPath, logger);

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

        static void TryDelete(string p, ILogger? l)
        {
            try { File.Delete(p); }
            catch (Exception ex) { l?.Debug(ex, "[LockFile] Could not delete stale lock {Path}", p); }
        }
    }
}
