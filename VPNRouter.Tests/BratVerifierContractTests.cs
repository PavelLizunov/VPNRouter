namespace VPNRouter.Tests;

public sealed class BratVerifierContractTests
{
    [Fact]
    public void Script_PinsTheBratTarget_AndKeepsUiWorkInsideTheRemoteHelper()
    {
        var script = Read("tools", "brat-verify.ps1");

        Assert.Contains("192.168.0.106", script);
        Assert.Contains("WINBRAT", script);
        Assert.DoesNotContain("$TargetHost", script);
        Assert.DoesNotContain("$TestHost", script);
        Assert.DoesNotContain("$ComputerName", script);
        Assert.DoesNotContain("$HostName", script);

        Assert.Contains("New-PSSession", script);
        Assert.Contains("[Environment]::MachineName", script);

        const string begin = "BEGIN REMOTE IN-SESSION HELPER";
        const string end = "END REMOTE IN-SESSION HELPER";
        var b = script.IndexOf(begin, StringComparison.Ordinal);
        var e = script.IndexOf(end, StringComparison.Ordinal);
        Assert.True(b >= 0 && e > b, "brat-verify.ps1 must delimit its embedded remote helper with ordered BEGIN/END markers.");
        Assert.Equal(b, script.LastIndexOf(begin, StringComparison.Ordinal));
        Assert.Equal(e, script.LastIndexOf(end, StringComparison.Ordinal));

        var controller = script.Remove(b, e + end.Length - b);
        Assert.DoesNotContain("CopyFromScreen", controller);
        Assert.DoesNotContain("UIAutomationClient", controller);
        Assert.DoesNotContain("System.Windows.Automation", controller);
        Assert.DoesNotContain("mouse_event", controller);
        Assert.DoesNotContain("SendInput", controller);

        Assert.Contains("UIAutomationClient", script);
        Assert.Contains("CopyFromScreen", script);

        Assert.Contains("-LogonType Interactive", script);
        Assert.Contains("-RunLevel Highest", script);
        Assert.Contains("-FromSession", script);

        // Controller wait = UI match budget + 30 s transport/startup slack.
        Assert.Contains("$TimeoutSeconds + 30", script);
        // Result JSON is published atomically: .tmp write, then Move-Item -Force.
        Assert.Contains("$ResultPath.tmp", script);
        Assert.Contains("Move-Item", script);
        // tscon runs through a unique transient SYSTEM console task, no password.
        Assert.Contains("-UserId 'SYSTEM' -LogonType ServiceAccount", script);
        // Cleanup stops the transient task before unregister/delete.
        Assert.Contains("Stop-ScheduledTask", script);
        // UIA target prefers VPNRouter.GUI and only falls back to VPNRouter.App.
        var gui = script.IndexOf("Get-Process -Name VPNRouter.GUI", StringComparison.Ordinal);
        var app = script.IndexOf("Get-Process -Name VPNRouter.App", StringComparison.Ordinal);
        Assert.True(gui >= 0 && app > gui, "brat-verify.ps1 must prefer VPNRouter.GUI and fall back to VPNRouter.App.");

        // The deploy action must gate on the exact artifact + sidecar SHA256
        // before it ever contacts the brat box (fail closed on any problem).
        var deployStart = script.IndexOf("'deploy' {", StringComparison.Ordinal);
        var deployEnd = script.IndexOf("'uia' {", StringComparison.Ordinal);
        Assert.True(deployStart >= 0 && deployEnd > deployStart, "brat-verify.ps1 must keep a 'deploy' action before 'uia'.");
        var deploy = script.Substring(deployStart, deployEnd - deployStart);
        Assert.Contains("VPNRouter-v$Version-win.zip", deploy);
        Assert.Contains(".sha256", deploy);
        Assert.Contains("Get-FileHash", deploy);
        Assert.Contains("SHA256", deploy);
        Assert.Contains("mismatch", deploy);
        Assert.Contains("Failing closed", deploy);

        var skill = Read(".agents", "skills", "post-ship-mcp-verify", "SKILL.md");
        Assert.Contains("192.168.0.106", skill);
        Assert.Contains("NO local fallback", skill);
    }

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine(new[] { FindRoot() }.Concat(parts).ToArray()));

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "VPNRouter.sln")))
                return dir.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
