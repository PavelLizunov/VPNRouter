using System;
using System.IO;
using System.Linq;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

public sealed class WindowsServiceCommandTests
{
    [Fact]
    public void FormatImagePath_QuotesOnlyNormalizedExecutable()
    {
        var relative = Path.Combine("folder with space", "VPNRouter.Service.exe");
        var full = Path.GetFullPath(relative);

        var imagePath = WindowsServiceCommand.FormatImagePath(relative);

        Assert.Equal($"\"{full}\" --service", imagePath);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bad\"path.exe")]
    public void FormatImagePath_RejectsUnsafePath(string path)
    {
        Assert.ThrowsAny<ArgumentException>(
            () => WindowsServiceCommand.FormatImagePath(path));
    }

    [Fact]
    public void FormatImagePath_RejectsNull()
    {
        Assert.ThrowsAny<ArgumentException>(
            () => WindowsServiceCommand.FormatImagePath(null!));
    }

    [Fact]
    public void IsCurrentImagePath_RequiresPersistedExecutableQuotes()
    {
        var executable = Path.GetFullPath(Path.Combine(
            "Program Files", "VPNRouter", "VPNRouter.Service.exe"));
        var quoted = WindowsServiceCommand.FormatImagePath(executable);
        var legacyUnquoted = $"{executable} --service";

        Assert.True(WindowsServiceCommand.IsCurrentImagePath(quoted, executable));
        Assert.True(WindowsServiceCommand.IsCurrentImagePath(
            $"  {quoted.ToUpperInvariant()}  ", executable));
        Assert.False(WindowsServiceCommand.IsCurrentImagePath(legacyUnquoted, executable));
        Assert.False(WindowsServiceCommand.IsCurrentImagePath(null, executable));
    }

    [Fact]
    public void RecognizedImagePath_AllowsOnlyVpnRouterServiceContract()
    {
        var executable = Path.GetFullPath(Path.Combine(
            "old install", "VPNRouter.Service.exe"));
        var current = $"\"{executable}\" --service";
        var legacyUnquoted = $"{executable} --service";
        var legacyWholeQuoted = $"\"{executable} --service\"";

        AssertRecognized(current, executable);
        AssertRecognized(legacyUnquoted, executable);
        AssertRecognized(legacyWholeQuoted, executable);

        Assert.False(WindowsServiceCommand.IsRecognizedVpnRouterImagePath(
            $"\"{Path.Combine(Path.GetDirectoryName(executable)!, "foreign.exe")}\" --service",
            out _));
        Assert.False(WindowsServiceCommand.IsRecognizedVpnRouterImagePath(
            $"\"{executable}\" --service --extra",
            out _));
        Assert.False(WindowsServiceCommand.IsRecognizedVpnRouterImagePath(
            "VPNRouter.Service.exe --service",
            out _));
    }

    [Fact]
    public void CreateAndFailureArguments_AreExactOrderedContracts()
    {
        var executable = Path.GetFullPath(Path.Combine(
            "Program Files", "VPNRouter", "VPNRouter.Service.exe"));
        var imagePath = WindowsServiceCommand.FormatImagePath(executable);

        Assert.Equal(
            new[]
            {
                "create", "VPNRouter",
                "binPath=", imagePath,
                "start=", "auto",
                "obj=", "LocalSystem",
                "DisplayName=", "VPN Process Router"
            },
            WindowsServiceCommand.BuildCreateArguments(
                "VPNRouter", executable, "VPN Process Router"));

        Assert.Equal(
            new[]
            {
                "create", "VPNRouter",
                "binPath=", imagePath,
                "start=", "auto",
                "obj=", "LocalSystem",
                "depend=", "Tcpip/Dnscache/Dhcp",
                "DisplayName=", "VPN Process Router"
            },
            WindowsServiceCommand.BuildCreateArguments(
                "VPNRouter", executable, "VPN Process Router", "Tcpip/Dnscache/Dhcp"));

        Assert.Equal(
            new[]
            {
                "failure", "VPNRouter",
                "reset=", "86400",
                "actions=", "restart/60000/restart/60000/restart/60000"
            },
            WindowsServiceCommand.BuildFailureRecoveryArguments("VPNRouter"));
    }

    [Fact]
    public void GetSystemScPath_UsesKnownWindowsSystemDirectory()
    {
        if (!OperatingSystem.IsWindows()) return;

        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "sc.exe");
        Assert.Equal(expected, WindowsServiceCommand.GetSystemScPath());
        Assert.True(Path.IsPathFullyQualified(expected));
    }

    [Fact]
    public void GetSystemScPath_ResolvesSystem32OrThrowsOnNonWindows()
    {
        if (OperatingSystem.IsWindows())
        {
            var path = WindowsServiceCommand.GetSystemScPath();
            Assert.True(Path.IsPathFullyQualified(path));
            Assert.EndsWith(Path.Combine("System32", "sc.exe"), path, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            Assert.Throws<PlatformNotSupportedException>(() => WindowsServiceCommand.GetSystemScPath());
        }
    }

    private static string LoadSource(params string[] relativeParts)
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        for (var i = 0; i < 8 && directory != null; i++, directory = directory.Parent)
        {
            var candidate = Path.Combine(
                new[] { directory.FullName }.Concat(relativeParts).ToArray());
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
        }
        throw new FileNotFoundException(
            $"Could not locate repository source: {Path.Combine(relativeParts)}");
    }

    private static void AssertRecognized(string imagePath, string expectedExecutable)
    {
        Assert.True(WindowsServiceCommand.IsRecognizedVpnRouterImagePath(
            imagePath,
            out var actualExecutable));
        Assert.Equal(expectedExecutable, actualExecutable);
    }
}
