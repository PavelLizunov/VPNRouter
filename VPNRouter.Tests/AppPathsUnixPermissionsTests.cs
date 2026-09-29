using System.Reflection;
using System.Runtime.InteropServices;
using VPNRouter.Core;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

public sealed class AppPathsUnixPermissionsTests
{
    [Fact]
    public void EnsureDirectories_UsesOwnerOnlyUnixModes()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;

        WithTemporaryDataDir(() =>
        {
            AppPaths.EnsureDirectories();

            Assert.Equal(AppPaths.PrivateUnixDirectoryMode, File.GetUnixFileMode(AppPaths.DataDir));
            Assert.Equal(AppPaths.PrivateUnixDirectoryMode, File.GetUnixFileMode(AppPaths.ConfigDir));
        });
    }

    [Fact]
    public void EnsureDirectories_RejectsSymbolicConfigDirectory()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;

        WithTemporaryDataDir(() =>
        {
            Directory.CreateDirectory(AppPaths.DataDir, AppPaths.PrivateUnixDirectoryMode);
            var target = Path.Combine(AppPaths.DataDir, "attacker-dir");
            Directory.CreateDirectory(target);
            Directory.CreateSymbolicLink(AppPaths.ConfigDir, target);

            Assert.Throws<IOException>(AppPaths.EnsureDirectories);
        });
    }

    private static void WithTemporaryDataDir(Action test)
    {
        var previous = AppPaths.DataDir;
        var temporary = Path.Combine(Path.GetTempPath(), $"vpnrouter-unix-mode-{Guid.NewGuid():N}");
        try
        {
            AppPaths.OverrideDataDir(temporary);
            test();
        }
        finally
        {
            AppPaths.OverrideDataDir(previous);
            try { if (Directory.Exists(temporary)) Directory.Delete(temporary, recursive: true); }
            catch { }
        }
    }

    [DllImport("libc", EntryPoint = "umask")]
    private static extern uint Umask(uint mask);
}
