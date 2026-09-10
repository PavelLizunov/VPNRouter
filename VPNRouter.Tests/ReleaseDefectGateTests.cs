namespace VPNRouter.Tests;

public sealed class ReleaseDefectGateTests
{
    [Theory]
    [InlineData("## Open\n- [ ] **P0** blocker", 2)]
    [InlineData("## Open\n- [ ] **P1** blocker", 2)]
    [InlineData("## Open\n- [ ] **P1 SECURITY FOLLOW-UP (Ox Alpha review 2026-08-21)** blocker", 2)]
    [InlineData("## Open\n- [ ] **P1 CANDIDATE / MEASUREMENT-GATED** blocker", 2)]
    [InlineData("## Open\n- [ ] **P0 SECURITY** blocker", 2)]
    [InlineData("## Open\n- [x] **P1 SECURITY** resolved\n- [X] **P0** resolved\n- [ ] **P2** deferred", 0)]
    [InlineData("## Open\n- [ ] **P10** nonblocking\n- [ ] **P1foo** nonblocking", 0)]
    [InlineData("## Open\n### Follow-ups\n- [ ] **P1 SECURITY** blocker", 2)]
    [InlineData("## Open\n\n## Resolved\n- [ ] **P1** history", 0)]
    [InlineData("## Open\n", 0)]
    public void Gate_RecognizesSeverityAndSectionScope(string ledger, int expectedExitCode)
    {
        if (!OperatingSystem.IsWindows()) return;
        var result = RunGate(ledger, "");
        Assert.True(result.ExitCode == expectedExitCode, result.Output);
    }

    [Theory]
    [InlineData(false, "", "")]
    [InlineData(false, "", "owner-approved fixture waiver")]
    [InlineData(true, "", "")]
    [InlineData(true, "# Ledger\n## Resolved\n", "owner-approved fixture waiver")]
    [InlineData(true, "## Open\n## Open\n", "")]
    [InlineData(true, "## Open\n## Resolved\n## OPEN\n", "owner-approved fixture waiver")]
    public void Gate_RejectsInvalidStructureBeforeWaiver(bool createLedger, string ledger, string waiver)
    {
        if (!OperatingSystem.IsWindows()) return;
        var result = RunGate(ledger, waiver, createLedger);
        Assert.True(result.ExitCode == 3, result.Output);
        Assert.DoesNotContain("WAIVED", result.Output);
    }

    [Theory]
    [InlineData("owner-approved fixture waiver", 0)]
    [InlineData("   ", 2)]
    public void Gate_WaivesOnlyWithAnExplicitNonblankReason(string waiver, int expectedExitCode)
    {
        if (!OperatingSystem.IsWindows()) return;
        var result = RunGate("## Open\n- [ ] **P1 SECURITY FOLLOW-UP** blocker", waiver);
        Assert.True(result.ExitCode == expectedExitCode, result.Output);
        if (expectedExitCode == 0)
            Assert.Contains("WAIVED for this cut: " + waiver, result.Output);
        else
            Assert.DoesNotContain("WAIVED", result.Output);
    }

    private static (int ExitCode, string Output) RunGate(string ledger, string waiver, bool createLedger = true)
    {
        var sourceRoot = new DirectoryInfo(AppContext.BaseDirectory);
        while (sourceRoot != null && !File.Exists(Path.Combine(sourceRoot.FullName, "VPNRouter.sln")))
            sourceRoot = sourceRoot.Parent;
        Assert.NotNull(sourceRoot);

        var temp = Directory.CreateTempSubdirectory("vpnrouter-defect-gate-");
        try
        {
            var tools = Directory.CreateDirectory(Path.Combine(temp.FullName, "tools"));
            var plans = Directory.CreateDirectory(Path.Combine(temp.FullName, "plans"));
            var script = Path.Combine(tools.FullName, "check-open-p0.ps1");
            File.Copy(Path.Combine(sourceRoot!.FullName, "tools", "check-open-p0.ps1"), script);
            if (createLedger)
                File.WriteAllText(Path.Combine(plans.FullName, "OPEN-DEFECTS.md"), ledger);

            var shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe");
            var startInfo = new System.Diagnostics.ProcessStartInfo(shell)
            {
                WorkingDirectory = temp.FullName,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script })
                startInfo.ArgumentList.Add(argument);
            if (waiver.Length > 0)
            {
                startInfo.ArgumentList.Add("-Waive");
                startInfo.ArgumentList.Add(waiver);
            }

            using var process = System.Diagnostics.Process.Start(startInfo);
            Assert.NotNull(process);
            var stdout = process!.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(10_000))
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
                Assert.Fail("Isolated defect gate timed out.");
            }
            return (process.ExitCode, stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult());
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }
}
