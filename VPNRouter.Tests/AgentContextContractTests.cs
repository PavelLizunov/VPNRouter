namespace VPNRouter.Tests;

public sealed class AgentContextContractTests
{
    [Theory]
    [InlineData("AGENTS.md", ".agents/skills/<name>/SKILL.md")]
    [InlineData("CLAUDE.md", ".claude/skills/<name>/SKILL.md")]
    public void RootBootstrap_LoadsCanonicalContractWithoutDuplicatingPolicy(
        string bootstrapName,
        string toolSpecificSkillPath)
    {
        var bootstrap = Read(bootstrapName);

        Assert.Contains("docs/agent-contract.md", bootstrap);
        Assert.Contains("Before taking any repository action", bootstrap);
        Assert.Contains(toolSpecificSkillPath, bootstrap);
        Assert.DoesNotContain("## Golden rules", bootstrap);
        Assert.InRange(bootstrap.Split('\n').Length, 10, 30);
    }

    [Fact]
    public void CanonicalContract_PinsSafetyReleaseAndTestOracles()
    {
        var contract = Read("docs", "agent-contract.md");
        var normalized = NormalizeWhitespace(contract);

        Assert.Contains("Tags, releases, deployments, merges and stable cuts require an explicit owner", normalized);
        Assert.Contains("AppVersion.Version", contract);
        Assert.Contains("tools/verify-last-commit-ci.ps1", contract);
        Assert.Contains("plans/OPEN-DEFECTS.md", contract);
        Assert.Contains("100.115.182.0", contract);
        Assert.Contains("Never install or control", contract);
        Assert.Contains("ConfigGeneratorTests", contract);
        Assert.Contains("MainWindowViewModelCharacterizationTests", contract);
        Assert.Contains("ReleaseToolingContractTests", contract);
        Assert.Contains("If WINBRAT, WinRM or credentials are unavailable, stop and report the blocker", normalized);
        Assert.Contains("never use local mouse/screen tools as a fallback", normalized);
        Assert.Contains("Push immediately after the commit", normalized);
        Assert.Contains("Any in-progress or red result means stop", normalized);

        foreach (var zone in new[]
        {
            "VPNRouter.Core/CLAUDE.md",
            "VPNRouter.App/CLAUDE.md",
            "VPNRouter.Android/CLAUDE.md",
            "VPNRouter.CLI/CLAUDE.md",
            "VPNRouter.Service/CLAUDE.md",
            "VPNRouter.Tests/CLAUDE.md",
            ".github/workflows/CLAUDE.md",
            "packaging/CLAUDE.md",
            "plans/CLAUDE.md"
        })
        {
            Assert.Contains(zone, contract);
        }
    }

    [Theory]
    [InlineData(".agents")]
    [InlineData(".claude")]
    public void RollingShipSkill_RequiresAuthorityBranchPrAndFullPostShipGate(string skillRoot)
    {
        var skill = Read(skillRoot, "skills", "ship-rolling-candidate", "SKILL.md");

        Assert.Contains("user explicitly authorizes", skill, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("stable cut is never", skill, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("autonomous", skill, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("git push -u origin HEAD", skill);
        Assert.Contains("Do not push `HEAD:main`", skill);
        Assert.Contains("Merge requires explicit owner authorization", skill);
        Assert.Contains("exactly 16 canonical assets", skill);
        Assert.Contains("Android", skill);
        Assert.Contains("tools/post-ship-verify.ps1", skill);
        Assert.Contains("-Cycles 2", skill);
        Assert.DoesNotContain("ship autonomously", skill, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("git push github HEAD:main", skill);
        Assert.DoesNotContain("C:\\Project\\VPNRouter\\build.ps1", skill);
        Assert.DoesNotContain("14 desktop assets", skill, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(".agents")]
    [InlineData(".claude")]
    public void PhaseTaskSkill_AlwaysUsesTaskBranchAndExecutableReviewGate(string skillRoot)
    {
        var skill = Read(skillRoot, "skills", "phase-task-launcher", "SKILL.md");
        var template = Read(skillRoot, "skills", "phase-task-launcher", "references", "brief-template.md");

        Assert.Contains("Never work directly on `main`", skill);
        Assert.Contains("`bug-hunt`", skill);
        Assert.Contains("git push -u origin HEAD", skill);
        Assert.Contains("tools/verify-last-commit-ci.ps1", skill);
        Assert.DoesNotContain("Phase 1 quick wins -> directly on `main`", skill);
        Assert.DoesNotContain("git push github", skill);
        Assert.DoesNotContain("`simplify`", skill);
        Assert.DoesNotContain("`security-review`", skill);
        Assert.Contains("task branch to origin", template);
        Assert.Contains("tools/post-ship-verify.ps1", template);
        Assert.Contains("WINBRAT @ `100.115.182.0`", template);
        Assert.Contains("no developer-machine fallback", template);
        Assert.DoesNotContain("github + origin", template);
        Assert.DoesNotContain("MCP screenshot", template);
    }

    [Fact]
    public void PhaseTaskTemplates_AreExactMirrors()
    {
        Assert.Equal(
            NormalizeLineEndings(Read(".agents", "skills", "phase-task-launcher", "references", "brief-template.md")),
            NormalizeLineEndings(Read(".claude", "skills", "phase-task-launcher", "references", "brief-template.md")));
    }

    [Theory]
    [InlineData(".agents")]
    [InlineData(".claude")]
    public void RequiredRepositorySkillsExist(string skillRoot)
    {
        foreach (var skill in new[]
        {
            "phase-task-launcher",
            "bug-hunt",
            "ship-rolling-candidate",
            "cut-stable",
            "post-ship-mcp-verify"
        })
        {
            Assert.True(
                File.Exists(Path.Combine(FindRoot(), skillRoot, "skills", skill, "SKILL.md")),
                $"Required skill is missing: {skillRoot}/skills/{skill}/SKILL.md");
        }
    }

    [Fact]
    public void PublicReadmes_ReferenceVersionedCredentialFreeScreenshots()
    {
        var readme = Read("README.md");
        var readmeRu = Read("README.ru.md");
        var screenshots = new[]
        {
            "page-simple.png",
            "page-applications.png",
            "page-tools.png"
        };

        foreach (var screenshot in screenshots)
        {
            Assert.Contains($"VPNRouter.Tests/screenshots/{screenshot}", readme);
            Assert.Contains($"VPNRouter.Tests/screenshots/{screenshot}", readmeRu);
            Assert.True(
                File.Exists(Path.Combine(FindRoot(), "VPNRouter.Tests", "screenshots", screenshot)),
                $"README screenshot is missing: {screenshot}");
        }

        Assert.DoesNotContain("Screenshots coming soon", readme);
        Assert.DoesNotContain("Скриншоты скоро", readmeRu);
    }

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine(new[] { FindRoot() }.Concat(parts).ToArray()));

    private static string NormalizeWhitespace(string value) =>
        System.Text.RegularExpressions.Regex.Replace(value, @"\s+", " ");

    private static string NormalizeLineEndings(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal);

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
