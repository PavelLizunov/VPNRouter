using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace VPNRouter.Tests;

/// <summary>Isolated Windows PowerShell behavior; live-update and CI assertions are source-only.</summary>
public sealed class ReleaseSafetyBehaviorTests
{
    [Theory]
    [InlineData("bare", "")]
    [InlineData("named", "")]
    [InlineData("binary", "")]
    [InlineData("uppercase", "")]
    [InlineData("wrong-name", "names a different artifact")]
    [InlineData("wrong-case", "names a different artifact")]
    [InlineData("wrong-hash", "does not match its SHA256 sidecar")]
    [InlineData("multiline", "sidecar is malformed")]
    [InlineData("short", "sidecar is malformed")]
    [InlineData("missing", "were not both downloaded")]
    public async Task ArtifactHash_ValidatesActualBytesAndSidecarIdentity(string scenario, string expectedError)
    {
        if (!OperatingSystem.IsWindows()) return;
        await RunFixtureAsync("""
            . (Read-Definition 'tools/post-ship-verify.ps1' 'Get-Sha256Hex')
            . (Read-Definition 'tools/post-ship-verify.ps1' 'Get-VerifiedArtifactHash')
            $artifact = Join-Path $PSScriptRoot 'artifact.zip'
            $sidecar = "$artifact.sha256"
            [IO.File]::WriteAllBytes($artifact, [byte[]]@(0, 1, 2, 3, 255))
            $sha = [Security.Cryptography.SHA256]::Create()
            try { $hash = ([BitConverter]::ToString($sha.ComputeHash([IO.File]::ReadAllBytes($artifact)))).Replace('-', '').ToLowerInvariant() }
            finally { $sha.Dispose() }
            $text = switch ($Scenario) {
                'bare' { $hash }
                'named' { "$hash  artifact.zip" }
                'binary' { "$hash *artifact.zip" }
                'uppercase' { $hash.ToUpperInvariant() + "`tartifact.zip`r`n" }
                'wrong-name' { "$hash  other.zip" }
                'wrong-case' { "$hash  Artifact.zip" }
                'wrong-hash' { ('0' * 64) + '  artifact.zip' }
                'multiline' { "$hash  artifact.zip`n$hash  artifact.zip" }
                'short' { '1234' }
                'missing' { $hash }
                default { throw 'Unknown fixture case' }
            }
            [IO.File]::WriteAllText($sidecar, $text)
            if ($Scenario -eq 'missing') { Remove-Item -LiteralPath $artifact }
            $caught = $null
            try { $result = Get-VerifiedArtifactHash -ArtifactPath $artifact -SidecarPath $sidecar }
            catch { $caught = $_.Exception.Message }
            if ($ExpectedError) {
                if (-not $caught -or -not $caught.Contains($ExpectedError)) { throw "Expected '$ExpectedError', got '$caught'" }
            } else {
                if ($caught) { throw $caught }
                if ($result.Expected -cne $hash -or $result.Actual -cne $hash) { throw 'Wrong verified digest' }
            }
            """, scenario, expectedError);
    }

    [Theory]
    [InlineData("candidate", "")]
    [InlineData("stable", "")]
    [InlineData("draft", "published release with the exact tag and channel")]
    [InlineData("candidate-wrong-channel", "published release with the exact tag and channel")]
    [InlineData("stable-wrong-channel", "published release with the exact tag and channel")]
    [InlineData("wrong-tag", "published release with the exact tag and channel")]
    [InlineData("missing", "exact expected 16 assets")]
    [InlineData("extra", "exact expected 16 assets")]
    [InlineData("duplicate", "exact expected 16 assets")]
    [InlineData("missing-id", "Incomplete release asset identity")]
    [InlineData("missing-timestamp", "Incomplete release asset identity")]
    [InlineData("zero-size", "Incomplete release asset identity")]
    [InlineData("gh-failure", "inventory could not be read")]
    public async Task ReleaseInventory_RequiresPublishedChannelAndExactIdentifiedAssets(string scenario, string expectedError)
    {
        if (!OperatingSystem.IsWindows()) return;
        await RunFixtureAsync("""
            . (Read-Definition 'tools/post-ship-verify.ps1' 'Assert-ExactReleaseAssets')
            $Version = if ($Scenario.StartsWith('stable')) { '9.9.9' } else { '9.9.9-r1' }
            $Repo = 'fixture/no-network'
            $names = @("VPNRouter-v$Version-win.zip", "VPNRouter-update-v$Version-win.zip",
                "VPNRouter-v$Version-mac.dmg", "VPNRouter-v$Version-mac.zip",
                "VPNRouter-v$Version-linux.tar.gz", "VPNRouter-v$Version-linux-amd64.deb",
                "VPNRouter-v$Version-linux-x86_64.AppImage", "VPNRouter-v$Version-android-arm64.apk")
            $id = 0
            $assets = @($names | ForEach-Object {
                foreach ($name in @($_, "$_.sha256")) {
                    $id++
                    [pscustomobject]@{ name = $name; id = $id; size = 100; updatedAt = '2026-09-01T00:00:00Z' }
                }
            })
            $release = [pscustomobject]@{ assets = $assets; isDraft = $false; isPrerelease = ($Version -like '*-r*'); tagName = "v$Version" }
            switch ($Scenario) {
                'draft' { $release.isDraft = $true }
                'candidate-wrong-channel' { $release.isPrerelease = $false }
                'stable-wrong-channel' { $release.isPrerelease = $true }
                'wrong-tag' { $release.tagName = 'v9.9.8' }
                'missing' { $release.assets = @($assets | Select-Object -Skip 1) }
                'extra' { $release.assets += [pscustomobject]@{ name = 'foreign.zip'; id = 99; size = 1; updatedAt = '2026-09-01T00:00:00Z' } }
                'duplicate' { $release.assets[1] = $release.assets[0] }
                'missing-id' { $release.assets[0].id = $null }
                'missing-timestamp' { $release.assets[0].updatedAt = $null }
                'zero-size' { $release.assets[0].size = 0 }
            }
            $script:ghCalls = 0
            function gh {
                $script:ghCalls++
                if (($args -join ' ') -cne "release view v$Version --repo $Repo --json assets,isDraft,isPrerelease,tagName") { throw 'Unexpected gh invocation' }
                $global:LASTEXITCODE = if ($Scenario -eq 'gh-failure') { 1 } else { 0 }
                $release | ConvertTo-Json -Depth 5 -Compress
            }
            $caught = $null
            try { $inventory = Assert-ExactReleaseAssets }
            catch { $caught = $_.Exception.Message }
            if ($script:ghCalls -ne 1) { throw 'Expected exactly one mocked gh call' }
            if ($ExpectedError) {
                if (-not $caught -or -not $caught.Contains($ExpectedError)) { throw "Expected '$ExpectedError', got '$caught'" }
            } else {
                if ($caught) { throw $caught }
                $expected = (($assets | Sort-Object name | ForEach-Object { '{0}|{1}|{2}|{3}' -f $_.name, $_.id, $_.size, $_.updatedAt }) -join "`n")
                if ($inventory -cne $expected) { throw 'Inventory omitted asset identity' }
                [array]::Reverse($release.assets)
                if ((Assert-ExactReleaseAssets) -cne $inventory) { throw 'Inventory depends on API ordering' }
                $release.assets[0].id = 1000
                if ((Assert-ExactReleaseAssets) -ceq $inventory) { throw 'Replacement asset ID was not bound' }
            }
            """, scenario, expectedError);
    }

    [Theory]
    [InlineData("http")]
    [InlineData("udp")]
    [InlineData("aggregate")]
    public async Task Soak_ThrowsOnSecondControlFailureWithoutWaitingOrRemoteExecution(string failure)
    {
        if (!OperatingSystem.IsWindows()) return;
        await RunFixtureAsync("""
            . (Read-Definition 'tools/brat-stability.ps1' 'Invoke-Soak')
            $DurationMinutes = 1
            $SampleSeconds = 1
            $script:controls = 0
            $script:boundaries = 0
            $script:sleeps = 0
            $script:samples = @()
            function Ensure-Disconnected { }
            function Connect-And-Wait { }
            function Get-BratState { @{ Connected = $true } }
            function Test-ConnectedState { param($State) $true }
            function Write-Evidence { param($Kind, $Data) if ($Kind -eq 'SoakSample') { $script:samples += $Data.Sample } }
            function Start-Sleep { param($Seconds) $script:sleeps++; if ($script:sleeps -gt 1) { throw 'Unexpected extra sample/wait' } }
            function Get-Lifecycle { throw 'Must not reach final lifecycle after failed sample' }
            function Assert-CleanLifecycle { throw 'Must not accept failed soak' }
            function Invoke-Probe {
                param($Profile)
                if ($Profile -eq 'Boundary') { $script:boundaries++; return @{ Success = $true } }
                if ($Profile -ne 'Control') { throw 'Unexpected probe profile' }
                $script:controls++
                if ($script:controls -gt 2) { throw 'Failed control was ignored' }
                $failed = $script:controls -eq 2
                return @{ Success = -not ($failed -and $Scenario -eq 'aggregate');
                    Http = @{ Success = -not ($failed -and $Scenario -eq 'http') };
                    Udp = @(@{ Success = -not ($failed -and $Scenario -eq 'udp') }) }
            }
            $caught = $null
            try { Invoke-Soak } catch { $caught = $_.Exception.Message }
            if ($caught -cne 'Soak dataplane probe failed at sample 2.') { throw "Unexpected soak result: $caught" }
            if ($script:controls -ne 2 -or $script:boundaries -ne 1 -or $script:sleeps -ne 1 -or ($script:samples -join ',') -ne '1,2') {
                throw 'Soak did not stop immediately after recording the failed middle control'
            }
            """, failure);
    }

    [Theory]
    [InlineData("build.ps1")]
    [InlineData("tools/post-ship-verify.ps1")]
    [InlineData("tools/brat-stability.ps1")]
    [InlineData("tools/brat-verify.ps1")]
    [InlineData("tools/check-open-p0.ps1")]
    [InlineData("tools/verify-last-commit-ci.ps1")]
    public async Task ChangedScripts_ParseInWindowsPowerShell51(string relativePath)
    {
        if (!OperatingSystem.IsWindows()) return;
        await RunFixtureAsync("""
            $tokens = $null; $errors = $null
            $null = [Management.Automation.Language.Parser]::ParseFile((Join-Path $RepoRoot $Scenario), [ref]$tokens, [ref]$errors)
            if ($errors.Count) { throw ($errors | Out-String) }
            """, relativePath);
    }

    [Fact]
    public async Task UpdaterWorkflow_PowerShellBlocksParseInWindowsPowerShell51()
    {
        if (!OperatingSystem.IsWindows()) return;
        // This fixed-layout extraction is not a YAML validator; expressions are inert parse placeholders.
        var blocks = Regex.Matches(ReadSource(".github/workflows/test-windows-update.yml"),
            @"(?m)^        run: \|\r?\n((?:(?:          [^\r\n]*|)[\r]?\n)+)");
        Assert.Equal(9, blocks.Count);
        var scripts = blocks.Select(block => Regex.Replace(block.Groups[1].Value, @"\$\{\{.*?\}\}", "fixture")).ToArray();
        await RunFixtureAsync("""
            $blocks = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'blocks.json') -Raw | ConvertFrom-Json
            foreach ($block in $blocks) {
                $tokens = $null; $errors = $null
                $null = [Management.Automation.Language.Parser]::ParseInput($block, [ref]$tokens, [ref]$errors)
                if ($errors.Count) { throw ($errors | Out-String) }
            }
            """, extraFile: System.Text.Json.JsonSerializer.Serialize(scripts));
    }

    [Fact]
    public void LiveUpdate_SourceOnlyPinsOwnedPathsHandlesInteractiveSidAndStrictCleanup()
    {
        var source = ReadSource("tools/brat-verify.ps1");
        var start = source.IndexOf("    'liveupdate' {", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var live = source[start..];
        foreach (var token in new[] { "$s = New-VerifiedBratSession",
                     "$ownedPaths -icontains $ownedProcess.Path", "$ownedProcess.WaitForExit(10000)",
                     "finally { $ownedProcess.Dispose() }", "if ($remaining.Count)",
                     "-LogonType Interactive", "[int]$candidate.SessionId -le 0", "[string]$candidate.ExecutablePath -ine",
                     "-MethodName GetOwnerSid -ErrorAction Stop", "$owner.Sid -eq $expectedSid",
                     "Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction Stop",
                     "throw 'Live-update launch task cleanup failed.'" })
            Assert.Contains(token, live, StringComparison.Ordinal);
        Assert.True(live.IndexOf("$null = $ownedProcess.Handle", StringComparison.Ordinal) <
                    live.IndexOf("$ownedProcess.Kill()", StringComparison.Ordinal));
        Assert.Contains("$null = $ownedProcess.Handle", live, StringComparison.Ordinal);
        Assert.True(live.IndexOf("$null = $ownedProcess.Handle", StringComparison.Ordinal) <
                    live.IndexOf("$ownedPaths -icontains $ownedProcess.Path", StringComparison.Ordinal));
        Assert.DoesNotContain("Stop-Process -Name", live, StringComparison.Ordinal);
        Assert.DoesNotContain("Stop-Process -Id", live, StringComparison.Ordinal);
    }

    [Fact]
    public void LegacySigningAndUpdaterCopySentinel_AreSourceOnlyFailClosedContracts()
    {
        var legacy = ReadSource(".github/workflows/sign-android.yml");
        Assert.Contains("permissions: {}", legacy, StringComparison.Ordinal);
        Assert.Contains("Legacy APK signing is disabled", legacy, StringComparison.Ordinal);
        Assert.Contains("exit 1", legacy, StringComparison.Ordinal);
        Assert.DoesNotContain("secrets.", legacy, StringComparison.Ordinal);
        Assert.DoesNotContain("gh release", legacy, StringComparison.Ordinal);
        var updater = ReadSource(".github/workflows/test-windows-update.yml");
        Assert.Contains("(Join-Path $appDir 'update-copy-probe.txt') -Value 'old'", updater, StringComparison.Ordinal);
        Assert.Contains("(Join-Path $stagedDir 'update-copy-probe.txt') -Value 'new'", updater, StringComparison.Ordinal);
        Assert.Contains("@('VPNRouter.Core.dll', 'VPNRouter.GUI.exe', 'update-copy-probe.txt')", updater, StringComparison.Ordinal);
        Assert.Contains("(Get-FileHash -Algorithm SHA256 -LiteralPath $target).Hash -ne $property.Value", updater, StringComparison.Ordinal);
        Assert.Contains("throw \"Updater did not install expected staged bytes:", updater, StringComparison.Ordinal);
        var build = ReadSource("build.ps1");
        Assert.Contains("Release is no longer the expected draft; refusing staging.", build, StringComparison.Ordinal);
        Assert.Contains("never unsigned fallback", build, StringComparison.Ordinal);
        Assert.DoesNotContain("gh release create", build, StringComparison.Ordinal);
        Assert.DoesNotContain("gh release edit", build, StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "VPNRouter.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return directory!.FullName;
    }

    private static string ReadSource(string path) => File.ReadAllText(Path.Combine(RepoRoot(), path));

    private static async Task RunFixtureAsync(string body, string scenario = "", string expectedError = "", string? extraFile = null)
    {
        var temp = Directory.CreateTempSubdirectory("vpnrouter-release-safety-");
        try
        {
            var script = Path.Combine(temp.FullName, "fixture.ps1");
            File.WriteAllText(script, """
                param([string]$RepoRoot, [string]$Scenario, [string]$ExpectedError)
                Set-StrictMode -Version Latest
                $ErrorActionPreference = 'Stop'
                if ($PSVersionTable.PSVersion.Major -ne 5 -or $PSVersionTable.PSVersion.Minor -ne 1) { throw 'Requires Windows PowerShell 5.1' }
                # Parse, then load only the named definition: never dot-source a remote-capable script.
                function Read-Definition {
                    param($Path, $Name)
                    $tokens = $null; $errors = $null
                    $ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $RepoRoot $Path), [ref]$tokens, [ref]$errors)
                    if ($errors.Count) { throw ($errors | Out-String) }
                    $definitions = @($ast.FindAll({ param($node)
                        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $Name
                    }, $true))
                    if ($definitions.Count -ne 1) { throw "Expected exactly one function $Name" }
                    [scriptblock]::Create($definitions[0].Extent.Text)
                }
                """ + Environment.NewLine + body + Environment.NewLine + "Write-Output 'FIXTURE-PASS'", new UTF8Encoding(true));
            if (extraFile is not null) File.WriteAllText(Path.Combine(temp.FullName, "blocks.json"), extraFile);
            var shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe");
            var info = new ProcessStartInfo(shell)
            {
                WorkingDirectory = temp.FullName, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
                         "-File", script, "-RepoRoot", RepoRoot(), "-Scenario", scenario, "-ExpectedError", expectedError })
                info.ArgumentList.Add(argument);
            using var process = Process.Start(info);
            Assert.NotNull(process);
            var stdout = process!.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                using var killTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await process.WaitForExitAsync(killTimeout.Token);
                Assert.Fail("Isolated release-safety fixture timed out.");
            }
            var output = await stdout.WaitAsync(TimeSpan.FromSeconds(5)) + await stderr.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(process.ExitCode == 0, $"Scenario={scenario}; exit={process.ExitCode}; {output}");
            Assert.Contains("FIXTURE-PASS", output, StringComparison.Ordinal);
        }
        finally { temp.Delete(recursive: true); }
    }
}
