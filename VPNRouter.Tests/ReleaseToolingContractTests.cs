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
