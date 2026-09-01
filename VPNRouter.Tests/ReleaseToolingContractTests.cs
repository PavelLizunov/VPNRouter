namespace VPNRouter.Tests;

public sealed class ReleaseToolingContractTests
{
    [Fact]
    public void Repair_VerifiesReleaseSidecarBeforeExtracting()
    {
        var cmd = Read("packaging", "windows", "repair.cmd");

        Assert.Contains("$asset.name + '.sha256'", cmd);
        Assert.Contains("^[0-9a-f]{64}$", cmd);
        Assert.Contains("fail closed", cmd, StringComparison.OrdinalIgnoreCase);
        Assert.True(cmd.IndexOf("Get-FileHash", StringComparison.Ordinal) <
                    cmd.IndexOf("Expand-Archive", StringComparison.Ordinal));
    }

    [Fact]
    public void StableCutGate_FiltersUncheckedEntriesToP0AndP1()
    {
        var script = Read("tools", "check-open-p0.ps1");

        Assert.Contains(@"\*\*P[01]\*\*", script);
        Assert.Contains(@"(?i)^##\s+Open\b", script);
        Assert.Contains("$Waive", script);
    }

    [Fact]
    public void ReleaseIntegrity_ExpectsBothMacSidecars()
    {
        var workflow = Read(".github", "workflows", "verify-release-integrity.yml");

        Assert.Contains("VPNRouter-v${EXPECTED_VERSION}-mac.dmg.sha256", workflow);
        Assert.Contains("VPNRouter-v${EXPECTED_VERSION}-mac.zip.sha256", workflow);
        Assert.Contains("All 16 expected assets present", workflow);
        Assert.DoesNotContain("All 14 expected assets present", workflow);
    }

    [Fact]
    public void WindowsUpdateCache_TracksBuildSingBoxVersion()
    {
        var build = Read("build.ps1");
        var workflow = Read(".github", "workflows", "test-windows-update.yml");
        var version = System.Text.RegularExpressions.Regex.Match(
            build, @"\$SingBoxVersion\s*=\s*""([^""]+)""").Groups[1].Value;

        Assert.False(string.IsNullOrEmpty(version), "Could not read SingBoxVersion from build.ps1.");
        Assert.Contains($"key: singbox-{version}-${{{{ runner.os }}}}", workflow);
    }

    [Fact]
    public void ReleaseUpload_AlwaysBundlesTrueSplitDriver()
    {
        var build = Read("build.ps1");

        Assert.Contains("$bundleSplitDriver = $BundleSplitDriver -or $Upload", build);
        Assert.Contains("if ($bundleSplitDriver)", build);
    }

    [Fact]
    public void ReleaseUpload_FailsClosedWhenGitHubReleaseCreationFails()
    {
        var build = Read("build.ps1");

        Assert.Contains("throw \"gh CLI not found.", build);
        Assert.Contains("$releaseCreateExitCode = $LASTEXITCODE", build);
        Assert.Contains("if ($releaseCreateExitCode -eq 0)", build);
        Assert.Contains("throw \"GitHub release creation failed", build);
    }

    [Fact]
    public void LocalLinuxBuild_PublishesAppOnly()
    {
        var script = Read("build-linux.ps1");

        Assert.Contains("VPNRouter.App\\VPNRouter.App.csproj", script);
        Assert.DoesNotContain("VPNRouter.CLI\\VPNRouter.CLI.csproj", script);
        Assert.DoesNotContain("VPNRouter.Service\\VPNRouter.Service.csproj", script);
    }

    [Fact]
    public void AndroidReleaseBuild_SelectsAndroidArm64Rid()
    {
        var workflow = Read(".github", "workflows", "build-android.yml");

        Assert.Contains("-p:RuntimeIdentifier=android-arm64", workflow);
        Assert.DoesNotContain("-p:RuntimeIdentifiers=android-arm64", workflow);
        Assert.DoesNotMatch(
            @"(?m)^    if: github\.event_name == 'workflow_dispatch'\s*$",
            workflow);
        Assert.Contains("android-arm64.apk", workflow);
    }

    [Fact]
    public void AndroidDownloadPageAndReadmes_SelectCanonicalArm64Asset()
    {
        var page = Read("packaging", "android-page", "index.html");
        var readme = Read("README.md");
        var readmeRu = Read("README.ru.md");

        Assert.Contains("VPNRouter-v.*-android-arm64\\.apk$", page);
        Assert.Contains("VPNRouter-v{version}-android-arm64.apk", readme);
        Assert.Contains("VPNRouter-v{version}-android-arm64.apk", readmeRu);
        Assert.DoesNotContain("arm64/arm/x64/x86 universal", readme);
        Assert.DoesNotContain("arm64/arm/x64/x86 универсальный", readmeRu);
    }

    [Fact]
    public void WindowsSigner_FailsFastUnlessAllSignPathSecretsExist()
    {
        var workflow = Read(".github", "workflows", "sign-windows.yml");

        Assert.Contains("[ -z \"$TOKEN\" ]", workflow);
        Assert.Contains("[ -z \"$ORGANIZATION_ID\" ]", workflow);
        Assert.Contains("[ -z \"$PROJECT_SLUG\" ]", workflow);
        Assert.Contains("[ -z \"$POLICY_SLUG\" ]", workflow);
        Assert.Contains("[ -z \"$ARTIFACT_CONFIG_SLUG\" ]", workflow);
        Assert.Contains("All five SignPath secrets are required", workflow);
        Assert.Contains("SIGNPATH_EXPECTED_SUBJECT", workflow);
        Assert.Contains("refs/tags/v${{ inputs.version }}", workflow);
        Assert.Contains("AppVersion/tag mismatch", workflow);
        Assert.Contains("steps.upload-unsigned.outputs.artifact-id", workflow);
        Assert.DoesNotContain("gh release download", workflow);
        Assert.Contains("actions: read", workflow);
        Assert.Contains("contents: read", workflow);
        Assert.Contains("contents: write", workflow);
        Assert.Contains("persist-credentials: false", workflow);
        Assert.Contains("stage-draft-assets:", workflow);
        Assert.Contains("needs: build-sign-and-verify", workflow);
        Assert.Contains("vpnrouter-windows-verified-signed", workflow);
        Assert.DoesNotContain("needs.build.outputs.artifact-id", workflow);
        Assert.Contains("Get-AuthenticodeSignature", workflow);
        Assert.Contains("SignatureStatus]::Valid", workflow);
        Assert.Contains("Unexpected signer", workflow);
        Assert.Contains("is no longer a draft; refusing any mutation", workflow);
        foreach (var assembly in new[]
        {
            "VPNRouter.App.dll",
            "VPNRouter.CLI.dll",
            "VPNRouter.Service.dll"
        })
        {
            Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(workflow, assembly).Count);
        }
        Assert.True(
            workflow.IndexOf("Get-AuthenticodeSignature", StringComparison.Ordinal) <
            workflow.IndexOf("stage-draft-assets:", StringComparison.Ordinal) &&
            workflow.IndexOf("stage-draft-assets:", StringComparison.Ordinal) <
            workflow.IndexOf("gh release upload", StringComparison.Ordinal),
            "Authenticode verification must complete before the draft release is mutated.");
    }

    [Fact]
    public void WindowsSigner_IsValidYaml()
    {
        var yaml = new YamlDotNet.RepresentationModel.YamlStream();
        using var reader = new StringReader(Read(".github", "workflows", "sign-windows.yml"));

        yaml.Load(reader);

        Assert.Single(yaml.Documents);
    }

    [Fact]
    public void PrePushHook_AllowsTaskBranchesButStillGatesMain()
    {
        var hook = Read(".githooks", "pre-push");

        Assert.Contains("refs/heads/main", hook);
        Assert.Contains("Non-main branch push detected", hook);
        Assert.Contains("verify-last-commit-ci.ps1", hook);
    }

    [Fact]
    public void PreCommitHook_EnablesPipefailBeforeGate1Build()
    {
        var hook = Read(".githooks", "pre-commit");

        const string pipefail = "set -o pipefail";
        const string gate1Build = "if ! dotnet build";

        Assert.Contains(pipefail, hook);
        Assert.Contains(gate1Build, hook);

        var pipefailIndex = hook.IndexOf(pipefail, StringComparison.Ordinal);
        var gate1BuildIndex = hook.IndexOf(gate1Build, StringComparison.Ordinal);
        Assert.True(
            pipefailIndex >= 0 && gate1BuildIndex >= 0 && pipefailIndex < gate1BuildIndex,
            "set -o pipefail must appear before Gate 1's actual 'if ! dotnet build' command.");
    }

    [Fact]
    public void WindowsInstaller_IsValidWindowsPowerShell()
    {
        if (!OperatingSystem.IsWindows()) return;

        var shell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        var startInfo = new System.Diagnostics.ProcessStartInfo(shell)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add("$t=$null;$e=$null;[Management.Automation.Language.Parser]::ParseFile($env:VPNROUTER_INSTALLER_PARSE_PATH,[ref]$t,[ref]$e)|Out-Null;if($e.Count){$e|ForEach-Object{$_.Message}|Write-Error;exit 1}");
        startInfo.Environment["VPNROUTER_INSTALLER_PARSE_PATH"] =
            Path.Combine(FindRoot(), "packaging", "windows", "install.ps1");

        using var process = System.Diagnostics.Process.Start(startInfo);
        Assert.NotNull(process);
        Assert.True(process!.WaitForExit(10_000), "PowerShell parser timed out.");
        var stderr = process.StandardError.ReadToEnd();
        Assert.True(process.ExitCode == 0, stderr);
    }

    [Fact]
    public void WindowsInstaller_ValidatesVersionBeforeElevation()
    {
        var script = Read("packaging", "windows", "install.ps1");
        var patternMatch = System.Text.RegularExpressions.Regex.Match(
            script,
            "(?s)\\[ValidatePattern\\('([^']+)'\\)\\]\\s*\\[string\\]\\$Version\\s*=\\s*\"\"");

        Assert.True(patternMatch.Success, "The Version parameter must own the validation grammar.");
        var pattern = patternMatch.Groups[1].Value;
        foreach (var accepted in new[] { "", "2.49.0", "2.49.0-r1", "10.20.300-r42" })
            Assert.Matches(pattern, accepted);
        foreach (var rejected in new[] { "v2.49.0", "2.49", "2.49.0-r0", "2.49.0; Start-Process calc", "2.49.0 r1" })
            Assert.DoesNotMatch(pattern, rejected);
    }

    [Fact]
    public void WindowsInstaller_KeepsCallerValuesOutOfElevatedSource()
    {
        var script = Read("packaging", "windows", "install.ps1");
        var elevation = script[..script.IndexOf("# From here on: admin rights confirmed.", StringComparison.Ordinal)];
        const string launch = "Start-Process -FilePath $WindowsPowerShell -Verb RunAs -ArgumentList \"-NoProfile -ExecutionPolicy Bypass -EncodedCommand $encodedBootstrap\"";

        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(elevation, System.Text.RegularExpressions.Regex.Escape(launch)).Count);
        Assert.Contains("$WindowsPowerShell = Join-Path $SystemDirectory", elevation);
        Assert.DoesNotContain("Start-Process powershell.exe", elevation);
        AssertFailClosedBranch(elevation, "-not [IO.File]::Exists($WindowsPowerShell)");
        Assert.Contains("$encodedVersion = [Convert]::ToBase64String", elevation);
        Assert.Contains("$forwardFlags = $forwardFlags -bor 1", elevation);
        Assert.Contains("$forwardFlags = $forwardFlags -bor 2", elevation);
        Assert.Contains("$forwardFlags = $forwardFlags -bor 4", elevation);
        Assert.Contains("$bootstrapTemplate = @'", elevation);
        Assert.Contains("FromBase64String('__FORWARDED_VERSION__')", elevation);
        Assert.Contains("$installParams = @{ Elevated = $true }", elevation);
        Assert.Contains("$installParams.Version = $version", elevation);
        Assert.Contains("@installParams", elevation);
        Assert.Contains("Replace('__FORWARDED_FLAGS__', [string]$forwardFlags)", elevation);
        Assert.Contains("$response = Invoke-WebRequest", elevation);
        Assert.Contains("if ($content -is [byte[]])", elevation);
        Assert.Contains("[ScriptBlock]::Create([string]$content)", elevation);
        Assert.DoesNotContain("$flagsString", elevation);
        Assert.DoesNotContain("$env:VPNROUTER_INSTALL_", elevation);
    }

    [Fact]
    public void WindowsInstaller_BootstrapBindsNamedParameters()
    {
        if (!OperatingSystem.IsWindows()) return;

        var installer = Read("packaging", "windows", "install.ps1");
        var templateMatch = System.Text.RegularExpressions.Regex.Match(
            installer,
            @"(?ms)^\s*\$bootstrapTemplate = @'\r?\n(?<body>.*?)\r?\n'@");
        Assert.True(templateMatch.Success, "Could not extract the elevation bootstrap template.");
        var template = System.Text.RegularExpressions.Regex.Replace(
            templateMatch.Groups["body"].Value,
            @"(?m)^pause\r?$",
            string.Empty);
        const string fakeDownload =
            "function Invoke-WebRequest {\n" +
            "param($Uri, [switch]$UseBasicParsing, $ErrorAction)\n" +
            "[pscustomobject]@{ Content = @'\n" +
            "[CmdletBinding()]\n" +
            "param([string]$Version = \"\", [switch]$Prerelease, [switch]$Service, [switch]$NoLaunch, [switch]$Elevated)\n" +
            "[ordered]@{ Version=$Version; Prerelease=[bool]$Prerelease; Service=[bool]$Service; NoLaunch=[bool]$NoLaunch; Elevated=[bool]$Elevated } | ConvertTo-Json -Compress\n" +
            "'@ }\n" +
            "}\n";

        foreach (var testCase in new[]
        {
            (Version: "", Flags: 0),
            (Version: "2.49.0", Flags: 0),
            (Version: "", Flags: 1),
            (Version: "", Flags: 2),
            (Version: "", Flags: 4),
            (Version: "2.49.0-r7", Flags: 7),
        })
        {
            var encodedVersion = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(testCase.Version));
            var bootstrap = template
                .Replace("__FORWARDED_VERSION__", encodedVersion, StringComparison.Ordinal)
                .Replace("__FORWARDED_FLAGS__", testCase.Flags.ToString(), StringComparison.Ordinal);
            var encodedCommand = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(fakeDownload + bootstrap));
            var shell = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe");
            var startInfo = new System.Diagnostics.ProcessStartInfo(shell)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-EncodedCommand");
            startInfo.ArgumentList.Add(encodedCommand);

            using var process = System.Diagnostics.Process.Start(startInfo);
            Assert.NotNull(process);
            Assert.True(process!.WaitForExit(10_000), "Bootstrap binding probe timed out.");
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            Assert.True(process.ExitCode == 0, stderr);
            var json = stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Last();
            using var document = System.Text.Json.JsonDocument.Parse(json);
            var result = document.RootElement;
            Assert.Equal(testCase.Version, result.GetProperty("Version").GetString());
            Assert.Equal((testCase.Flags & 1) != 0, result.GetProperty("Prerelease").GetBoolean());
            Assert.Equal((testCase.Flags & 2) != 0, result.GetProperty("Service").GetBoolean());
            Assert.Equal((testCase.Flags & 4) != 0, result.GetProperty("NoLaunch").GetBoolean());
            Assert.True(result.GetProperty("Elevated").GetBoolean());
        }
    }

    [Fact]
    public void WindowsInstaller_BindsAndVerifiesSecuredReleaseAssetBeforeExtracting()
    {
        var script = Read("packaging", "windows", "install.ps1");
        var assetStart = script.IndexOf("$expectedZipName =", StringComparison.Ordinal);
        var extractEnd = script.IndexOf("Expand-Archive", StringComparison.Ordinal);

        Assert.True(assetStart >= 0 && extractEnd > assetStart);
        var verification = script[assetStart..extractEnd];
        AssertOrder(
            verification,
            "$expectedZipName = \"VPNRouter-v$resolvedVersion-win.zip\"",
            "$zipAssets.Count -ne 1",
            "$shaAssets.Count -ne 1",
            "$stagingDir = Join-Path $ProgramFilesRoot",
            "/inheritance:r /grant:r",
            "Invoke-WebRequest -Uri $zipAsset.browser_download_url",
            "Invoke-WebRequest -Uri $shaAsset.browser_download_url",
            "^[0-9a-f]{64}$",
            "Get-FileHash -Algorithm SHA256 $zipPath",
            "$actualSha -ne $expectedSha");
        Assert.Contains("$ProgramFilesRoot = [Environment]::GetFolderPath", script);
        Assert.Contains("$ProgramDataRoot  = [Environment]::GetFolderPath", script);
        Assert.Contains("$SystemDirectory  = [Environment]::SystemDirectory", script);
        Assert.Contains("*S-1-5-32-544:(OI)(CI)F", verification);
        Assert.Contains("*S-1-5-18:(OI)(CI)F", verification);
        Assert.Contains("} finally {", script[assetStart..]);
        Assert.Contains("Remove-Item $stagingDir -Recurse -Force", script[assetStart..]);
        Assert.DoesNotContain("$env:TEMP", verification);
        Assert.DoesNotContain("$env:ProgramFiles", script);
        Assert.DoesNotContain("$env:ProgramData", script);
        Assert.DoesNotContain("$env:SystemRoot", script);
        Assert.DoesNotContain("skipping hash verification", script, StringComparison.OrdinalIgnoreCase);
        AssertFailClosedBranch(script, "$zipAssets.Count -ne 1");
        AssertFailClosedBranch(script, "$shaAssets.Count -ne 1");
        AssertFailClosedBranch(script, "$LASTEXITCODE -ne 0");
        AssertFailClosedBranch(script, "$expectedSha -notmatch '^[0-9a-f]{64}$'");
        AssertFailClosedBranch(script, "$actualSha -ne $expectedSha");
    }

    private static void AssertFailClosedBranch(string text, string condition)
    {
        var start = text.IndexOf($"if ({condition})", StringComparison.Ordinal);
        var end = start < 0 ? -1 : text.IndexOf("\n}", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, $"Could not locate guarded branch: {condition}");
        Assert.Contains("exit 1", text[start..end]);
    }

    private static void AssertOrder(string text, params string[] tokens)
    {
        var previous = -1;
        foreach (var token in tokens)
        {
            var current = text.IndexOf(token, previous + 1, StringComparison.Ordinal);
            Assert.True(current > previous, $"Expected '{token}' after the previous trust-boundary operation.");
            previous = current;
        }
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
