using System;
using System.IO;
using System.Linq;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

/// <summary>
/// v2.31.10-r2 Task E — pin contract for the local rollback safety net.
///
/// <para>The rollback path replaces network-fetched <c>SelfRepair</c>
/// as first-line recovery. If a future refactor weakens any of these
/// invariants — empty-snapshot guard, atomic-rename, idempotency,
/// helper.cmd emitter wiring — the whole user-facing "auto-update can't
/// brick the install" promise breaks. These tests pin that promise.</para>
///
/// <para>Disk fixture: each test sets up a temp directory with an
/// <c>app/</c> sub-tree mimicking a real VPNRouter install (a handful
/// of fake .dll / .exe / nested file). Cleanup runs in a finally block
/// so a partial failure leaves nothing behind.</para>
/// </summary>
public sealed class UpdateBackupTests
{
    /// <summary>
    /// Build a temp install dir with an app/ subtree containing roughly
    /// the file count + nesting we see in real installs. Returns the
    /// installDir path; caller MUST clean up via try/finally.
    /// </summary>
    private static string CreateFakeInstall(int fileCount = 8)
    {
        var root = Path.Combine(Path.GetTempPath(),
            "vpnrouter-update-backup-tests-" + Guid.NewGuid().ToString("N"));
        var app = Path.Combine(root, "app");
        Directory.CreateDirectory(app);

        // Mix top-level and subdir files — the recursive copy must
        // preserve the layout exactly.
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

            // File-by-file content check — the snapshot is unusable if
            // any file was lost or content corrupted.
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

            // Staging dir must be cleaned up.
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
            // First snapshot.
            UpdateBackup.CreateSnapshot(root);

            // Mutate app/ — add a new file that should appear in the
            // second snapshot but NOT in the (replaced) first.
            File.WriteAllText(Path.Combine(root, "app", "newfile.dll"), "new");

            var second = UpdateBackup.CreateSnapshot(root);
            Assert.True(second.Success);

            Assert.True(File.Exists(Path.Combine(root, "app.bak", "newfile.dll")),
                "second snapshot should include newfile.dll");
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
    public void RestoreSnapshot_ReplacesCorruptedAppWithBackup()
    {
        var root = CreateFakeInstall();
        try
        {
            UpdateBackup.CreateSnapshot(root);

            // "Corrupt" the install: delete some files and modify
            // others (the mixed-version pre-Task-E symptom).
            var firstFile = Directory.GetFiles(Path.Combine(root, "app"))
                .First(f => f.EndsWith(".dll"));
            File.WriteAllText(firstFile, "CORRUPTED");
            File.Delete(Directory.GetFiles(Path.Combine(root, "app", "runtimes", "win-x64", "native"))
                .First());

            var r = UpdateBackup.RestoreSnapshot(root);
            Assert.True(r.Restored, $"RestoreSnapshot failed: {r.Reason}");

            // After restore, the corrupted file's content must be back
            // to the snapshot's content.
            Assert.Contains("dll-content", File.ReadAllText(firstFile));

            // After restore, app.bak/ is consumed (snapshot moved into
            // place). Calling restore again is now a no-op.
            Assert.False(Directory.Exists(Path.Combine(root, "app.bak")));
            var second = UpdateBackup.RestoreSnapshot(root);
            Assert.False(second.Restored);
        }
        finally { CleanUp(root); }
    }

    [Fact]
    public void RestoreSnapshot_RefusesEmptySnapshot()
    {
        var root = CreateFakeInstall();
        try
        {
            // Manually create an empty app.bak/ — the empty-snapshot
            // guard must kick in. A truly-empty snapshot would brick
            // the install worse than any partial-update damage.
            var bak = Path.Combine(root, "app.bak");
            Directory.CreateDirectory(bak);
            File.WriteAllText(Path.Combine(bak, "lonely.txt"), "x");

            var r = UpdateBackup.RestoreSnapshot(root);
            Assert.False(r.Restored);
            Assert.Contains("empty/truncated", r.Reason);

            // The original app/ should NOT have been touched.
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
            // Manually leak a staging dir so we can verify cleanup.
            Directory.CreateDirectory(Path.Combine(root, "app.bak.tmp"));
            File.WriteAllText(Path.Combine(root, "app.bak.tmp", "leak.txt"), "x");

            var ok = UpdateBackup.DeleteSnapshot(root);
            Assert.True(ok);
            Assert.False(Directory.Exists(Path.Combine(root, "app.bak")));
            Assert.False(Directory.Exists(Path.Combine(root, "app.bak.tmp")));

            // Idempotency: calling delete again returns ok with no work
            // to do.
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

            // Simulate the marker that helper.cmd writes on xcopy
            // failure.
            var markerPath = Path.Combine(root, "app", UpdateBackup.FailureMarkerName);
            File.WriteAllText(markerPath, "xcopy exit=4 at 2026-05-06 11:23:45");

            Assert.True(UpdateBackup.HasFailureMarker(root));
            Assert.Contains("xcopy exit=4", UpdateBackup.ReadFailureMarker(root));

            UpdateBackup.ClearFailureMarker(root);
            Assert.False(UpdateBackup.HasFailureMarker(root));
        }
        finally { CleanUp(root); }
    }

    /// <summary>
    /// Source-string pin: the helper.cmd emitter
    /// (<c>UpdateChecker.ApplyUpdateWindows</c>) MUST call
    /// <c>UpdateBackup.CreateSnapshot</c> BEFORE
    /// <c>File.WriteAllText(helperPath, cmd)</c>. If a future refactor
    /// reorders these or drops the snapshot call, this test fires.
    /// </summary>
    [Fact]
    public void HelperCmdEmitter_CallsCreateSnapshotBeforeWritingHelper()
    {
        var sourcePath = FindUpdateCheckerSource();
        if (sourcePath == null) return; // partial CI checkout — skip

        // Strip C# // line comments so an explanatory comment that
        // happens to mention the call shape doesn't false-positive
        // the position search below. (The preceding block comment in
        // UpdateChecker.cs does mention "File.WriteAllText(helperPath,"
        // by name in the design rationale.)
        var src = StripLineComments(File.ReadAllText(sourcePath));

        var snapshotIdx = src.IndexOf("UpdateBackup.CreateSnapshot", StringComparison.Ordinal);
        Assert.True(snapshotIdx > 0,
            "ApplyUpdateWindows must call UpdateBackup.CreateSnapshot — not found in UpdateChecker source");

        var writeIdx = src.IndexOf("File.WriteAllText(helperPath", StringComparison.Ordinal);
        Assert.True(writeIdx > 0,
            "expected File.WriteAllText(helperPath, ...) in UpdateChecker source");

        Assert.True(snapshotIdx < writeIdx,
            "CreateSnapshot call must precede the helper.cmd write — Task E rollback contract");
    }

    /// <summary>
    /// Source-string pin: the helper.cmd template MUST emit a marker-
    /// write line that creates <c>%DST%\.update-failed</c> when xcopy
    /// reports non-zero. Without this marker, App startup can't trigger
    /// rollback on a same-version-but-incomplete copy.
    /// </summary>
    [Fact]
    public void HelperCmdTemplate_WritesFailureMarkerOnXcopyFailure()
    {
        var sourcePath = FindUpdateCheckerSource();
        if (sourcePath == null) return;

        // Strip C# // line comments — UpdateChecker.cs has a design-
        // rationale comment near line 395 that mentions
        // ".update-failed" by name, which appears BEFORE the actual
        // template emit and would false-fail the ordering check.
        var src = StripLineComments(File.ReadAllText(sourcePath));
        Assert.Contains(".update-failed", src);
        Assert.Contains("XCOPY_EXIT", src);

        // The C# source string literal that emits the helper line
        // "if not \"!XCOPY_EXIT!\"==\"0\" (" appears in the file with
        // backslash-escaped quotes — i.e. the on-disk bytes are
        // `\"!XCOPY_EXIT!\"==\"0\"`. Search for the substring as it
        // appears on disk (with explicit \\\" escape pairs in the
        // C# search-literal).
        var checkIdx = src.IndexOf("if not \\\"!XCOPY_EXIT!\\\"==\\\"0\\\"",
            StringComparison.Ordinal);
        var markerIdx = src.IndexOf(".update-failed", StringComparison.Ordinal);
        Assert.True(checkIdx > 0,
            "expected !XCOPY_EXIT! non-zero check in helper.cmd template");
        Assert.True(markerIdx > checkIdx,
            "marker write must follow the xcopy-fail branch in the template");
        Assert.True(markerIdx - checkIdx < 800,
            "xcopy-fail check and marker write should be within the same template block");
    }

    /// <summary>
    /// Trim C# // line comments to avoid false positives in source-
    /// pin searches. A naive split-on-"//" is fine here because the
    /// patterns we search for don't contain "//".
    /// </summary>
    private static string StripLineComments(string src)
    {
        return string.Join("\n",
            src.Split('\n').Select(l =>
                l.Contains("//") ? l[..l.IndexOf("//")] : l));
    }

    private static string? FindUpdateCheckerSource()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(
                dir.FullName, "VPNRouter.Core", "Services", "UpdateChecker.cs");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
