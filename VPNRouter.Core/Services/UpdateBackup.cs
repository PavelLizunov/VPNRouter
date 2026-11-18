using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;

namespace VPNRouter.Core.Services;

public static class UpdateBackup
{
    private const string SnapshotName = "app.bak";

    private const string SnapshotGenerationName = "app.bak.id";

    internal const string OperationLockName = ".update-backup.lock";

    internal static string? SnapshotInstallDirectory(string applicationDir)
    {
        var app = new DirectoryInfo(Path.TrimEndingDirectorySeparator(Path.GetFullPath(applicationDir)));
        return app.Name.Equals("app", StringComparison.OrdinalIgnoreCase) ? app.Parent?.FullName : null;
    }

    private static FileStream? TryAcquireOperationLock(
        string installDir,
        out bool contention,
        out string? error)
    {
        contention = false;
        error = null;
        try
        {
            var path = Path.Combine(installDir, OperationLockName);
            if (new FileInfo(path).LinkTarget != null) throw new IOException("Update lock is redirected.");
            return File.Open(
                path,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);
        }
        catch (IOException ex)
        {
            var nativeCode = ex.HResult & 0xffff;
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

    public const string FailureMarkerName = ".update-failed";

    public sealed record SnapshotResult(bool Success, string SnapshotPath, string Diagnostic);

    public sealed record RestoreResult(bool Restored, string Reason)
    {
        public bool OperationInProgress { get; init; }
        public bool RelaunchRejected { get; init; }
    }

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
            var length = new FileInfo(generationPath).Length;
            if (length is <= 0 or > 64)
                return null;

            var text = File.ReadAllText(generationPath);
            return text.Length == 32 && Guid.TryParseExact(text, "N", out _)
                ? text
                : null;
        }
        catch
        {
            return null;
        }
    }

    public static SnapshotResult CreateSnapshot(string installDir)
    {
        if (string.IsNullOrWhiteSpace(installDir))
            return new SnapshotResult(false, string.Empty, "installDir was null or empty");

        var src = Path.Combine(installDir, "app");
        var dst = Path.Combine(installDir, SnapshotName);
        // Historical app.bak.tmp could be an incomplete copy or a displaced live tree. Preserve it.
        var stage = Path.Combine(installDir, "app.snapshot-copy-" + Guid.NewGuid().ToString("N"));
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
            if (Directory.Exists(stage) || File.Exists(stage) || new DirectoryInfo(stage).LinkTarget != null)
                return new(false, dst, $"snapshot staging path already exists at '{stage}'");
            CopyDirectoryRecursive(src, stage, applicationRoot: true);

            try { File.Delete(generationPath); }
            catch (Exception ex)
            {
                return new SnapshotResult(false, dst,
                    $"failed to clear stale snapshot generation: {ex.Message} " +
                    $"(staging copy preserved at '{stage}' for manual recovery)");
            }

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
                $"snapshot creation failed: {ex.GetType().Name}: {ex.Message} (staging path: '{stage}')");
        }
    }

    internal static RestoreResult CheckFlatInstall(string applicationDir)
    {
        using var operationLock = TryAcquireOperationLock(applicationDir, out var busy, out var error);
        return operationLock == null ? LockUnavailable(busy, error) : new(false, "Flat installation has no directory-swap snapshot.");
    }

    private static RestoreResult LockUnavailable(bool busy, string? error) =>
        new(false, busy ? "another snapshot operation is in progress" : $"snapshot operation lock unavailable: {error}")
        { OperationInProgress = busy, RelaunchRejected = !busy };

    public static RestoreResult RestoreSnapshot(string installDir)
    {
        if (!OperatingSystem.IsWindows()) return RestoreSnapshot(installDir, Directory.Move);
        try { VerifyWindowsInstallDirectory(installDir); }
        catch (Exception ex) { return new(false, ex.Message) { RelaunchRejected = true }; }
        void Verify(string path) => VerifyWindowsApplicationTree(installDir, path);
        return RestoreSnapshot(installDir, Directory.Move, Verify, validateDisplaced: Verify);
    }

    [SupportedOSPlatform("windows")]
    internal static string VerifyWindowsApplicationTree(string installDir, string tree)
    {
        VerifyWindowsInstallDirectory(installDir);
        var app = new DirectoryInfo(tree);
        ZapretRuntimeDirectory.CheckWindowsTree(app, allowCreatorOwnerTemplate: true);
        return app.FullName;
    }

    [SupportedOSPlatform("windows")]
    internal static void VerifyWindowsInstallDirectory(string installDir)
    {
        var drive = Path.GetPathRoot(installDir);
        if (!Path.IsPathFullyQualified(installDir) || drive == null || drive.Length != 3 ||
            !char.IsAsciiLetter(drive[0]) || drive[1] != ':' || drive[2] != '\\' ||
            new DriveInfo(drive).DriveType is not (DriveType.Fixed or DriveType.Removable or DriveType.Ram))
            throw new IOException("Rollback requires a protected local installation.");
        var install = new DirectoryInfo(installDir);
        ZapretRuntimeDirectory.CheckWindowsParents(install.FullName);
        ZapretRuntimeDirectory.CheckWindowsDirectory(install, allowCreatorOwnerTemplate: true);
    }

    internal static RestoreResult RestoreSnapshot(
        string installDir,
        Action<string, string> moveDirectory,
        Action<string>? validateCandidate = null,
        Action? relaunch = null,
        Action<string>? validateDisplaced = null)
    {
        ArgumentNullException.ThrowIfNull(moveDirectory);

        if (string.IsNullOrWhiteSpace(installDir))
            return new RestoreResult(false, "installDir was null or empty");

        var app = Path.Combine(installDir, "app");
        var bak = Path.Combine(installDir, SnapshotName);
        var stage = Path.Combine(installDir, "app.displaced-" + Guid.NewGuid().ToString("N"));
        var generationPath = Path.Combine(installDir, SnapshotGenerationName);

        using var operationLock = TryAcquireOperationLock(
            installDir,
            out var lockContention,
            out var lockError);
        if (operationLock == null) return LockUnavailable(lockContention, lockError);

        if (!Directory.Exists(bak))
            return new RestoreResult(false, $"no snapshot at '{bak}' — nothing to restore");

        try
        {
            validateCandidate?.Invoke(bak);
        }
        catch (Exception ex)
        {
            return new RestoreResult(false, $"snapshot verification failed: {ex.Message}") { RelaunchRejected = true };
        }

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
            if (Directory.Exists(app))
            {
                validateDisplaced?.Invoke(app);
                moveDirectory(app, stage);
                appMovedToStage = true;
            }

            moveDirectory(bak, app);
            try
            {
                relaunch?.Invoke();
            }
            catch (Exception ex)
            {
                try
                {
                    moveDirectory(app, bak);
                    if (appMovedToStage) RestoreDisplacedLive();
                }
                catch (Exception restoreError)
                {
                    return new RestoreResult(false, $"rollback relaunch and compensation failed: {restoreError.Message}; " +
                        $"recover the retained trees under '{installDir}'")
                    { RelaunchRejected = true };
                }
                return new RestoreResult(false, $"rollback relaunch failed: {ex.Message}") { RelaunchRejected = true };
            }
            try { File.Delete(generationPath); }
            catch { }

            if (appMovedToStage)
            {
                try { Directory.Delete(stage, recursive: true); } catch { }
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
                    RestoreDisplacedLive();
                    return new RestoreResult(false,
                        $"restore failed: {ex.GetType().Name}: {ex.Message}; " +
                        "the previous app tree was restored");
                }
                catch (Exception compensationEx)
                {
                    return new RestoreResult(false,
                        $"restore failed: {ex.GetType().Name}: {ex.Message}; " +
                        $"compensation failed: {compensationEx.GetType().Name}: {compensationEx.Message} " +
                        $"(previous app tree preserved at '{stage}')") { RelaunchRejected = true };
                }
            }

            return new RestoreResult(false,
                $"restore failed: {ex.GetType().Name}: {ex.Message}") { RelaunchRejected = true };
        }

        void RestoreDisplacedLive()
        {
            // The completed rename above is the proof of provenance for this invocation.
            validateDisplaced?.Invoke(stage);
            moveDirectory(stage, app);
        }
    }

    public static bool DeleteSnapshot(string installDir) =>
        DeleteSnapshotCore(installDir, expectedGeneration: null);

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
        var generationPath = Path.Combine(installDir, SnapshotGenerationName);

        using var operationLock = TryAcquireOperationLock(installDir, out _, out _);
        if (operationLock is null)
            return false;

        if (expectedGeneration is not null &&
            ReadSnapshotGeneration(installDir) != expectedGeneration)
        {
            return false;
        }

        if (!Directory.Exists(app)) return false;

        var ok = true;
        try { if (Directory.Exists(bak)) Directory.Delete(bak, recursive: true); }
        catch { ok = false; }

        try { File.Delete(generationPath); }
        catch { ok = false; }

        return ok;
    }

    public static bool HasFailureMarker(string installDir) =>
        !string.IsNullOrWhiteSpace(installDir) && HasApplicationFailureMarker(Path.Combine(installDir, "app"));

    internal static bool HasApplicationFailureMarker(string applicationDir) =>
        File.Exists(Path.Combine(applicationDir, FailureMarkerName));

    public static void ClearFailureMarker(string installDir)
    {
        if (string.IsNullOrWhiteSpace(installDir)) return;
        try { File.Delete(Path.Combine(installDir, "app", FailureMarkerName)); }
        catch { }
    }

    public static string ReadFailureMarker(string installDir) =>
        string.IsNullOrWhiteSpace(installDir) ? string.Empty : ReadApplicationFailureMarker(Path.Combine(installDir, "app"));

    internal static string ReadApplicationFailureMarker(string applicationDir)
    {
        try { return File.ReadAllText(Path.Combine(applicationDir, FailureMarkerName)).Trim(); }
        catch { return string.Empty; }
    }

    private static void CopyDirectoryRecursive(string sourceDir, string destDir, bool applicationRoot = false)
    {
        Directory.CreateDirectory(destDir);

        foreach (var file in Directory.EnumerateFiles(sourceDir))
        {
            var name = Path.GetFileName(file);
            var dst = Path.Combine(destDir, name);

            if (File.Exists(dst))
            {
                try
                {
                    var attr = File.GetAttributes(dst);
                    if ((attr & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
                        File.SetAttributes(dst, attr & ~FileAttributes.ReadOnly);
                }
                catch { }
            }

            File.Copy(file, dst, overwrite: true);
        }

        foreach (var sub in Directory.EnumerateDirectories(sourceDir))
        {
            var name = Path.GetFileName(sub);
            // A rollback snapshot is an application payload. Private repair scratch keeps its own ACL.
            if (applicationRoot && name.Equals(".repair-temp", StringComparison.OrdinalIgnoreCase)) continue;
            CopyDirectoryRecursive(sub, Path.Combine(destDir, name));
        }
    }
}
