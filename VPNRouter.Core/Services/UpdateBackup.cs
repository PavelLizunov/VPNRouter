using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace VPNRouter.Core.Services;

public static class UpdateBackup
{
    private const string SnapshotName = "app.bak";

    private const string SnapshotStagingName = "app.bak.tmp";

    private const string SnapshotGenerationName = "app.bak.id";

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
                $"snapshot creation failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public static RestoreResult RestoreSnapshot(string installDir) =>
        RestoreSnapshot(installDir, Directory.Move);

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
            if (Directory.Exists(stage) && !Directory.Exists(app))
            {
                appMovedToStage = true;
            }
            else
            {
                if (Directory.Exists(stage))
                {
                    try { Directory.Delete(stage, recursive: true); } catch { }
                }

                if (Directory.Exists(app))
                {
                    moveDirectory(app, stage);
                    appMovedToStage = true;
                }
            }

            moveDirectory(bak, app);
            try { File.Delete(generationPath); }
            catch { }

            if (Directory.Exists(stage))
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

    public static bool HasFailureMarker(string installDir)
    {
        if (string.IsNullOrWhiteSpace(installDir)) return false;
        var marker = Path.Combine(installDir, "app", FailureMarkerName);
        return File.Exists(marker);
    }

    public static void ClearFailureMarker(string installDir)
    {
        if (string.IsNullOrWhiteSpace(installDir)) return;
        var marker = Path.Combine(installDir, "app", FailureMarkerName);
        try { if (File.Exists(marker)) File.Delete(marker); }
        catch { }
    }

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

    private static void CopyDirectoryRecursive(string sourceDir, string destDir)
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
            CopyDirectoryRecursive(sub, Path.Combine(destDir, name));
        }
    }
}
