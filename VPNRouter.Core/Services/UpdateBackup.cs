using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace VPNRouter.Core.Services;

/// <summary>
/// Local snapshot-based rollback for failed auto-updates. v2.31.10-r2
/// Task E (Pavel-tasked).
///
/// <para><b>Why this exists.</b> Pre-Task-E the only safety net for a
/// botched in-app update was <c>SelfRepair</c>, which downloads
/// <c>install.ps1</c> from the network and re-runs the canonical
/// installer. That works, but has three real failure modes that we keep
/// hitting in support tickets:
/// <list type="bullet">
///   <item>The user has no network — captive portal at a hotel, broken
///   DNS after a botched VPN apply, or the very VPN we routed through
///   us is now broken because the update damaged Service.dll.</item>
///   <item>Defender/Avast flag the inline <c>iwr | iex</c> bootstrap
///   pattern (F1 from the AV/firewall audit). Even after Task C's
///   tempfile fix the AMSI scan still costs ~5–15 seconds and
///   occasionally false-positives.</item>
///   <item>The web one-liner takes 30–90s end-to-end (download +
///   extract + Service stop/start + relaunch). A user who just clicked
///   "Update" and is now staring at PowerShell windows opening
///   themselves is reasonably alarmed.</item>
/// </list></para>
///
/// <para><b>What this does.</b> Before <c>helper.cmd</c> overwrites
/// <c>app/</c> with the staged update, we copy the current <c>app/</c>
/// to a sibling <c>app.bak/</c>. If the next launch detects a damaged
/// install (mixed-version DLLs from a partial xcopy, or the marker file
/// <c>app/.update-failed</c> our hardened helper.cmd writes on non-zero
/// xcopy exit), we restore from <c>app.bak/</c> in-process — no
/// network, no PowerShell, no AMSI. After one healthy launch the
/// snapshot is deleted to free the ~50–60 MB it occupies.</para>
///
/// <para><b>Why preferable to network SelfRepair.</b>
/// <list type="bullet">
///   <item>Works offline.</item>
///   <item>~5 seconds local file copy vs. 30–90 s network round-trip.</item>
///   <item>No PowerShell / AMSI surface.</item>
///   <item>No chicken-and-egg: a damaged Service.dll could break VPN,
///   making vpn.ninitux.com unreachable. Local rollback doesn't care.</item>
/// </list>
/// SelfRepair stays as the second-line fallback for the case where
/// <c>app.bak/</c> itself is missing or corrupt.</para>
///
/// <para><b>Disk space cost.</b> ~50–60 MB while a snapshot is alive.
/// We delete it on the first healthy post-update launch (caller's job
/// — see <see cref="DeleteSnapshot"/>). The transient doubling is the
/// price of being able to roll back without network.</para>
///
/// <para><b>Atomicity.</b> <see cref="CreateSnapshot"/> writes to a
/// <c>.bak.tmp</c> staging dir first, then renames to <c>.bak</c>. If
/// the rename fails, the partial copy stays in <c>.bak.tmp</c> and is
/// cleaned on the next snapshot attempt. <see cref="RestoreSnapshot"/>
/// is similarly idempotent — calling it when no snapshot exists is a
/// no-op, and calling it after a successful restore returns the same
/// "no rollback needed" answer. Create, restore, and cleanup share an
/// install-scoped file lock; delayed cleanup is generation-bound.</para>
/// </summary>
public static class UpdateBackup
{
    /// <summary>Sibling-of-app/ snapshot directory name.</summary>
    private const string SnapshotName = "app.bak";

    /// <summary>Atomic-rename staging directory name.</summary>
    private const string SnapshotStagingName = "app.bak.tmp";

    /// <summary>Generation sidecar for stale-cleanup rejection.</summary>
    private const string SnapshotGenerationName = "app.bak.id";

    /// <summary>Install-scoped cross-process operation lock.</summary>
    internal const string OperationLockName = ".update-backup.lock";

    private static FileStream? TryAcquireOperationLock(
        string installDir,
        out bool contention,
        out string? error)
    {
        contention = false;
        error = null;
        try
        {
            return File.Open(
                Path.Combine(installDir, OperationLockName),
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);
        }
        catch (IOException ex)
        {
            var nativeCode = ex.HResult & 0xffff;
            // POSIX EAGAIN/EACCES; Win32 sharing/lock violation.
            contention = nativeCode is 11 or 13 or 32 or 33;
            error = $"{ex.GetType().Name}: {ex.Message}";
            return null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            return null;
        }
    }

    /// <summary>
    /// Marker file written into <c>app/</c> by helper.cmd if the file
    /// copy failed. Read by the App on next start; presence triggers
    /// rollback even if the (mostly-old) DLL set happens to be self-
    /// consistent according to <c>InstallHealthCheck</c>.
    /// </summary>
    public const string FailureMarkerName = ".update-failed";

    /// <summary>Outcome of <see cref="CreateSnapshot"/>.</summary>
    public sealed record SnapshotResult(bool Success, string SnapshotPath, string Diagnostic);

    /// <summary>Outcome of <see cref="RestoreSnapshot"/>.</summary>
    public sealed record RestoreResult(bool Restored, string Reason)
    {
        public bool OperationInProgress { get; init; }
    }

    /// <summary>Capture the immutable identity of the current snapshot.</summary>
    public static string? GetSnapshotGeneration(string installDir)
    {
        if (string.IsNullOrWhiteSpace(installDir))
            return null;

        using var operationLock = TryAcquireOperationLock(installDir, out _, out _);
        if (operationLock is null)
            return null;

        var existing = ReadSnapshotGeneration(installDir);
        if (existing is not null)
            return existing;

        var snapshot = Path.Combine(installDir, SnapshotName);
        if (!Directory.Exists(snapshot))
            return null;

        var generation = Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(
                Path.Combine(installDir, SnapshotGenerationName),
                generation);
            return generation;
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadSnapshotGeneration(string installDir)
    {
        var snapshot = Path.Combine(installDir, SnapshotName);
        var generationPath = Path.Combine(installDir, SnapshotGenerationName);
        if (!Directory.Exists(snapshot) || !File.Exists(generationPath))
            return null;

        try
        {
            var text = File.ReadAllText(generationPath).Trim();
            return Guid.TryParseExact(text, "N", out _) ? text : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Copy <c>{installDir}/app/</c> to <c>{installDir}/app.bak/</c>.
    /// Idempotent: replaces a previous snapshot. Atomic via tmp-rename.
    /// </summary>
    /// <param name="installDir">
    /// Directory CONTAINING <c>app/</c>. For VPNRouter on Windows that's
    /// typically <c>C:\Program Files\VPNRouter\</c> — the parent of
    /// <c>AppContext.BaseDirectory</c>.
    /// </param>
    /// <returns>Diagnostic record. <see cref="SnapshotResult.Success"/>
    /// is <c>true</c> only if the snapshot is now usable for restore.</returns>
    public static SnapshotResult CreateSnapshot(string installDir)
    {
        if (string.IsNullOrWhiteSpace(installDir))
            return new SnapshotResult(false, string.Empty, "installDir was null or empty");

        var src = Path.Combine(installDir, "app");
        var dst = Path.Combine(installDir, SnapshotName);
        var stage = Path.Combine(installDir, SnapshotStagingName);
        var generationPath = Path.Combine(installDir, SnapshotGenerationName);

        using var operationLock = TryAcquireOperationLock(
            installDir,
            out var lockContention,
            out var lockError);
        if (operationLock is null)
        {
            var diagnostic = lockContention
                ? "another snapshot operation is in progress"
                : $"snapshot operation lock unavailable: {lockError ?? "unknown error"}";
            return new SnapshotResult(false, dst, diagnostic);
        }

        if (!Directory.Exists(src))
            return new SnapshotResult(false, dst, $"source app/ does not exist at '{src}'");

        try
        {
            // Clear any half-finished prior staging dir from a crashed
            // earlier attempt. We don't need it — about to rebuild.
            if (Directory.Exists(stage))
            {
                try { Directory.Delete(stage, recursive: true); }
                catch (Exception ex)
                {
                    return new SnapshotResult(false, dst,
                        $"failed to clear stale staging dir '{stage}': {ex.Message}");
                }
            }

            CopyDirectoryRecursive(src, stage);

            // Clear the old generation before replacing its snapshot. If
            // this fails, retain both the old backup and the new stage.
            try { File.Delete(generationPath); }
            catch (Exception ex)
            {
                return new SnapshotResult(false, dst,
                    $"failed to clear stale snapshot generation: {ex.Message} " +
                    $"(staging copy preserved at '{stage}' for manual recovery)");
            }

            // Atomic-ish rename: delete old .bak, then move .bak.tmp →
            // .bak. Directory.Move is atomic on the same volume, which
            // is always the case here (sibling under installDir).
            if (Directory.Exists(dst))
            {
                try { Directory.Delete(dst, recursive: true); }
                catch (Exception ex)
                {
                    return new SnapshotResult(false, dst,
                        $"failed to delete previous snapshot '{dst}': {ex.Message} " +
                        $"(staging copy preserved at '{stage}' for manual recovery)");
                }
            }

            Directory.Move(stage, dst);
            try
            {
                File.WriteAllText(generationPath, Guid.NewGuid().ToString("N"));
            }
            catch (Exception ex)
            {
                return new SnapshotResult(false, dst,
                    $"snapshot created but generation marker failed: {ex.Message}");
            }

            return new SnapshotResult(true, dst,
                $"snapshot created at '{dst}'");
        }
        catch (Exception ex)
        {
            return new SnapshotResult(false, dst,
                $"snapshot creation failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// If <c>{installDir}/app.bak/</c> exists, replace
    /// <c>{installDir}/app/</c> with its contents. Idempotent — no-op
    /// if no snapshot exists.
    /// </summary>
    /// <remarks>
    /// Caller is expected to invoke this BEFORE any DLL from
    /// <c>app/</c> is loaded into the current process. After restore
    /// the caller should relaunch the App so the freshly-restored
    /// binaries replace the in-memory mismatched ones.
    /// </remarks>
    public static RestoreResult RestoreSnapshot(string installDir) =>
        RestoreSnapshot(installDir, Directory.Move);

    /// <summary>Fault-injection seam for deterministic restore compensation tests.</summary>
    internal static RestoreResult RestoreSnapshot(
        string installDir,
        Action<string, string> moveDirectory)
    {
        ArgumentNullException.ThrowIfNull(moveDirectory);

        if (string.IsNullOrWhiteSpace(installDir))
            return new RestoreResult(false, "installDir was null or empty");

        var app = Path.Combine(installDir, "app");
        var bak = Path.Combine(installDir, SnapshotName);
        var stage = Path.Combine(installDir, SnapshotStagingName);
        var generationPath = Path.Combine(installDir, SnapshotGenerationName);

        using var operationLock = TryAcquireOperationLock(
            installDir,
            out var lockContention,
            out var lockError);
        if (operationLock is null)
        {
            return new RestoreResult(
                false,
                lockContention
                    ? "another snapshot operation is in progress"
                    : $"snapshot operation lock unavailable: {lockError ?? "unknown error"}")
            {
                OperationInProgress = lockContention,
            };
        }

        if (!Directory.Exists(bak))
            return new RestoreResult(false, $"no snapshot at '{bak}' — nothing to restore");

        // Sanity: do not restore from an empty / single-file snapshot.
        // 50 MB+ install dir replaced by 0-byte directory tree would
        // brick the user worse than the corrupted state we're trying
        // to fix.
        try
        {
            var fileCount = Directory.EnumerateFiles(bak, "*", SearchOption.AllDirectories).Count();
            if (fileCount < 5)
            {
                return new RestoreResult(false,
                    $"snapshot at '{bak}' looks empty/truncated ({fileCount} files) — refusing to restore");
            }
        }
        catch (Exception ex)
        {
            return new RestoreResult(false,
                $"snapshot integrity check failed: {ex.Message}");
        }

        var appMovedToStage = false;
        try
        {
            // Stage-rename pattern so the restore is atomic from the
            // App's perspective: app/ either points at the old broken
            // tree or the snapshot tree, never a half-replaced mix.
            // (At most 1 "outdated" snapshot tree leaks into stage if
            // we crash mid-rename — cleaned next attempt.)

            if (Directory.Exists(stage) && !Directory.Exists(app))
            {
                // A prior failed compensation already left the previous app
                // safely staged. Reuse it instead of deleting the only
                // recoverable current tree before another restore attempt.
                appMovedToStage = true;
            }
            else
            {
                if (Directory.Exists(stage))
                {
                    try { Directory.Delete(stage, recursive: true); } catch { /* best-effort */ }
                }

                // Move app/ aside. If app/ doesn't exist (highly unusual
                // but possible if user manually nuked it), skip.
                if (Directory.Exists(app))
                {
                    moveDirectory(app, stage);
                    appMovedToStage = true;
                }
            }

            // Move bak/ → app/. After this point, app/ contains the
            // pre-update DLL set.
            moveDirectory(bak, app);
            try { File.Delete(generationPath); }
            catch { /* stale sidecar is harmless without app.bak/ */ }

            // Clean up the old (broken) tree we moved to stage. Best-
            // effort — if delete fails, it just lingers as "app.bak.tmp"
            // and gets cleaned on the next CreateSnapshot.
            if (Directory.Exists(stage))
            {
                try { Directory.Delete(stage, recursive: true); } catch { /* best-effort */ }
            }

            return new RestoreResult(true,
                $"restored '{app}' from snapshot");
        }
        catch (Exception ex)
        {
            if (appMovedToStage && !Directory.Exists(app) && Directory.Exists(stage))
            {
                try
                {
                    moveDirectory(stage, app);
                    return new RestoreResult(false,
                        $"restore failed: {ex.GetType().Name}: {ex.Message}; " +
                        "the previous app tree was restored");
                }
                catch (Exception compensationEx)
                {
                    return new RestoreResult(false,
                        $"restore failed: {ex.GetType().Name}: {ex.Message}; " +
                        $"compensation failed: {compensationEx.GetType().Name}: {compensationEx.Message} " +
                        $"(previous app tree preserved at '{stage}')");
                }
            }

            return new RestoreResult(false,
                $"restore failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Remove <c>{installDir}/app.bak/</c>. Called after the App has
    /// confirmed the new install is healthy (typically a few seconds
    /// after a successful first post-update launch). Idempotent.
    /// </summary>
    /// <returns><c>true</c> if a snapshot was deleted (or didn't exist
    /// to begin with), <c>false</c> if delete failed.</returns>
    public static bool DeleteSnapshot(string installDir) =>
        DeleteSnapshotCore(installDir, expectedGeneration: null);

    /// <summary>Delete only the snapshot generation captured by the caller.</summary>
    public static bool DeleteSnapshot(string installDir, string expectedGeneration)
    {
        if (string.IsNullOrWhiteSpace(expectedGeneration))
            return false;

        return DeleteSnapshotCore(installDir, expectedGeneration);
    }

    private static bool DeleteSnapshotCore(string installDir, string? expectedGeneration)
    {
        if (string.IsNullOrWhiteSpace(installDir))
            return false;

        var app = Path.Combine(installDir, "app");
        var bak = Path.Combine(installDir, SnapshotName);
        var stage = Path.Combine(installDir, SnapshotStagingName);
        var generationPath = Path.Combine(installDir, SnapshotGenerationName);

        using var operationLock = TryAcquireOperationLock(installDir, out _, out _);
        if (operationLock is null)
            return false;

        if (expectedGeneration is not null &&
            ReadSnapshotGeneration(installDir) != expectedGeneration)
        {
            return false;
        }

        // Never delete the only recoverable tree left by an interrupted
        // restore, even for an otherwise matching cleanup generation.
        if (!Directory.Exists(app) &&
            (Directory.Exists(bak) || Directory.Exists(stage)))
        {
            return false;
        }

        var ok = true;
        foreach (var path in new[] { bak, stage })
        {
            if (!Directory.Exists(path)) continue;
            try { Directory.Delete(path, recursive: true); }
            catch { ok = false; }
        }

        try { File.Delete(generationPath); }
        catch { ok = false; }

        return ok;
    }

    /// <summary>
    /// Returns <c>true</c> if helper.cmd left a <c>.update-failed</c>
    /// marker in <c>{installDir}/app/</c> on the most recent update —
    /// signal that the file copy did not complete cleanly even if the
    /// DLL set happens to look consistent.
    /// </summary>
    public static bool HasFailureMarker(string installDir)
    {
        if (string.IsNullOrWhiteSpace(installDir)) return false;
        var marker = Path.Combine(installDir, "app", FailureMarkerName);
        return File.Exists(marker);
    }

    /// <summary>
    /// Best-effort delete of the <c>.update-failed</c> marker. Called
    /// by App startup after successful rollback or after a clean
    /// <c>InstallHealthCheck</c> verifies the install is fine.
    /// </summary>
    public static void ClearFailureMarker(string installDir)
    {
        if (string.IsNullOrWhiteSpace(installDir)) return;
        var marker = Path.Combine(installDir, "app", FailureMarkerName);
        try { if (File.Exists(marker)) File.Delete(marker); }
        catch { /* swallow — best-effort */ }
    }

    /// <summary>
    /// Reads the diagnostic line stored in the <c>.update-failed</c>
    /// marker (helper.cmd writes a single line with the xcopy exit
    /// code + timestamp). Empty string if not present or unreadable.
    /// </summary>
    public static string ReadFailureMarker(string installDir)
    {
        if (string.IsNullOrWhiteSpace(installDir)) return string.Empty;
        var marker = Path.Combine(installDir, "app", FailureMarkerName);
        try
        {
            if (!File.Exists(marker)) return string.Empty;
            return File.ReadAllText(marker).Trim();
        }
        catch { return string.Empty; }
    }

    /// <summary>
    /// Recursive directory copy. .NET has no built-in equivalent that
    /// handles read-only files gracefully, so we roll one. Mirrors
    /// xcopy /E /Y /R semantics — overwrites read-only targets.
    /// </summary>
    private static void CopyDirectoryRecursive(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);

        foreach (var file in Directory.EnumerateFiles(sourceDir))
        {
            var name = Path.GetFileName(file);
            var dst = Path.Combine(destDir, name);

            // If dst exists and is read-only (rare in our installs but
            // some build tools mark .pdb / .config read-only), strip
            // the attribute so File.Copy doesn't throw.
            if (File.Exists(dst))
            {
                try
                {
                    var attr = File.GetAttributes(dst);
                    if ((attr & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
                        File.SetAttributes(dst, attr & ~FileAttributes.ReadOnly);
                }
                catch { /* let File.Copy surface the real error */ }
            }

            File.Copy(file, dst, overwrite: true);
        }

        foreach (var sub in Directory.EnumerateDirectories(sourceDir))
        {
            var name = Path.GetFileName(sub);
            CopyDirectoryRecursive(sub, Path.Combine(destDir, name));
        }
    }
}
