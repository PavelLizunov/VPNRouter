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
}
