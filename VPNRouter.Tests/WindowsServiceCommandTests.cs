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

    [Theory]
    [InlineData("VPNRouter.Service", "ServiceInstaller.cs")]
    [InlineData("VPNRouter.App", "Services", "WindowsServiceHelper.cs")]
    public void ServiceHelpers_UseStructuredSystemScArguments(params string[] parts)
    {
        var source = LoadSource(parts);

        Assert.Contains("WindowsServiceCommand.BuildCreateArguments", source);
        Assert.Contains("WindowsServiceCommand.BuildFailureRecoveryArguments", source);
        Assert.Contains("WindowsServiceCommand.GetSystemScPath()", source);
        Assert.Contains("psi.ArgumentList.Add(argument)", source);
        Assert.DoesNotContain("Arguments = arguments", source);
        Assert.DoesNotContain("FileName = \"sc.exe\"", source);
        Assert.DoesNotContain("RunSc($\"", source);
    }

    [Fact]
    public void DesktopSelfHeal_PreservesScReportedExecutableQuotes()
    {
        var source = LoadSource(
            "VPNRouter.App", "Services", "WindowsServiceHelper.cs");

        Assert.Contains("return line[(colon + 1)..].Trim();", source);
        Assert.DoesNotContain("Trim('\"')", source);
        Assert.Contains("WindowsServiceCommand.IsCurrentImagePath", source);
        Assert.Contains("WindowsServiceCommand.IsRecognizedVpnRouterImagePath", source);
        Assert.Contains("\"config\", ServiceName, \"binPath=\", expected", source);

        var startup = LoadSource("VPNRouter.App", "Program.cs");
        Assert.Contains("if (!healResult.Success", startup);
    }

    [Fact]
    public void ServiceHelpers_WireExactSharedBuilders()
    {
        var service = LoadSource("VPNRouter.Service", "ServiceInstaller.cs");
        var app = LoadSource(
            "VPNRouter.App", "Services", "WindowsServiceHelper.cs");

        Assert.Contains(
            "ServiceName, exePath, DisplayName, ServiceDependencies",
            service);
        Assert.Contains("ServiceName, exePath, DisplayName)", app);
        foreach (var source in new[] { service, app })
        {
            Assert.Contains(
                "BuildFailureRecoveryArguments(ServiceName)",
                source);
            Assert.Contains(
                "RunSc(\"description\", ServiceName, Description)",
                source);
        }
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

    [Fact]
    public void WindowsInboxTool_AuditedSourcesUseSystemScPath_NoBareSc()
    {
        var vmConnection = LoadSource("VPNRouter.App", "ViewModels", "MainWindowViewModel.Connection.cs");
        var healthCheck = LoadSource("VPNRouter.Core", "Services", "HealthCheck.cs");
        var diagExporter = LoadSource("VPNRouter.Core", "Services", "Diagnostics", "DiagnosticsExporter.cs");
        var zapretActions = LoadSource("VPNRouter.Core", "Services", "ZapretActions.cs");
        var zapretUpdater = LoadSource("VPNRouter.Core", "Services", "ZapretUpdater.cs");

        // Verify WindowsServiceCommand.GetSystemScPath usage
        Assert.Contains("WindowsServiceCommand.GetSystemScPath()", vmConnection);
        Assert.Contains("WindowsServiceCommand.GetSystemScPath()", healthCheck);
        Assert.Contains("WindowsServiceCommand.GetSystemScPath()", diagExporter);
        Assert.Contains("WindowsServiceCommand.GetSystemScPath()", zapretActions);
        Assert.Contains("WindowsServiceCommand.GetSystemScPath()", zapretUpdater);

        // Verify no bare "sc.exe" ProcessStartInfo constructors remain
        Assert.DoesNotContain("new ProcessStartInfo(\"sc.exe\"", healthCheck);
        Assert.DoesNotContain("new System.Diagnostics.ProcessStartInfo(\"sc.exe\"", vmConnection);
        Assert.DoesNotContain("new System.Diagnostics.ProcessStartInfo(\"sc\"", zapretUpdater);
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
