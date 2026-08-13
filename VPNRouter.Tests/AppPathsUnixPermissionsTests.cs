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
    public void SettingsSave_UsesOwnerReadWriteUnixMode()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;

        WithTemporaryDataDir(() =>
        {
            AppPaths.EnsureDirectories();
            RealSettingsStore.Instance.Save(new AppSettings(), AppPaths.ConfigYamlPath);

            Assert.Equal(AppPaths.PrivateUnixFileMode, File.GetUnixFileMode(AppPaths.ConfigYamlPath));
        });
    }

    [Fact]
    public void SingBoxConfigWrite_UsesOwnerReadWriteUnixMode()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;

        WithTemporaryDataDir(() =>
        {
            AppPaths.EnsureDirectories();
            var writer = typeof(SingBoxManager).GetMethod(
                "WriteJsonToDisk",
                BindingFlags.Static | BindingFlags.NonPublic);

            Assert.NotNull(writer);
            var path = Assert.IsType<string>(writer.Invoke(null, ["{}"]));
            Assert.Equal(AppPaths.PrivateUnixFileMode, File.GetUnixFileMode(path));
        });
    }

    [Fact]
    public void CreatePrivateFile_RejectsSymbolicLinkWithoutTouchingTarget()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;

        WithTemporaryDataDir(() =>
        {
            AppPaths.EnsureDirectories();
            var target = Path.Combine(AppPaths.DataDir, "attacker-target");
            var link = Path.Combine(AppPaths.ConfigDir, "current.json");
            File.WriteAllText(target, "unchanged");
            File.CreateSymbolicLink(link, target);

            Assert.Throws<IOException>(() => AppPaths.WritePrivateText(link, "secret"));
            Assert.Equal("unchanged", File.ReadAllText(target));
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

    [Fact]
    public void EnsureDirectories_RemainsOwnerOnlyWithPermissiveUmask()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;

        var previousMask = Umask(0);
        try
        {
            WithTemporaryDataDir(() =>
            {
                AppPaths.EnsureDirectories();
                AppPaths.WritePrivateText(AppPaths.CurrentConfigPath, "{}");
                Assert.Equal(AppPaths.PrivateUnixDirectoryMode, File.GetUnixFileMode(AppPaths.DataDir));
                Assert.Equal(AppPaths.PrivateUnixDirectoryMode, File.GetUnixFileMode(AppPaths.ConfigDir));
                Assert.Equal(AppPaths.PrivateUnixFileMode, File.GetUnixFileMode(AppPaths.CurrentConfigPath));
            });
        }
        finally
        {
            Umask(previousMask);
        }
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
