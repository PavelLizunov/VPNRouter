using System;
using System.Diagnostics;
using System.IO;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

public class ProcessImagePathTests
{
    [Fact]
    public void TryGetByPid_CurrentProcess_MatchesMainModulePath()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(),
            "QueryFullProcessImageName is Windows-only");

        using var self = Process.GetCurrentProcess();
        var expected = self.MainModule!.FileName;

        var actual = ProcessImagePath.TryGetByPid(self.Id);

        Assert.False(string.IsNullOrEmpty(actual));
        Assert.True(File.Exists(actual));
        Assert.Equal(Path.GetFullPath(expected), Path.GetFullPath(actual!), ignoreCase: true);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(0x7FFFFFF0)]
    public void TryGetByPid_InvalidPid_ReturnsNull(int pid)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows-only");
        Assert.Null(ProcessImagePath.TryGetByPid(pid));
    }

    [Fact]
    public void ResolveRunningPath_CurrentProcessByName_ResolvesToExistingFile()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows-only");

        using var self = Process.GetCurrentProcess();
        var name = self.ProcessName + ".exe";

        var path = ProcessImagePath.ResolveRunningPath(name);

        Assert.False(string.IsNullOrEmpty(path));
        Assert.True(File.Exists(path));
        Assert.Equal(self.ProcessName, Path.GetFileNameWithoutExtension(path), ignoreCase: true);
    }

    [Fact]
    public void ResolveRunningPath_WithoutExeSuffix_AlsoResolves()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows-only");

        using var self = Process.GetCurrentProcess();
        var path = ProcessImagePath.ResolveRunningPath(self.ProcessName);

        Assert.False(string.IsNullOrEmpty(path));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void ResolveRunningPath_DottedProcessName_StripsOnlyExeSuffix()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows-only");

        using var self = Process.GetCurrentProcess();
        Assert.SkipUnless(self.ProcessName.Contains('.'),
            $"test host '{self.ProcessName}' has no internal dot to exercise");

        var path = ProcessImagePath.ResolveRunningPath(self.ProcessName + ".exe");

        Assert.False(string.IsNullOrEmpty(path));
        Assert.True(File.Exists(path));
        Assert.Equal(self.ProcessName, Path.GetFileNameWithoutExtension(path), ignoreCase: true);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveRunningPath_NullOrWhitespace_ReturnsNull(string? name)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows-only");
        Assert.Null(ProcessImagePath.ResolveRunningPath(name));
    }

    [Fact]
    public void ResolveRunningPath_NonexistentProcess_ReturnsNull()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows-only");
        Assert.Null(ProcessImagePath.ResolveRunningPath(
            "VPNRouter_definitely_not_a_real_process_zzq.exe"));
    }
}
