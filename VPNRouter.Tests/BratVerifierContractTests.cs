namespace VPNRouter.Tests;

public sealed class BratVerifierContractTests
{
    [Fact]
    public void Script_PinsTheBratTarget_AndKeepsUiWorkInsideTheRemoteHelper()
    {
        var script = Read("tools", "brat-verify.ps1");

        // Fixed transport endpoint is the Tailscale peer; the WINBRAT identity
        // check stays mandatory. The legacy LAN-era credential filename is
        // retained on purpose so no secret copy/migration is required.
        Assert.Contains("$BratIp          = '100.115.182.0'", script);
        Assert.Contains("$BratMachineName = 'WINBRAT'", script);
        Assert.Contains(".testpc-cred-192.168.0.106.xml", script);
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
        Assert.Contains("Remote helper cleanup failed", script);
        Assert.Contains("Remove-Item -LiteralPath $RequestPath", script);
        // UIA target prefers the real Avalonia app; VPNRouter.GUI is only the
        // bootstrap/update host and may remain alive without owning controls.
        var gui = script.IndexOf("Get-Process -Name VPNRouter.GUI", StringComparison.Ordinal);
        var app = script.IndexOf("Get-Process -Name VPNRouter.App", StringComparison.Ordinal);
        Assert.True(app >= 0 && gui > app, "brat-verify.ps1 must prefer VPNRouter.App and fall back to VPNRouter.GUI.");
        Assert.Contains("'Select'", script);
        Assert.Contains("SelectionItemPattern", script);
        Assert.Contains("if (-not $pat.Current.IsSelected)", script);

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
        // deploy hands the already-resolved credential to the generic deploy
        // script explicitly, so it never prompts or caches a new-IP credential.
        Assert.Contains("-Credential (Import-Clixml $CredFile)", deploy);
        Assert.Contains("-ExpectedMachineName $BratMachineName", deploy);

        var logsStart = script.IndexOf("'logs' {", StringComparison.Ordinal);
        Assert.True(logsStart >= 0, "brat-verify.ps1 must keep a remote logs action.");
        var logs = script.Substring(logsStart);
        Assert.Contains("LogWindowMinutes", logs);
        Assert.Contains("TryParseExact", logs);
        Assert.Contains("$maxLines = 50000", logs);
        Assert.Contains("verification window exceeds", logs);
        Assert.DoesNotContain("-Tail 1000", logs);
        Assert.Contains("recentEntryCount -eq 0", logs);
        Assert.Contains("Cannot verify recent remote logs", logs);

        var skill = Read(".agents", "skills", "post-ship-mcp-verify", "SKILL.md");
        Assert.Contains("100.115.182.0", skill);
        Assert.Contains("NO local fallback", skill);
    }

    [Fact]
    public void GenericDeploy_PinsIdentityBeforeMutation_AndFailsWhenLaunchFails()
    {
        var deploy = Read("deploy-to-testpc.ps1");

        Assert.Contains("[string]$ExpectedMachineName", deploy);
        var identity = deploy.IndexOf("$actualMachineName = Invoke-Command", StringComparison.Ordinal);
        var stop = deploy.IndexOf("Stopping running VPNRouter", StringComparison.Ordinal);
        Assert.True(identity >= 0 && stop > identity,
            "The deployment session must verify the expected machine before stopping processes.");
        Assert.Contains("throw \"VPNRouter.App.exe is not running", deploy);
    }

    [Fact]
    public void VerificationArtifacts_AreIgnored_AndCiGateScopesNativeStderrHandling()
    {
        var ignore = Read(".gitignore");
        var gate = Read("tools", "verify-last-commit-ci.ps1");

        Assert.Contains("/artifacts/brat-verify/", ignore);
        Assert.DoesNotContain("gh auth status", gate);

        var savePreference = gate.IndexOf("$previousErrorActionPreference = $ErrorActionPreference", StringComparison.Ordinal);
        var relaxPreference = gate.IndexOf("$ErrorActionPreference = \"Continue\"", StringComparison.Ordinal);
        var api = gate.IndexOf("$json = gh api $apiPath 2>&1", StringComparison.Ordinal);
        var captureExit = gate.IndexOf("$apiExitCode = $LASTEXITCODE", StringComparison.Ordinal);
        var restorePreference = gate.IndexOf("$ErrorActionPreference = $previousErrorActionPreference", StringComparison.Ordinal);
        var failClosed = gate.IndexOf("if ($apiExitCode -ne 0)", StringComparison.Ordinal);
        Assert.True(savePreference >= 0 && relaxPreference > savePreference && api > relaxPreference &&
                    captureExit > api && restorePreference > captureExit && failClosed > restorePreference,
            "The gh API call must scope native stderr handling, restore Stop, then fail closed.");
    }

    [Fact]
    public void TestVmControl_DefaultsToTailscaleEndpoint_AndProbesWinRmBeforeProxmoxInEnsureReady()
    {
        var testvm = Read("tools", "testvm-control.ps1");

        // Default WinRM transport endpoint is the fixed Tailscale peer.
        Assert.Contains("$VmIp = '100.115.182.0'", testvm);

        // ensure-ready probes the Tailscale WinRM endpoint before its first
        // Proxmox-backed Get-VmStatus call, so an already-reachable VM reports
        // ready with no Proxmox API/token requirement.
        var actionStart = testvm.IndexOf("'ensure-ready' {", StringComparison.Ordinal);
        Assert.True(actionStart >= 0, "testvm-control.ps1 must keep an 'ensure-ready' action.");
        var ensureReady = testvm.Substring(actionStart);

        var probe = ensureReady.IndexOf("Test-NetConnection -ComputerName $VmIp -Port 5985", StringComparison.Ordinal);
        var firstStatus = ensureReady.IndexOf("Get-VmStatus", StringComparison.Ordinal);
        Assert.True(probe >= 0, "ensure-ready must probe the Tailscale WinRM endpoint.");
        Assert.True(firstStatus > probe, "ensure-ready must probe WinRM before its first Get-VmStatus call.");
    }

    [Fact]
    public void Scripts_ResolveCredentials_LocalFirst_WithPrimaryWorktreeFallback_AndNeverCopyThem()
    {
        var brat = Read("tools", "brat-verify.ps1");
        var testvm = Read("tools", "testvm-control.ps1");

        foreach (var script in new[] { brat, testvm })
        {
            // Both standalone scripts carry the duplicated credential-resolution helper.
            var fnStart = script.IndexOf("function Resolve-CredentialFile", StringComparison.Ordinal);
            Assert.True(fnStart >= 0, "Each script must define Resolve-CredentialFile.");

            // Primary-worktree fallback via Git's common directory, anchored to the
            // checkout root ($LocalRoot) so it is independent of the caller's CWD.
            Assert.Contains("git -C $LocalRoot rev-parse --git-common-dir", script);

            // Local-first: the helper checks the current checkout before the git fallback.
            var fn = script.Substring(fnStart);
            var localCheck = fn.IndexOf("Test-Path $local", StringComparison.Ordinal);
            var gitFallback = fn.IndexOf("git -C $LocalRoot rev-parse --git-common-dir", StringComparison.Ordinal);
            Assert.True(localCheck >= 0 && gitFallback > localCheck,
                "Resolve-CredentialFile must check the local checkout (Test-Path $local) before the git common-dir fallback.");
        }

        // Fail-closed: the callers guard on the resolved path and throw when missing.
        Assert.Contains("Test-Path $CredFile", brat);
        Assert.Contains("Test-Path $TokenFile", testvm);

        // Credential files are never copied into task worktrees.
        Assert.DoesNotContain("Copy-Item", testvm);
        Assert.DoesNotContain("Copy-Item -Path $CredFile", brat);
        Assert.DoesNotContain("Copy-Item -Path $TokenFile", brat);
    }

    [Fact]
    public void ClaudeMirror_IsByteIdenticalToAgentsSkill_AndObsoleteLocalScriptsAreGone()
    {
        // The tracked .claude mirror must not drift from the .agents source of
        // truth (QF-9): CLAUDE.md rule #12 points Claude agents at the .claude
        // copy, so it must carry the exact remote-only windows-brat contract.
        // QF-10: compare raw bytes so BOM/encoding differences cannot escape.
        Assert.Equal(
            ReadBytes(".agents", "skills", "post-ship-mcp-verify", "SKILL.md"),
            ReadBytes(".claude", "skills", "post-ship-mcp-verify", "SKILL.md"));

        var checklists = new[]
        {
            "checklist-free-configs.md",
            "checklist-localization.md",
            "checklist-network-settings.md",
            "checklist-tgproxy.md",
            "checklist-vpn-core.md",
            "checklist-zapret.md",
        };

        foreach (var name in checklists)
        {
            Assert.Equal(
                ReadBytes(".agents", "skills", "post-ship-mcp-verify", "references", name),
                ReadBytes(".claude", "skills", "post-ship-mcp-verify", "references", name));
        }

        // The retired local-MCP scripts are gone; tools/brat-verify.ps1 is the
        // single driver and no replacement may reappear under the mirror.
        var root = FindRoot();
        Assert.False(File.Exists(Path.Combine(root, ".claude", "skills", "post-ship-mcp-verify", "scripts", "post-ship-install-launch.ps1")));
        Assert.False(File.Exists(Path.Combine(root, ".claude", "skills", "post-ship-mcp-verify", "scripts", "post-ship-collect-logs.ps1")));
    }

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine(new[] { FindRoot() }.Concat(parts).ToArray()));

    private static byte[] ReadBytes(params string[] parts) =>
        File.ReadAllBytes(Path.Combine(new[] { FindRoot() }.Concat(parts).ToArray()));

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
