using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using VPNRouter.App.Services;
using Xunit;

namespace VPNRouter.Tests;

public sealed class SelfRepairTests
{
#if VPNROUTER_BUSINESS
    [Fact]
    public void BusinessDoesNotDispatchOrEmbedTheStandardRepairInstaller()
    {
        Assert.False(SelfRepair.Plan().ShouldRun);
        Assert.DoesNotContain("VPNRouter.Windows.RepairBootstrap", typeof(SelfRepair).Assembly.GetManifestResourceNames());
    }
#else
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RepairCarriesTheExactApplicationDirectoryAndChannel(bool prerelease)
    {
        var fixture = Path.GetFullPath(Path.Combine("fixture", "O'Brien & Co ^ 100% 目标"));
        var system = Path.GetFullPath(Path.Combine("fixture", "Windows", "System32"));
        var start = SelfRepair.BuildProcessStartInfo(fixture, prerelease, system);
        var shell = Path.Combine(system, "WindowsPowerShell", "v1.0");
        Assert.Equal(Path.Combine(shell, "powershell.exe"), start.FileName);
        Assert.Equal(system, start.WorkingDirectory);
        Assert.False(start.UseShellExecute);
        Assert.True(start.CreateNoWindow);
        Assert.Equal(Path.Combine(shell, "Modules"), start.Environment["PSModulePath"]);
        Assert.Equal(Path.Combine(system, "cmd.exe"), start.Environment["ComSpec"]);
        Assert.DoesNotContain("DOTNET_STARTUP_HOOKS", start.Environment.Keys);
        Assert.Equal(new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand" },
            start.ArgumentList.Take(5).ToArray());
        var source = Encoding.Unicode.GetString(Convert.FromBase64String(start.ArgumentList[5]));
        Assert.DoesNotContain(fixture, source);
        Assert.Contains(Convert.ToBase64String(Encoding.UTF8.GetBytes(fixture)), source);
        Assert.DoesNotContain("__APPLICATION_DIR__", source);
        Assert.Contains(prerelease ? "$prerelease = $true" : "$prerelease = $false", source);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("relative")]
    [InlineData("line\nbreak")]
    [InlineData("double\"quote")]
    public void InvalidRepairDirectoryIsRejected(string? path)
    {
        Assert.ThrowsAny<ArgumentException>(() =>
            SelfRepair.BuildProcessStartInfo(path!, false, Path.GetFullPath("system")));
    }
#endif

    [Fact]
    public void AutomaticRepairDispatchUsesOfflineInstallerFixture()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "VPNRouter.sln"))) root = root.Parent;
        Assert.NotNull(root);
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"))
        {
            UseShellExecute = false,
            WorkingDirectory = Environment.SystemDirectory,
            CreateNoWindow = true
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File",
                     Path.Combine(root!.FullName, "VPNRouter.Tests", "Windows", "Test-AutomaticRepair.ps1") })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        Assert.True(process.WaitForExit(30_000));
        Assert.Equal(0, process.ExitCode);
    }
}
