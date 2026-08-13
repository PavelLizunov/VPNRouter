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
