using System;
using System.IO;
using System.Linq;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

public sealed class UpdateBackupTests
{
    private static string CreateFakeInstall(int fileCount = 8)
    {
        var root = Path.Combine(Path.GetTempPath(),
            "vpnrouter-update-backup-tests-" + Guid.NewGuid().ToString("N"));
        var app = Path.Combine(root, "app");
        Directory.CreateDirectory(app);

        for (int i = 0; i < fileCount / 2; i++)
            File.WriteAllText(Path.Combine(app, $"VPNRouter.{i}.dll"), $"dll-content-{i}");

        var sub = Path.Combine(app, "runtimes", "win-x64", "native");
        Directory.CreateDirectory(sub);
        for (int i = 0; i < fileCount / 2; i++)
            File.WriteAllText(Path.Combine(sub, $"native{i}.dll"), $"native-{i}");

        return root;
    }

    private static void CleanUp(string root)
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { }
    }

    [Fact]
    public void CreateSnapshot_CopiesAppDirToBak()
    {
        var root = CreateFakeInstall();
        try
        {
            var result = UpdateBackup.CreateSnapshot(root);
            Assert.True(result.Success, $"CreateSnapshot failed: {result.Diagnostic}");
            Assert.Equal(Path.Combine(root, "app.bak"), result.SnapshotPath);
            Assert.True(Directory.Exists(Path.Combine(root, "app.bak")),
                "app.bak/ should exist after CreateSnapshot");

            var srcFiles = Directory.GetFiles(Path.Combine(root, "app"), "*", SearchOption.AllDirectories);
            var bakFiles = Directory.GetFiles(Path.Combine(root, "app.bak"), "*", SearchOption.AllDirectories);
            Assert.Equal(srcFiles.Length, bakFiles.Length);

            foreach (var src in srcFiles)
            {
                var rel = Path.GetRelativePath(Path.Combine(root, "app"), src);
                var dst = Path.Combine(root, "app.bak", rel);
                Assert.True(File.Exists(dst), $"missing in snapshot: {rel}");
                Assert.Equal(File.ReadAllText(src), File.ReadAllText(dst));
            }

            Assert.False(Directory.Exists(Path.Combine(root, "app.bak.tmp")),
                "app.bak.tmp/ leaked after successful CreateSnapshot");
        }
        finally { CleanUp(root); }
    }

    [Fact]
    public void CreateSnapshot_OverwritesPreviousSnapshot()
    {
        var root = CreateFakeInstall();
        try
        {
            UpdateBackup.CreateSnapshot(root);

            File.WriteAllText(Path.Combine(root, "app", "newfile.dll"), "new");

            var second = UpdateBackup.CreateSnapshot(root);
            Assert.True(second.Success);

            Assert.True(File.Exists(Path.Combine(root, "app.bak", "newfile.dll")),
                "second snapshot should include newfile.dll");
        }
        finally { CleanUp(root); }
    }

    [Fact]
    public void DeleteSnapshot_StaleGenerationCannotDeleteReplacement()
    {
        var root = CreateFakeInstall();
        try
        {
            var first = UpdateBackup.CreateSnapshot(root);
            Assert.True(first.Success, first.Diagnostic);
            var firstGeneration = UpdateBackup.GetSnapshotGeneration(root);
            Assert.NotNull(firstGeneration);

            File.WriteAllText(Path.Combine(root, "app", "new-generation.dll"), "new");
            var second = UpdateBackup.CreateSnapshot(root);
            Assert.True(second.Success, second.Diagnostic);
            var secondGeneration = UpdateBackup.GetSnapshotGeneration(root);
            Assert.NotNull(secondGeneration);
            Assert.NotEqual(firstGeneration, secondGeneration);

            Assert.False(UpdateBackup.DeleteSnapshot(root, firstGeneration!));
            Assert.True(File.Exists(Path.Combine(root, "app.bak", "new-generation.dll")));
            Assert.Equal(secondGeneration, UpdateBackup.GetSnapshotGeneration(root));

            Assert.True(UpdateBackup.DeleteSnapshot(root, secondGeneration!));
            Assert.False(Directory.Exists(Path.Combine(root, "app.bak")));
            Assert.Null(UpdateBackup.GetSnapshotGeneration(root));
        }
        finally { CleanUp(root); }
    }

    [Fact]
    public void SnapshotGeneration_MalformedSidecarsFailClosedAndRepair()
    {
        var root = CreateFakeInstall();
        try
        {
            var snapshot = UpdateBackup.CreateSnapshot(root);
            Assert.True(snapshot.Success, snapshot.Diagnostic);
            var generationPath = Path.Combine(root, "app.bak.id");
            var currentGeneration = UpdateBackup.GetSnapshotGeneration(root)!;

            foreach (var malformed in new[]
            {
                string.Empty,
                "not-a-guid",
                currentGeneration + "0",
                $" {currentGeneration} ",
                new string('a', 4096),
            })
            {
                File.WriteAllText(generationPath, malformed);
                Assert.False(UpdateBackup.DeleteSnapshot(root, currentGeneration));
                Assert.True(Directory.Exists(Path.Combine(root, "app.bak")));

                var repaired = UpdateBackup.GetSnapshotGeneration(root);
                Assert.NotNull(repaired);
                Assert.NotEqual(currentGeneration, repaired);
                currentGeneration = repaired!;
            }

            File.Delete(generationPath);
            Assert.False(UpdateBackup.DeleteSnapshot(root, currentGeneration));
            Assert.True(Directory.Exists(Path.Combine(root, "app.bak")));
            var repairedMissing = UpdateBackup.GetSnapshotGeneration(root);
            Assert.NotNull(repairedMissing);
            Assert.NotEqual(currentGeneration, repairedMissing);
        }
        finally { CleanUp(root); }
    }

    [Fact]
    public void RestoreSnapshot_NoOpWhenNoSnapshot()
    {
        var root = CreateFakeInstall();
        try
        {
            var r = UpdateBackup.RestoreSnapshot(root);
            Assert.False(r.Restored);
            Assert.Contains("no snapshot", r.Reason, StringComparison.OrdinalIgnoreCase);
        }
        finally { CleanUp(root); }
    }

    [Fact]
    public void SnapshotOperations_RefuseWhileInstallLockIsHeld()
    {
        var root = CreateFakeInstall();
        try
        {
            var snapshot = UpdateBackup.CreateSnapshot(root);
            Assert.True(snapshot.Success, snapshot.Diagnostic);

            using var heldLock = new FileStream(
                Path.Combine(root, UpdateBackup.OperationLockName),
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);

            var create = UpdateBackup.CreateSnapshot(root);
            var restore = UpdateBackup.RestoreSnapshot(root);
            var delete = UpdateBackup.DeleteSnapshot(root);

            Assert.False(create.Success);
            Assert.Contains("in progress", create.Diagnostic);
            Assert.False(restore.Restored);
            Assert.True(restore.OperationInProgress);
            Assert.Contains("in progress", restore.Reason);
            Assert.False(delete);
            Assert.True(Directory.Exists(Path.Combine(root, "app")));
            Assert.True(Directory.Exists(Path.Combine(root, "app.bak")));
            Assert.False(Directory.Exists(Path.Combine(root, "app.bak.tmp")));
        }
        finally { CleanUp(root); }
    }

    [Fact]
    public void RestoreSnapshot_LockPathFailure_IsNotReportedAsContention()
    {
        var root = CreateFakeInstall();
        try
        {
            var snapshot = UpdateBackup.CreateSnapshot(root);
            Assert.True(snapshot.Success, snapshot.Diagnostic);

            var lockPath = Path.Combine(root, UpdateBackup.OperationLockName);
            File.Delete(lockPath);
            Directory.CreateDirectory(lockPath);

            var create = UpdateBackup.CreateSnapshot(root);
            var restore = UpdateBackup.RestoreSnapshot(root);

            Assert.False(create.Success);
            Assert.Contains("lock unavailable", create.Diagnostic);
            Assert.DoesNotContain("in progress", create.Diagnostic);
            Assert.False(restore.Restored);
            Assert.False(restore.OperationInProgress);
            Assert.Contains("lock unavailable", restore.Reason);
            Assert.True(Directory.Exists(Path.Combine(root, "app")));
            Assert.True(Directory.Exists(Path.Combine(root, "app.bak")));
            Assert.False(Directory.Exists(Path.Combine(root, "app.bak.tmp")));
        }
        finally { CleanUp(root); }
    }

    [Fact]
    public void RestoreSnapshot_ReplacesCorruptedAppWithBackup()
    {
        var root = CreateFakeInstall();
        try
        {
            UpdateBackup.CreateSnapshot(root);

            var firstFile = Directory.GetFiles(Path.Combine(root, "app"))
                .First(f => f.EndsWith(".dll"));
            File.WriteAllText(firstFile, "CORRUPTED");
            File.Delete(Directory.GetFiles(Path.Combine(root, "app", "runtimes", "win-x64", "native"))
                .First());

            var r = UpdateBackup.RestoreSnapshot(root);
            Assert.True(r.Restored, $"RestoreSnapshot failed: {r.Reason}");

            Assert.Contains("dll-content", File.ReadAllText(firstFile));

            Assert.False(Directory.Exists(Path.Combine(root, "app.bak")));
            var second = UpdateBackup.RestoreSnapshot(root);
            Assert.False(second.Restored);
        }
        finally { CleanUp(root); }
    }

    [Fact]
    public void RestoreSnapshot_SecondMoveFailure_RestoresPreviousApp()
    {
        var root = CreateFakeInstall();
        try
        {
            UpdateBackup.CreateSnapshot(root);
            var marker = Path.Combine(root, "app", "current-tree.txt");
            File.WriteAllText(marker, "keep-current");
            var moveAttempt = 0;

            var result = UpdateBackup.RestoreSnapshot(root, (source, destination) =>
            {
                moveAttempt++;
                if (moveAttempt == 2)
                    throw new IOException("injected snapshot move failure");
                Directory.Move(source, destination);
            });

            Assert.False(result.Restored);
            Assert.Contains("previous app tree was restored", result.Reason);
            Assert.Equal("keep-current", File.ReadAllText(marker));
            Assert.True(Directory.Exists(Path.Combine(root, "app.bak")));
            Assert.False(Directory.Exists(Path.Combine(root, "app.bak.tmp")));
            Assert.Equal(3, moveAttempt);
        }
        finally { CleanUp(root); }
    }

    [Fact]
    public void RestoreSnapshot_CompensationFailure_PreservesStagedApp()
    {
        var root = CreateFakeInstall();
        try
        {
            UpdateBackup.CreateSnapshot(root);
            File.WriteAllText(Path.Combine(root, "app", "current-tree.txt"), "recoverable");
            var moveAttempt = 0;

            var result = UpdateBackup.RestoreSnapshot(root, (source, destination) =>
            {
                moveAttempt++;
                if (moveAttempt >= 2)
                    throw new IOException($"injected move failure {moveAttempt}");
                Directory.Move(source, destination);
            });

            var stage = Path.Combine(root, "app.bak.tmp");
            Assert.False(result.Restored);
            Assert.Contains("restore failed", result.Reason);
            Assert.Contains("compensation failed", result.Reason);
            Assert.Contains(stage, result.Reason);
            Assert.False(Directory.Exists(Path.Combine(root, "app")));
            Assert.True(Directory.Exists(Path.Combine(root, "app.bak")));
            Assert.Equal("recoverable", File.ReadAllText(Path.Combine(stage, "current-tree.txt")));
            Assert.Equal(3, moveAttempt);
            Assert.False(UpdateBackup.DeleteSnapshot(root));
            Assert.True(Directory.Exists(Path.Combine(root, "app.bak")));
            Assert.True(Directory.Exists(stage));

            var retryMove = 0;
            var retry = UpdateBackup.RestoreSnapshot(root, (source, destination) =>
            {
                retryMove++;
                if (retryMove == 1)
                    throw new IOException("injected retry snapshot move failure");
                Directory.Move(source, destination);
            });

            Assert.False(retry.Restored);
            Assert.Contains("previous app tree was restored", retry.Reason);
            Assert.Equal("recoverable", File.ReadAllText(Path.Combine(root, "app", "current-tree.txt")));
            Assert.True(Directory.Exists(Path.Combine(root, "app.bak")));
            Assert.False(Directory.Exists(stage));
            Assert.Equal(2, retryMove);
        }
        finally { CleanUp(root); }
    }

    [Fact]
    public void RestoreSnapshot_RefusesEmptySnapshot()
    {
        var root = CreateFakeInstall();
        try
        {
            var bak = Path.Combine(root, "app.bak");
            Directory.CreateDirectory(bak);
            File.WriteAllText(Path.Combine(bak, "lonely.txt"), "x");

            var r = UpdateBackup.RestoreSnapshot(root);
            Assert.False(r.Restored);
            Assert.Contains("empty/truncated", r.Reason);

            Assert.True(Directory.Exists(Path.Combine(root, "app")));
            Assert.True(Directory.GetFiles(Path.Combine(root, "app"), "*", SearchOption.AllDirectories).Length > 0);
        }
        finally { CleanUp(root); }
    }

    [Fact]
    public void DeleteSnapshot_RemovesBackupAndStagingDirs()
    {
        var root = CreateFakeInstall();
        try
        {
            UpdateBackup.CreateSnapshot(root);
            Directory.CreateDirectory(Path.Combine(root, "app.bak.tmp"));
            File.WriteAllText(Path.Combine(root, "app.bak.tmp", "leak.txt"), "x");

            var ok = UpdateBackup.DeleteSnapshot(root);
            Assert.True(ok);
            Assert.False(Directory.Exists(Path.Combine(root, "app.bak")));
            Assert.False(Directory.Exists(Path.Combine(root, "app.bak.tmp")));
            Assert.Null(UpdateBackup.GetSnapshotGeneration(root));

            Assert.True(UpdateBackup.DeleteSnapshot(root));
        }
        finally { CleanUp(root); }
    }

    [Fact]
    public void FailureMarker_RoundTrips()
    {
        var root = CreateFakeInstall();
        try
        {
            Assert.False(UpdateBackup.HasFailureMarker(root));
            Assert.Equal(string.Empty, UpdateBackup.ReadFailureMarker(root));

            var markerPath = Path.Combine(root, "app", UpdateBackup.FailureMarkerName);
            File.WriteAllText(markerPath, "xcopy exit=4 at 2026-05-06 11:23:45");

            Assert.True(UpdateBackup.HasFailureMarker(root));
            Assert.Contains("xcopy exit=4", UpdateBackup.ReadFailureMarker(root));

            UpdateBackup.ClearFailureMarker(root);
            Assert.False(UpdateBackup.HasFailureMarker(root));
        }
        finally { CleanUp(root); }
    }

    private static string StripLineComments(string src)
    {
        return string.Join("\n",
            src.Split('\n').Select(l =>
                l.Contains("//") ? l[..l.IndexOf("//")] : l));
    }

    private static string? FindProgramSource() =>
        FindSource("VPNRouter.App", "Program.cs");

    private static string? FindUpdateCheckerSource() =>
        FindSource("VPNRouter.Core", "Services", "UpdateChecker.cs");

    private static string? FindSource(params string[] relativePath)
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, Path.Combine(relativePath));
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
