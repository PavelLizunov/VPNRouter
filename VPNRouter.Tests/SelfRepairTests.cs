using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using VPNRouter.App.Services;
using Xunit;

namespace VPNRouter.Tests;

public sealed class SelfRepairTests
{
    [Theory]
    [InlineData(@"C:\Users\User\AppData\Local\Temp\vpnr-self-repair-20261026120000.ps1")]
    [InlineData(@"C:\Users\O'Brien\AppData\Local\Temp\vpnr-self-repair.ps1")]
    [InlineData(@"C:\Temp\R&D\vpnr-self-repair.ps1")]
    [InlineData(@"C:\Temp\100%\vpnr-self-repair.ps1")]
    [InlineData(@"C:\Temp\A^B\vpnr-self-repair.ps1")]
    [InlineData(@"C:\Users\User Name\AppData\Local\Temp\vpnr repair.ps1")]
    public void BuildProcessStartInfo_LegalPaths_ConfiguresSafeProcessStartInfo(string validPath)
    {
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
    [InlineData("test\0script.ps1")]
    [InlineData("test\rscript.ps1")]
    [InlineData("test\nscript.ps1")]
    [InlineData("test\"script.ps1")]
    public void BuildProcessStartInfo_PathWithDisallowedCharacters_ThrowsArgumentException(string maliciousPath)
    {
        var ex = Assert.Throws<ArgumentException>(() => SelfRepair.BuildProcessStartInfo(maliciousPath));
        Assert.Contains("Bootstrap path contains disallowed characters", ex.Message);
    }

    [Fact]
    public void BuildProcessStartInfo_LegalPathWithSpecialChars_ExecutesSuccessfullyOnWindows()
    {
        if (!OperatingSystem.IsWindows())
            return;

        // Bounded test: create a harmless script in a directory with spaces, apostrophes, ampersands, carets, and percents.
        // It simply writes a sentinel token to an output file to prove execution with exact arguments.
        var tempRoot = Path.Combine(Path.GetTempPath(), $"vpnr_test_O'Brien & Co ^ 100% {Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            var scriptPath = Path.Combine(tempRoot, "test_repair.ps1");
            var outputPath = Path.Combine(tempRoot, "test_output.txt");
            var expectedToken = Guid.NewGuid().ToString("N");

            // Avoid interpolating outputPath directly into the script literal to prevent syntax errors with apostrophes
            File.WriteAllText(scriptPath, $"Set-Content -LiteralPath (Join-Path $PSScriptRoot 'test_output.txt') -Value '{expectedToken}'");

            var psi = SelfRepair.BuildProcessStartInfo(scriptPath);

            using var process = Process.Start(psi);
            Assert.NotNull(process);

            var exited = process!.WaitForExit(10000);
            if (!exited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(2000);
                }
                catch
                {
                    // Ignore kill errors during test cleanup
                }
            }

            Assert.True(exited, "PowerShell execution should complete within bounded timeout");
            Assert.Equal(0, process.ExitCode);

            Assert.True(File.Exists(outputPath), "Output file should be created by the executed PowerShell script");
            var actualToken = File.ReadAllText(outputPath).Trim();
            Assert.Equal(expectedToken, actualToken);
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempRoot))
                    Directory.Delete(tempRoot, recursive: true);
            }
            catch
            {
                // Best effort cleanup of fixture directory
            }
        }
    }
}
