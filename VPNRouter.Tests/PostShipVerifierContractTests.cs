#nullable enable

namespace VPNRouter.Tests;

public sealed class PostShipVerifierContractTests
{
    [Fact]
    public void BratRoutePlan_BehavioralFixturesPass()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var root = FindRepoRoot();
        var helper = Path.Combine(root, "tools", "brat-route-plan.ps1");
        var result = RunPowerShell(root, helper, Path.GetDirectoryName(helper)!, new[] { "-SelfTest" });

        Assert.True(result.ExitCode == 0,
            $"stdout={result.Stdout}{Environment.NewLine}stderr={result.Stderr}");
        Assert.Contains("\"Status\":\"PASS\"", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void CiGate_StrictRejectsCancelledPlatformAndIgnoresFailureWaiver()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var sourceRoot = FindRepoRoot();
        var temp = Directory.CreateTempSubdirectory("vpnrouter-ci-strict-");
        try
        {
            var root = temp.FullName;
            var fakes = Directory.CreateDirectory(Path.Combine(root, "fakes")).FullName;
            var gate = Path.Combine(root, "verify-last-commit-ci.ps1");
            File.Copy(Path.Combine(sourceRoot, "tools", "verify-last-commit-ci.ps1"), gate);
            File.WriteAllText(Path.Combine(fakes, "git.cmd"), """
                @echo off
                echo 1111111111111111111111111111111111111111
                exit /b 0
                """);
            File.WriteAllText(Path.Combine(fakes, "gh.cmd"), """
                @echo off
                if exist "%~dp0first-api.done" goto workflows
                type nul > "%~dp0first-api.done"
                type "%~dp0checks.json"
                goto end
                :workflows
                type "%~dp0workflows.json"
                :end
                exit /b 0
                """);
            File.WriteAllText(Path.Combine(fakes, "checks.json"), """
                {"check_runs":[
                  {"name":"build","status":"completed","conclusion":"success","html_url":"https://example.invalid/1"},
                  {"name":"build","status":"completed","conclusion":"success","html_url":"https://example.invalid/2"},
                  {"name":"build","status":"completed","conclusion":"success","html_url":"https://example.invalid/3"},
                  {"name":"build","status":"completed","conclusion":"cancelled","html_url":"https://example.invalid/4"},
                  {"name":"test","status":"completed","conclusion":"success","html_url":"https://example.invalid/5"},
                  {"name":"test","status":"completed","conclusion":"failure","html_url":"https://example.invalid/6"}
                ]}
                """);
            File.WriteAllText(Path.Combine(fakes, "workflows.json"), """
                {"workflow_runs":[]}
                """);

            var result = RunPowerShell(
                root,
                gate,
                fakes,
                new[] { "-Commit", "HEAD", "-RequiredSuccess", "build=3,test=1", "-Strict" },
                new Dictionary<string, string?>
                {
                    ["TOLERATE_FAILURE"] = "test",
                    ["TOLERATE_REASON"] = "must not apply to strict release verification",
                });

            Assert.True(result.ExitCode == 1,
                $"exit={result.ExitCode}{Environment.NewLine}stdout={result.Stdout}{Environment.NewLine}stderr={result.Stderr}");
            Assert.Contains("build [cancelled]", result.Stdout, StringComparison.Ordinal);
            Assert.Contains("https://example.invalid/6", result.Stdout, StringComparison.Ordinal);
            Assert.DoesNotContain("failure, tolerated", result.Stdout, StringComparison.Ordinal);
        }
        finally
        {
            DeleteBestEffort(temp);
        }
    }

    [Fact]
    public void CiGate_StrictRequiresExactPlatformWorkflows()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var sourceRoot = FindRepoRoot();
        var temp = Directory.CreateTempSubdirectory("vpnrouter-ci-workflows-");
        try
        {
            var root = temp.FullName;
            var fakes = Directory.CreateDirectory(Path.Combine(root, "fakes")).FullName;
            var gate = Path.Combine(root, "verify-last-commit-ci.ps1");
            File.Copy(Path.Combine(sourceRoot, "tools", "verify-last-commit-ci.ps1"), gate);
            File.WriteAllText(Path.Combine(fakes, "git.cmd"), """
                @echo off
                echo 1111111111111111111111111111111111111111
                exit /b 0
                """);
            File.WriteAllText(Path.Combine(fakes, "gh.cmd"), """
                @echo off
                if exist "%~dp0first-api.done" goto workflows
                type nul > "%~dp0first-api.done"
                type "%~dp0checks.json"
                goto end
                :workflows
                type "%~dp0workflows.json"
                :end
                exit /b 0
                """);
            File.WriteAllText(Path.Combine(fakes, "checks.json"), """
                {"check_runs":[
                  {"name":"test","status":"completed","conclusion":"success","html_url":"https://example.invalid/test"}
                ]}
                """);
            File.WriteAllText(Path.Combine(fakes, "workflows.json"), """
                {"workflow_runs":[
                  {"name":"Build macOS DMG","head_sha":"1111111111111111111111111111111111111111","status":"completed","conclusion":"success"},
                  {"name":"Build macOS DMG","head_sha":"1111111111111111111111111111111111111111","status":"completed","conclusion":"success"},
                  {"name":"Build macOS DMG","head_sha":"1111111111111111111111111111111111111111","status":"completed","conclusion":"success"}
                ]}
                """);

            var result = RunPowerShell(
                root,
                gate,
                fakes,
                new[]
                {
                    "-Commit", "HEAD",
                    "-RequiredSuccess", "test=1",
                    "-RequiredWorkflows", "Build macOS DMG,Build Linux AppImage + .deb",
                    "-Strict",
                });

            Assert.True(result.ExitCode == 1,
                $"exit={result.ExitCode}{Environment.NewLine}stdout={result.Stdout}{Environment.NewLine}stderr={result.Stderr}");
            Assert.Contains("workflow 'Build Linux AppImage + .deb'", result.Stdout, StringComparison.Ordinal);
            Assert.DoesNotContain("workflow 'Build macOS DMG' [required", result.Stdout, StringComparison.Ordinal);
        }
        finally
        {
            DeleteBestEffort(temp);
        }
    }

    [Fact]
    public void CiGate_StrictFailsClosedWhenCommitCannotBeResolved()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var sourceRoot = FindRepoRoot();
        var temp = Directory.CreateTempSubdirectory("vpnrouter-ci-unresolved-");
        try
        {
            var root = temp.FullName;
            var fakes = Directory.CreateDirectory(Path.Combine(root, "fakes")).FullName;
            var gate = Path.Combine(root, "verify-last-commit-ci.ps1");
            File.Copy(Path.Combine(sourceRoot, "tools", "verify-last-commit-ci.ps1"), gate);
            File.WriteAllText(Path.Combine(fakes, "git.cmd"), "@exit /b 1");

            var result = RunPowerShell(
                root,
                gate,
                fakes,
                new[] { "-Commit", "HEAD", "-Strict" });

            Assert.Equal(3, result.ExitCode);
            Assert.Contains("ERROR: could not resolve commit reference", result.Stdout, StringComparison.Ordinal);
        }
        finally
        {
            DeleteBestEffort(temp);
        }
    }

    [Fact]
    public void PostShipVerifier_GreenCiChildContinuesThroughDeployCyclesAndCleanup()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var sourceRoot = FindRepoRoot();
        var temp = Directory.CreateTempSubdirectory("vpnrouter-postship-");
        try
        {
            var root = temp.FullName;
            var tools = Directory.CreateDirectory(Path.Combine(root, "tools")).FullName;
            var tests = Directory.CreateDirectory(Path.Combine(root, "VPNRouter.Tests")).FullName;
            var fakes = Directory.CreateDirectory(Path.Combine(root, "fakes")).FullName;
            var trace = Path.Combine(root, "trace.txt");
            const string version = "9.9.9-r1";
            var zipName = $"VPNRouter-v{version}-win.zip";
            var hashName = $"{zipName}.sha256";
            var updateZipName = $"VPNRouter-update-v{version}-win.zip";
            var updateHashName = $"{updateZipName}.sha256";

            File.Copy(
                Path.Combine(sourceRoot, "tools", "post-ship-verify.ps1"),
                Path.Combine(tools, "post-ship-verify.ps1"));
            File.WriteAllText(Path.Combine(root, "global.json"), "{\"sdk\":{\"version\":\"10.0.301\"}}");
            File.WriteAllText(Path.Combine(tests, "VPNRouter.Tests.csproj"), "<Project />");

            var sourceZip = Path.Combine(fakes, "source-install.zip");
            CreateTrueSplitZip(sourceZip, "app");
            var payload = File.ReadAllBytes(sourceZip);
            File.WriteAllText(
                Path.Combine(fakes, "source-install.sha256"),
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(payload)).ToLowerInvariant());
            var sourceUpdateZip = Path.Combine(fakes, "source-update.zip");
            CreateTrueSplitZip(sourceUpdateZip, "_bootstrap");
            var updatePayload = File.ReadAllBytes(sourceUpdateZip);
            File.WriteAllText(
                Path.Combine(fakes, "source-update.sha256"),
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(updatePayload)).ToLowerInvariant());

            var expectedAssets = new[]
            {
                zipName, hashName, updateZipName, updateHashName,
                $"VPNRouter-v{version}-mac.dmg", $"VPNRouter-v{version}-mac.dmg.sha256",
                $"VPNRouter-v{version}-mac.zip", $"VPNRouter-v{version}-mac.zip.sha256",
                $"VPNRouter-v{version}-linux.tar.gz", $"VPNRouter-v{version}-linux.tar.gz.sha256",
                $"VPNRouter-v{version}-linux-amd64.deb", $"VPNRouter-v{version}-linux-amd64.deb.sha256",
                $"VPNRouter-v{version}-linux-x86_64.AppImage", $"VPNRouter-v{version}-linux-x86_64.AppImage.sha256",
                $"VPNRouter-v{version}-android-arm64.apk", $"VPNRouter-v{version}-android-arm64.apk.sha256",
            };
            File.WriteAllText(
                Path.Combine(fakes, "release.json"),
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    assets = expectedAssets.Select(name => new { name }).ToArray(),
                }));

            File.WriteAllText(Path.Combine(fakes, "dotnet.ps1"), """
                if ($args[0] -eq '--version') {
                    Write-Output '10.0.301'
                    $global:LASTEXITCODE = 0
                    return
                }
                Add-Content -LiteralPath $env:POSTSHIP_TRACE -Value "dotnet:$($args -join ' ')"
                $global:LASTEXITCODE = 0
                """);
            File.WriteAllText(Path.Combine(fakes, "gh.cmd"), """
                @echo off
                powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0gh-fake.ps1" %*
                exit /b %errorlevel%
                """);
            File.WriteAllText(Path.Combine(fakes, "gh-fake.ps1"), $$"""
                Add-Content -LiteralPath $env:POSTSHIP_TRACE -Value "gh:$($args -join ' ')"
                if ($args[0] -eq 'api') {
                  Write-Output '1111111111111111111111111111111111111111'
                  exit 0
                }
                if ($args[0] -eq 'release' -and $args[1] -eq 'view') {
                  Get-Content -LiteralPath (Join-Path $PSScriptRoot 'release.json') -Raw
                  exit 0
                }
                $dirIndex = [Array]::IndexOf($args, '--dir')
                if ($dirIndex -lt 0) { exit 2 }
                $destination = $args[$dirIndex + 1]
                New-Item -ItemType Directory -Path $destination -Force | Out-Null
                Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'source-install.zip') -Destination (Join-Path $destination '{{zipName}}') -Force
                Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'source-install.sha256') -Destination (Join-Path $destination '{{hashName}}') -Force
                Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'source-update.zip') -Destination (Join-Path $destination '{{updateZipName}}') -Force
                Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'source-update.sha256') -Destination (Join-Path $destination '{{updateHashName}}') -Force
                exit 0
                """);
            File.WriteAllText(Path.Combine(fakes, "git.cmd"), """
                @echo off
                >>"%POSTSHIP_TRACE%" echo git:%*
                echo %* | findstr /C:"rev-parse HEAD" >nul
                if not errorlevel 1 echo 1111111111111111111111111111111111111111
                exit /b 0
                """);
            File.WriteAllText(Path.Combine(tools, "verify-last-commit-ci.ps1"), """
                param([string]$Commit, [string]$Repo, [string]$IgnoreSkipped, [string]$RequiredSuccess, [string]$RequiredWorkflows, [switch]$Strict)
                Add-Content -LiteralPath $env:POSTSHIP_TRACE -Value "ci:$Commit`:$Repo`:$Strict"
                exit 0
                """);
            File.WriteAllText(Path.Combine(tools, "brat-verify.ps1"), """
                param([string]$Action, [string]$Version)
                Add-Content -LiteralPath $env:POSTSHIP_TRACE -Value "verify:$Action"
                if ($Action -eq 'state') { '{"AtUtc":"2026-08-12T12:00:00.0000000+00:00"}' }
                """);
            File.WriteAllText(Path.Combine(tools, "brat-stability.ps1"), """
                param([string]$Mode, [string]$Version, [int]$Cycles, [string]$RunSinceUtc)
                Add-Content -LiteralPath $env:POSTSHIP_TRACE -Value "stability:$Mode"
                """);

            var shell = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe");
            var startInfo = new System.Diagnostics.ProcessStartInfo(shell)
            {
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(Path.Combine(tools, "post-ship-verify.ps1"));
            startInfo.ArgumentList.Add("-Version");
            startInfo.ArgumentList.Add(version);
            startInfo.Environment["DOTNET_HOST_PATH"] = Path.Combine(fakes, "dotnet.ps1");
            startInfo.Environment["POSTSHIP_TRACE"] = trace;
            startInfo.Environment["PATH"] = fakes + Path.PathSeparator + startInfo.Environment["PATH"];

            using var process = System.Diagnostics.Process.Start(startInfo);
            Assert.NotNull(process);
            Assert.True(process!.WaitForExit(30_000), "Mocked post-ship verifier timed out.");
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            var observedTrace = File.Exists(trace) ? string.Join(" | ", File.ReadAllLines(trace)) : "<missing>";
            var observedFiles = string.Join(" | ", Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories));
            Assert.True(process.ExitCode == 0,
                $"stdout: {stdout}{Environment.NewLine}stderr: {stderr}{Environment.NewLine}" +
                $"trace: {observedTrace}{Environment.NewLine}files: {observedFiles}");
            Assert.Contains("\"Status\":\"PASS\"", stdout, StringComparison.Ordinal);

            var calls = File.ReadAllLines(trace);
            AssertOrdered(calls,
                "gh:api",
                "git:-C",
                "dotnet:test",
                "ci:1111111111111111111111111111111111111111:PavelLizunov/VPNRouter:True",
                "verify:identity",
                "gh:release view",
                "gh:release download",
                "verify:deploy",
                "stability:ColdCycles",
                "stability:Cleanup");
            Assert.Equal(
                File.ReadAllBytes(Path.Combine(fakes, "source-install.zip")),
                File.ReadAllBytes(Path.Combine(root, zipName)));
            Assert.Equal(
                File.ReadAllBytes(Path.Combine(fakes, "source-update.zip")),
                File.ReadAllBytes(Path.Combine(root, "artifacts", "post-ship", version, "release", updateZipName)));
        }
        finally
        {
            DeleteBestEffort(temp);
        }
    }

    [Fact]
    public void BratVerify_StateProbeLifecycleActions_AreFixedTargetAndRedacted()
    {
        var source = ReadRepoFile("tools", "brat-verify.ps1");

        Assert.Contains("'state'", source, StringComparison.Ordinal);
        Assert.Contains("'diagnose'", source, StringComparison.Ordinal);
        Assert.Contains("'probe'", source, StringComparison.Ordinal);
        Assert.Contains("'lifecycle'", source, StringComparison.Ordinal);
        Assert.Contains("'emergencycleanup'", source, StringComparison.Ordinal);
        Assert.Contains("$BratIp          = '100.115.182.0'", source, StringComparison.Ordinal);
        Assert.Contains("$BratMachineName = 'WINBRAT'", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ProbeHost", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ProbeUrl", source, StringComparison.Ordinal);

        var state = Slice(source, "    'state' {", "    'probe' {");
        Assert.Contains("GuiCount", state, StringComparison.Ordinal);
        Assert.Contains("CoreCount", state, StringComparison.Ordinal);
        Assert.Contains("TunState", state, StringComparison.Ordinal);
        Assert.Contains("RouteScope", state, StringComparison.Ordinal);
        Assert.DoesNotContain("ProcessId", state, StringComparison.Ordinal);
        Assert.DoesNotContain("CommandLine", state, StringComparison.Ordinal);
        Assert.DoesNotContain("PSComputerName", state, StringComparison.Ordinal);

        var probe = Slice(source, "    'probe' {", "    'lifecycle' {");
        var routeGate = probe.IndexOf("if ($routeScope -ne 'Tunnel')", StringComparison.Ordinal);
        Assert.True(routeGate >= 0);
        Assert.True(probe.IndexOf("$proxyHttp = New-Object System.Net.Http.HttpClient", StringComparison.Ordinal) > routeGate);
        Assert.True(probe.IndexOf("$udp = New-Object System.Net.Sockets.UdpClient", StringComparison.Ordinal) > routeGate);
        Assert.Contains("stun.cloudflare.com", probe, StringComparison.Ordinal);
        Assert.Contains("function New-StunBindingRequest", probe, StringComparison.Ordinal);
        Assert.Contains("@(20, 64, 512, 1200, 1392)", probe, StringComparison.Ordinal);
        Assert.Contains("/proxies/proxy/delay", probe, StringComparison.Ordinal);
        Assert.DoesNotContain("$http.GetAsync", probe, StringComparison.Ordinal);
        Assert.Contains("/connections", probe, StringComparison.Ordinal);
        Assert.Contains("[int]$_.metadata.destinationPort -eq $destinationPort", probe, StringComparison.Ordinal);
        Assert.Contains("$expectedUdpTag", probe, StringComparison.Ordinal);
        Assert.Contains("Resolve-UdpProbePlan $config", probe, StringComparison.Ordinal);
        Assert.Contains("Test-ProxyCapableChain 1 @('proxy') 'proxy' @($config.outbounds)", probe, StringComparison.Ordinal);
        Assert.Contains("Copy-Item -LiteralPath (Join-Path $PSHOME 'powershell.exe')", probe, StringComparison.Ordinal);
        Assert.Contains("if ($child -and -not $child.HasExited)", probe, StringComparison.Ordinal);
        Assert.Contains("$row.Success = $valid -and $row.ProxyObserved -and -not $row.DirectObserved", probe, StringComparison.Ordinal);
        Assert.Contains("[string]$_.metadata.sourceIP -eq $sourceIp", probe, StringComparison.Ordinal);
        Assert.Contains("Test-ProxyCapableChain $connection.Count $chains $expectedTag $outbounds", probe, StringComparison.Ordinal);
        Assert.Contains("ProxyObserved", probe, StringComparison.Ordinal);
        Assert.Contains("UnverifiedOutbound", probe, StringComparison.Ordinal);
        Assert.Contains("$proxyResult.Success -and $httpResult.Success", probe, StringComparison.Ordinal);
        Assert.Contains("'^127\\.0\\.0\\.1:", probe, StringComparison.Ordinal);
        Assert.DoesNotContain("Secret     =", probe, StringComparison.Ordinal);

        var cleanup = Slice(source, "    'emergencycleanup' {", "    'tuninventory' {");
        Assert.Contains("$ownedPaths -icontains", cleanup, StringComparison.Ordinal);
        Assert.Contains("CoreCount", cleanup, StringComparison.Ordinal);
        Assert.Contains("TunAbsent", cleanup, StringComparison.Ordinal);
        Assert.DoesNotContain("CommandLine", cleanup, StringComparison.Ordinal);

        var lifecycle = Slice(source, "    'lifecycle' {", "    'logs' {");
        Assert.Contains("EventCounts", lifecycle, StringComparison.Ordinal);
        Assert.Contains("UnknownErrorCount", lifecycle, StringComparison.Ordinal);
        Assert.DoesNotContain("Hits =", lifecycle, StringComparison.Ordinal);
        Assert.DoesNotContain("File =", lifecycle, StringComparison.Ordinal);
        Assert.Contains("$oldestTimestamp -ge $since", lifecycle, StringComparison.Ordinal);
    }

    [Fact]
    public void BratStability_DelegatesRemoteWorkAndCompletesCyclesBeforeDataplaneFailure()
    {
        var source = ReadRepoFile("tools", "brat-stability.ps1");
        var forbidden = new[]
        {
            "New-PSSession",
            "Invoke-Command",
            "UIAutomation",
            "Get-NetAdapter",
            "Find-NetRoute",
            "Get-Process",
            "Get-CimInstance",
            "Copy-Item",
        };

        Assert.Contains("brat-verify.ps1", source, StringComparison.Ordinal);
        foreach (var token in forbidden)
            Assert.DoesNotContain(token, source, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("finally {", source, StringComparison.Ordinal);
        Assert.Contains("Ensure-Disconnected", source, StringComparison.Ordinal);
        Assert.Contains("Action = 'emergencycleanup'", source, StringComparison.Ordinal);
        Assert.Contains("EmergencyCleanState", source, StringComparison.Ordinal);
        Assert.Contains("$script:DataPlaneFailed = $true", source, StringComparison.Ordinal);
        Assert.Contains("if ($DataPlaneFailed)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Cold cycle $cycle boundary probe failed", source, StringComparison.Ordinal);
        Assert.Contains("CycleLifecyclePassed", source, StringComparison.Ordinal);
        Assert.Contains("[string]$State.TunState -eq 'Absent'", source, StringComparison.Ordinal);
        Assert.Contains("[string]$State.RouteScope -eq 'Tunnel'", source, StringComparison.Ordinal);
        Assert.Contains("function Assert-CleanLifecycle", source, StringComparison.Ordinal);
        Assert.Contains("$Lifecycle.ErrorCount -gt 0", source, StringComparison.Ordinal);
        Assert.Contains("RunLifecyclePassed", source, StringComparison.Ordinal);
        Assert.Contains("Whole verification run", source, StringComparison.Ordinal);
        foreach (var kind in new[]
                 {
                     "HealthFailed", "CoreWedged", "RestartRequested", "RestartSucceeded",
                     "FailoverRequested", "FailoverCommitted",
                 })
            Assert.Contains($"'{kind}'", source, StringComparison.Ordinal);
    }

    [Fact]
    public void PostShipVerifier_RunsVisualAndTwoCycleVpnGatesWithCleanup()
    {
        var source = ReadRepoFile("tools", "post-ship-verify.ps1");
        Assert.Contains("(?:-r[1-9][0-9]*)?", source, StringComparison.Ordinal);
        var ciGate = ReadRepoFile("tools", "verify-last-commit-ci.ps1");
        var gitignore = ReadRepoFile(".gitignore");
        var pageScreenshots = ReadRepoFile("VPNRouter.Tests", "PageScreenshotTests.cs");
        var visualDiff = ReadRepoFile("VPNRouter.Tests", "VisualDiffTests.cs");
        var testSafety = ReadRepoFile("VPNRouter.Tests", "TestEnvironmentSafety.cs");

        var visual = source.IndexOf("PageScreenshotTests|FullyQualifiedName~VisualDiffTests", StringComparison.Ordinal);
        var ci = source.IndexOf("$CiGate", visual, StringComparison.Ordinal);
        var identity = source.IndexOf("-Action identity", ci, StringComparison.Ordinal);
        var deploy = source.IndexOf("-Action deploy", identity, StringComparison.Ordinal);
        var coldCycles = source.IndexOf("-Mode ColdCycles", deploy, StringComparison.Ordinal);
        Assert.True(visual >= 0 && ci > visual && identity > ci && deploy > identity && coldCycles > deploy);

        Assert.Contains("[ValidateRange(2, 10)]", source, StringComparison.Ordinal);
        Assert.Contains("[int]$Cycles = 2", source, StringComparison.Ordinal);
        Assert.Contains("finally {", source, StringComparison.Ordinal);
        Assert.Contains("-Mode Cleanup", source, StringComparison.Ordinal);
        Assert.Contains("$RemoteMutationStarted = $true", source, StringComparison.Ordinal);
        Assert.Contains("if ($RemoteMutationStarted)", source, StringComparison.Ordinal);
        Assert.Contains("'Local\\VPNRouterBratStability'", source, StringComparison.Ordinal);
        Assert.Contains("$GateMutex.WaitOne(0)", source, StringComparison.Ordinal);
        Assert.Contains("catch [System.Threading.AbandonedMutexException]", source, StringComparison.Ordinal);
        Assert.Contains("$GateMutex.ReleaseMutex()", source, StringComparison.Ordinal);
        Assert.Contains("'Fresh release artifact download'", source, StringComparison.Ordinal);
        Assert.Contains("'--clobber'", source, StringComparison.Ordinal);
        Assert.Contains("function Get-Sha256Hex", source, StringComparison.Ordinal);
        Assert.Contains("function Assert-TrueSplitBundle", source, StringComparison.Ordinal);
        Assert.Contains("$prefix/driver/mullvad-split-tunnel.sys", source, StringComparison.Ordinal);
        Assert.Contains("$prefix/driver/mullvad-split-tunnel.cat", source, StringComparison.Ordinal);
        Assert.Contains("$prefix/driver/mullvad-split-tunnel.inf", source, StringComparison.Ordinal);
        Assert.Contains("Assert-TrueSplitBundle -Path $FreshZipPath -ArchivePrefix 'app'", source, StringComparison.Ordinal);
        Assert.Contains("Assert-TrueSplitBundle -Path $FreshUpdateZipPath -ArchivePrefix '_bootstrap'", source, StringComparison.Ordinal);
        Assert.Contains("The published release does not contain the exact expected 16 assets", source, StringComparison.Ordinal);
        Assert.Contains("[System.Security.Cryptography.SHA256]::Create()", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Get-FileHash", source, StringComparison.Ordinal);
        Assert.Contains("$rootActual -ne $freshActual", source, StringComparison.Ordinal);
        Assert.Contains("Invoke-CheckedNative -FilePath $PowerShellHost", source, StringComparison.Ordinal);
        Assert.Contains("Resolve-ReleaseCommit", source, StringComparison.Ordinal);
        Assert.Contains("repos/$Repo/commits/v$Version", source, StringComparison.Ordinal);
        Assert.Contains("status --porcelain --untracked-files=all", source, StringComparison.Ordinal);
        Assert.Contains("'-Commit', $releaseCommit", source, StringComparison.Ordinal);
        Assert.Contains("'-Repo', $Repo", source, StringComparison.Ordinal);
        Assert.Contains("'-IgnoreSkipped', 'characterization-windows'", source, StringComparison.Ordinal);
        Assert.Contains("'publish=1,verify=1,test-update=1,test=1,go-test-windows=1,characterization-windows=1'", source, StringComparison.Ordinal);
        Assert.Contains("'-RequiredWorkflows'", source, StringComparison.Ordinal);
        Assert.Contains("'Build macOS DMG,Build Android APK,Build Linux AppImage + .deb", source, StringComparison.Ordinal);
        Assert.Contains("'-Strict'", source, StringComparison.Ordinal);
        Assert.Contains("& $BratVerify -Action state", source, StringComparison.Ordinal);
        Assert.Contains("-RunSinceUtc $RemoteVerificationStartedUtc.ToString('o')", source, StringComparison.Ordinal);
        Assert.DoesNotContain("& $CiGate", source, StringComparison.Ordinal);
        Assert.DoesNotContain("testvm-control.ps1", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PveHost", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("-Action screenshot", source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("throw 'Post-ship verification failed", source, StringComparison.Ordinal);
        Assert.Contains("/artifacts/brat-stability/", gitignore, StringComparison.Ordinal);
        Assert.Contains("/artifacts/post-ship/", gitignore, StringComparison.Ordinal);
        Assert.Contains("new(new InMemorySettingsStore())", pageScreenshots, StringComparison.Ordinal);
        Assert.Contains("using var vm = GetVm()", pageScreenshots, StringComparison.Ordinal);
        Assert.DoesNotContain("new MainWindowViewModel()", pageScreenshots, StringComparison.Ordinal);
        Assert.DoesNotContain("new VPNRouter.App.ViewModels.MainWindowViewModel()", pageScreenshots, StringComparison.Ordinal);
        Assert.Contains("new(new InMemorySettingsStore())", visualDiff, StringComparison.Ordinal);
        Assert.Contains("using var vm = GetVm()", visualDiff, StringComparison.Ordinal);
        Assert.Contains("IsolateTunCleanupFromHostDevices", testSafety, StringComparison.Ordinal);
        Assert.Contains("ResolveNativePnpDeviceIds", testSafety, StringComparison.Ordinal);
        Assert.Contains("new FakeProcessRunner()", testSafety, StringComparison.Ordinal);
        Assert.Contains("AppPaths.OverrideDataDir(testDataDir)", testSafety, StringComparison.Ordinal);
        Assert.Contains("VPNRouter.Tests.DisableBackgroundServices", testSafety, StringComparison.Ordinal);
        Assert.Contains("DeleteTestDataDirectory", testSafety, StringComparison.Ordinal);
        Assert.Contains("[switch]$Strict", ciGate, StringComparison.Ordinal);
        Assert.Contains("$TolerateFailure = $null", ciGate, StringComparison.Ordinal);
        Assert.Contains("$requiredGreen", ciGate, StringComparison.Ordinal);
        Assert.Contains("actions/runs\" -f \"head_sha=$head\"", ciGate, StringComparison.Ordinal);
        Assert.Contains("workflow '$requiredWorkflow'", ciGate, StringComparison.Ordinal);
        Assert.Contains("if ($Strict)", ciGate, StringComparison.Ordinal);
        Assert.Contains("$name [skipped, unexpected]", ciGate, StringComparison.Ordinal);
        Assert.Contains("$name [cancelled]", ciGate, StringComparison.Ordinal);

        foreach (var token in new[] { "New-PSSession", "Invoke-Command", "UIAutomation", "CopyFromScreen" })
            Assert.DoesNotContain(token, source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BratStability_EmergencyRecoveryStillFailsTheGate()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var sourceRoot = FindRepoRoot();
        var temp = Directory.CreateTempSubdirectory("vpnrouter-emergency-cleanup-");
        try
        {
            var root = temp.FullName;
            var tools = Directory.CreateDirectory(Path.Combine(root, "tools")).FullName;
            var stability = Path.Combine(tools, "brat-stability.ps1");
            File.Copy(Path.Combine(sourceRoot, "tools", "brat-stability.ps1"), stability);
            File.WriteAllText(Path.Combine(tools, "brat-verify.ps1"), """
                param([string]$Action)
                $marker = Join-Path $PSScriptRoot 'emergency.done'
                if ($Action -eq 'state') {
                    if (Test-Path $marker) {
                        '{"GuiCount":0,"CoreCount":0,"TunState":"Absent","RouteScope":"Direct"}'
                    } else {
                        '{"GuiCount":0,"CoreCount":1,"TunState":"Up","RouteScope":"Tunnel"}'
                    }
                    exit 0
                }
                if ($Action -eq 'emergencycleanup') {
                    Set-Content -LiteralPath $marker -Value 'done'
                    '{"StoppedOwnedProcessCount":1,"CoreCount":0,"TunAbsent":true}'
                    exit 0
                }
                throw "Unexpected action $Action"
                """);

            var result = RunPowerShell(
                root,
                stability,
                tools,
                new[] { "-Mode", "Cleanup" });

            Assert.NotEqual(0, result.ExitCode);
            var evidence = Directory.EnumerateFiles(
                Path.Combine(root, "artifacts", "brat-stability"),
                "*.jsonl").Single();
            Assert.Contains("EmergencyCleanState", File.ReadAllText(evidence), StringComparison.Ordinal);
        }
        finally
        {
            DeleteBestEffort(temp);
        }
    }

    [Fact]
    public void NativeDshPostShipSkill_StaysHeadlessAndWindowsContractsStayInCi()
    {
        var root = FindRepoRoot();
        var skillRoot = Path.Combine(root, ".dsh", "skills", "post-ship-mcp-verify");
        var forbidden = new[] { "-Action screenshot", "CopyFromScreen", "rdp-shots" };

        Assert.True(Directory.Exists(skillRoot), "Native DSH post-ship skill is missing.");
        foreach (var skillFile in Directory.EnumerateFiles(skillRoot, "*", SearchOption.AllDirectories))
        {
            var source = File.ReadAllText(skillFile);
            foreach (var token in forbidden)
                Assert.DoesNotContain(token, source, StringComparison.OrdinalIgnoreCase);
        }

        var workflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "test.yml"));
        Assert.Contains("characterization-windows:", workflow, StringComparison.Ordinal);
        Assert.Contains("FullyQualifiedName~PostShipVerifierContractTests", workflow, StringComparison.Ordinal);
        Assert.Contains("FullyQualifiedName~BratVerifierContractTests", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("if: startsWith(github.ref, 'refs/tags/')", workflow, StringComparison.Ordinal);
    }

    private static string ReadRepoFile(params string[] relativeParts)
    {
        var path = Path.Combine(new[] { FindRepoRoot() }.Concat(relativeParts).ToArray());
        Assert.True(File.Exists(path), $"Repository file not found: {path}");
        return File.ReadAllText(path);
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "VPNRouter.sln")))
            directory = directory.Parent;

        Assert.NotNull(directory);
        return directory!.FullName;
    }

    private static void AssertOrdered(string[] calls, params string[] prefixes)
    {
        var cursor = -1;
        foreach (var prefix in prefixes)
        {
            cursor = Array.FindIndex(calls, cursor + 1, call => call.StartsWith(prefix, StringComparison.Ordinal));
            Assert.True(cursor >= 0, $"Call '{prefix}' was not observed in order. Trace: {string.Join(" | ", calls)}");
        }
    }

    private static void CreateTrueSplitZip(string path, string prefix)
    {
        using var archive = System.IO.Compression.ZipFile.Open(
            path,
            System.IO.Compression.ZipArchiveMode.Create);
        foreach (var entryName in new[]
                 {
                     $"{prefix}/driver/mullvad-split-tunnel.sys",
                     $"{prefix}/driver/mullvad-split-tunnel.cat",
                     $"{prefix}/driver/mullvad-split-tunnel.inf",
                     $"{prefix}/driver/checksums.sha256",
                     $"{prefix}/LICENSE.split-tunnel",
                 })
        {
            var entry = archive.CreateEntry(entryName);
            using var writer = new StreamWriter(entry.Open());
            writer.Write("fixture");
        }
    }

    private static (int ExitCode, string Stdout, string Stderr) RunPowerShell(
        string workingDirectory,
        string script,
        string fakePath,
        IEnumerable<string> arguments,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        var shell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        var startInfo = new System.Diagnostics.ProcessStartInfo(shell)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(script);
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        startInfo.Environment["PATH"] = fakePath + Path.PathSeparator + startInfo.Environment["PATH"];
        if (environment is not null)
        {
            foreach (var pair in environment)
                startInfo.Environment[pair.Key] = pair.Value;
        }

        using var process = System.Diagnostics.Process.Start(startInfo);
        Assert.NotNull(process);
        Assert.True(process!.WaitForExit(30_000), "PowerShell contract test timed out.");
        return (process.ExitCode, process.StandardOutput.ReadToEnd(), process.StandardError.ReadToEnd());
    }

    private static string Slice(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        var end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Start marker not found: {startMarker}");
        Assert.True(end > start, $"End marker not found after {startMarker}: {endMarker}");
        return source[start..end];
    }

    private static void DeleteBestEffort(DirectoryInfo? temp)
    {
        if (temp == null) return;
        try
        {
            if (temp.Exists)
                temp.Delete(recursive: true);
        }
        catch
        {
            // best-effort cleanup on Windows where background process/antivirus can briefly lock files
        }
    }
}
