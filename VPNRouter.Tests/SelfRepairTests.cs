using System;
using System.Diagnostics;
using System.Linq;
using VPNRouter.App.Services;
using Xunit;

namespace VPNRouter.Tests;

public sealed class SelfRepairTests
{
    [Fact]
    public void BuildProcessStartInfo_ValidPath_ConfiguresSafeProcessStartInfo()
    {
        const string validPath = @"C:\Users\User\AppData\Local\Temp\vpnr-self-repair-20261026120000.ps1";

        var psi = SelfRepair.BuildProcessStartInfo(validPath);

        Assert.NotNull(psi);
        Assert.Equal("powershell.exe", psi.FileName);
        Assert.False(psi.UseShellExecute);
        Assert.True(psi.CreateNoWindow);
        Assert.Equal(ProcessWindowStyle.Hidden, psi.WindowStyle);
        Assert.Equal(string.Empty, psi.Arguments);

        var expectedArgs = new[]
        {
            "-NoProfile",
            "-WindowStyle",
            "Hidden",
            "-ExecutionPolicy",
            "Bypass",
            "-File",
            validPath
        };
        Assert.Equal(expectedArgs, psi.ArgumentList.ToArray());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildProcessStartInfo_NullOrWhitespace_ThrowsArgumentException(string? invalidPath)
    {
        var ex = Assert.Throws<ArgumentException>(() => SelfRepair.BuildProcessStartInfo(invalidPath!));
        Assert.Contains("Bootstrap path cannot be null or empty", ex.Message);
    }

    [Theory]
    [InlineData("test\rscript.ps1")]
    [InlineData("test\nscript.ps1")]
    [InlineData("test&script.ps1")]
    [InlineData("test|script.ps1")]
    [InlineData("test^script.ps1")]
    [InlineData("test<script.ps1")]
    [InlineData("test>script.ps1")]
    [InlineData("test%script.ps1")]
    [InlineData("test\"script.ps1")]
    [InlineData("test'script.ps1")]
    public void BuildProcessStartInfo_PathWithMetacharacters_ThrowsArgumentException(string maliciousPath)
    {
        var ex = Assert.Throws<ArgumentException>(() => SelfRepair.BuildProcessStartInfo(maliciousPath));
        Assert.Contains("Bootstrap path contains disallowed shell metacharacters", ex.Message);
    }
}
