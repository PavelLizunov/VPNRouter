using System.Diagnostics;
using System.IO;
using VPNRouter.App.Services;
using Xunit;

namespace VPNRouter.Tests;

public sealed class SelfRepairTests
{
    [Fact]
    public void BuildStartInfo_DisablesShellExecuteAndUsesArgumentList()
    {
        var testPath = Path.Combine(Path.GetTempPath(), "test script with spaces.ps1");
        var psi = SelfRepair.BuildStartInfo(testPath);

        Assert.Equal("powershell.exe", psi.FileName);
        Assert.False(psi.UseShellExecute, "UseShellExecute must be false to avoid shell parsing and injection vulnerabilities.");
        Assert.True(psi.CreateNoWindow);
        Assert.Equal(ProcessWindowStyle.Hidden, psi.WindowStyle);

        Assert.Contains("-NoProfile", psi.ArgumentList);
        Assert.Contains("-WindowStyle", psi.ArgumentList);
        Assert.Contains("Hidden", psi.ArgumentList);
        Assert.Contains("-ExecutionPolicy", psi.ArgumentList);
        Assert.Contains("Bypass", psi.ArgumentList);
        Assert.Contains("-File", psi.ArgumentList);

        var fileIndex = psi.ArgumentList.IndexOf("-File");
        Assert.True(fileIndex >= 0 && fileIndex < psi.ArgumentList.Count - 1);
        Assert.Equal(testPath, psi.ArgumentList[fileIndex + 1]);
    }
}
