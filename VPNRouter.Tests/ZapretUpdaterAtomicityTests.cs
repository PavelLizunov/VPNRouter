#nullable enable

using System.Text.RegularExpressions;
using Serilog;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

public sealed class ZapretUpdaterAtomicityTests : IDisposable
{
    private static readonly ILogger SilentLogger = new LoggerConfiguration().CreateLogger();
    private readonly List<string> _tempDirs = new();

    private string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"vpnrouter-zapret-atomicity-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try { Directory.Delete(dir, recursive: true); }
            catch { }
        }
    }

    [Fact]
    public void CopyDirectoryOverwrite_LockedFile_ReturnsFalse()
    {
        var src = NewTempDir();
        File.WriteAllText(Path.Combine(src, "free.txt"), "new-free");
        File.WriteAllText(Path.Combine(src, "locked.txt"), "new-locked");

        var dest = NewTempDir();
        var lockedDest = Path.Combine(dest, "locked.txt");
        File.WriteAllText(lockedDest, "old-locked");

        bool allCopied;
        if (OperatingSystem.IsWindows())
        {
            using (new FileStream(lockedDest, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                allCopied = ZapretUpdater.CopyDirectoryOverwrite(src, dest, SilentLogger);
            }
            Assert.Equal("old-locked", File.ReadAllText(lockedDest));
        }
        else
        {
            File.Delete(lockedDest);
            Directory.CreateDirectory(lockedDest);
            allCopied = ZapretUpdater.CopyDirectoryOverwrite(src, dest, SilentLogger);
            Assert.True(Directory.Exists(lockedDest));
        }

        Assert.False(allCopied);
        Assert.Equal("new-free", File.ReadAllText(Path.Combine(dest, "free.txt")));
    }

    private static string? LoadSource(params string[] relativeParts)
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        for (var i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(new[] { dir.FullName }.Concat(relativeParts).ToArray());
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
        }
        return null;
    }

    private static string StripLineComments(string src)
    {
        return string.Join('\n',
            src.Split('\n').Select(l =>
                l.Contains("//") ? l[..l.IndexOf("//")] : l));
    }
}
