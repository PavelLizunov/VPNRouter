namespace VPNRouter.Tests;

public sealed class AgentContextContractTests
{
    [Fact]
    public void RootBootstrap_LoadsCanonicalDshContractWithoutDuplicatingPolicy()
    {
        var bootstrap = Read("AGENTS.md");

        Assert.Contains("docs/agent-contract.md", bootstrap);
        Assert.Contains(".dsh/skills/<name>/SKILL.md", bootstrap);
        Assert.Contains("docs/test-workers.md", bootstrap);
        Assert.DoesNotContain("## Golden rules", bootstrap);
        Assert.InRange(bootstrap.Split('\n').Length, 8, 25);
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

        foreach (var zone in ExpectedAgentDocs().Where(path => path != "AGENTS.md"))
            Assert.Contains(zone, contract);
    }

    [Fact]
    public void RootAndZoneAgentDocs_ExistAndAreNonEmpty()
    {
        var root = FindRoot();

        foreach (var relativePath in ExpectedAgentDocs())
        {
            var fullPath = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(fullPath), $"Agent doc should exist at {relativePath}");
            Assert.False(string.IsNullOrWhiteSpace(File.ReadAllText(fullPath)),
                $"Agent doc at {relativePath} should not be empty");
        }
    }

    [Fact]
    public void NativeDshSkills_ExistAndHaveRequiredFrontmatter()
    {
        var skillsRoot = Path.Combine(FindRoot(), ".dsh", "skills");
        var expectedSkills = new[]
        {
            "audit-overflow-fix",
            "bug-hunt",
            "cut-stable",
            "diagnose-config",
            "merge-design-handoff",
            "phase-task-launcher",
            "post-ship-mcp-verify",
            "ship-rolling-candidate",
            "update-readme-versions"
        };

        Assert.True(Directory.Exists(skillsRoot), "Native .dsh/skills directory is missing.");

        foreach (var skillName in expectedSkills)
        {
            var skillPath = Path.Combine(skillsRoot, skillName, "SKILL.md");
            Assert.True(File.Exists(skillPath), $"Required DSH skill is missing: {skillName}");

            var text = File.ReadAllText(skillPath).Replace("\r\n", "\n", StringComparison.Ordinal);
            Assert.StartsWith("---\n", text);
            var frontmatterEnd = text.IndexOf("\n---\n", 4, StringComparison.Ordinal);
            Assert.True(frontmatterEnd > 4, $"Skill frontmatter is not closed: {skillName}");

            var frontmatter = text[4..frontmatterEnd];
            var lines = frontmatter.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(3, lines.Length);
            Assert.Equal($"name: {skillName}", lines[0]);
            Assert.StartsWith("description: ", lines[1]);
            Assert.StartsWith("whenToUse: ", lines[2]);

            foreach (var line in lines)
            {
                var value = line[(line.IndexOf(':') + 1)..].Trim();
                Assert.False(string.IsNullOrWhiteSpace(value), $"Empty frontmatter value: {skillName}");
                Assert.False(value.Contains(": ", StringComparison.Ordinal) &&
                             !value.StartsWith('"') && !value.StartsWith('\''),
                    $"Unquoted YAML colon in skill frontmatter: {skillName}");
            }
        }
    }

    [Fact]
    public void ReleaseSkills_RequireAuthorityBranchPrAndFullPostShipGate()
    {
        var rolling = Read(".dsh", "skills", "ship-rolling-candidate", "SKILL.md");
        var stable = Read(".dsh", "skills", "cut-stable", "SKILL.md");

        Assert.Contains("user explicitly authorizes", rolling, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("stable cut is never", rolling, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("git push -u origin HEAD", rolling);
        Assert.Contains("Do not push `HEAD:main`", rolling);
        Assert.Contains("Merge requires explicit owner authorization", rolling);
        Assert.Contains("exactly 16 canonical assets", rolling);
        Assert.Contains("tools/post-ship-verify.ps1", rolling);
        Assert.Contains("-Cycles 2", rolling);
        Assert.Contains("harness-test", rolling);
        Assert.Contains("read-only identity/active-job/CPU/RAM/disk/SDK preflight", rolling);
        Assert.DoesNotContain("ship autonomously", rolling, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("user explicitly authorizes", stable, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("exactly 16 canonical files", stable);
        Assert.Contains("Mandatory WINBRAT live-update gate", stable);
        Assert.Contains("tools/check-open-p0.ps1", stable);
        Assert.Contains("harness-test", stable);
        Assert.Contains("active jobs, CPU, available RAM, free disk", stable);
    }

    [Fact]
    public void PhaseTaskSkill_UsesTaskBranchAndExecutableReviewGate()
    {
        var skill = Read(".dsh", "skills", "phase-task-launcher", "SKILL.md");
        var template = Read(".dsh", "skills", "phase-task-launcher", "references", "brief-template.md");

        Assert.Contains("Create a `dsh/` task branch", skill);
        Assert.Contains("Never work directly on `main`", skill);
        Assert.Contains("`bug-hunt`", skill);
        Assert.Contains("git push -u origin HEAD", skill);
        Assert.Contains("tools/verify-last-commit-ci.ps1", skill);
        Assert.Contains("task branch to origin", template);
        Assert.Contains("tools/post-ship-verify.ps1", template);
        Assert.Contains("WINBRAT @ `100.115.182.0`", template);
        Assert.Contains("no developer-machine fallback", template);
    }

    [Fact]
    public void TestWorkersDoc_PinsResourceAndCleanupInvariants()
    {
        var doc = Read("docs", "test-workers.md");
        var normalized = NormalizeWhitespace(doc);

        Assert.Contains("control plane only", normalized, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("exact repository commit SHA", normalized);
        Assert.Contains("read-only", normalized, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("one mutable", normalized, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Never run broad cache", normalized);
        Assert.Contains("size, and age", normalized);
        Assert.Contains("Do not change VM CPU, RAM, disk allocation, lifecycle, networking, monitoring", normalized);
        Assert.Contains("Always remove artifacts created by the current deployment/test scenario", normalized);
        Assert.Contains("Do not add host memory and guest memory", normalized);
        Assert.Contains("windows-worker", doc);
        Assert.Contains("linux-worker", doc);
        Assert.Contains("mac-worker", doc);
        Assert.Contains("WINBRAT", doc);
    }

    [Fact]
    public void LegacyAgentTreesAndBootstrapFiles_AreAbsent()
    {
        var root = FindRoot();
        var legacyPaths = new[]
        {
            ".agents",
            ".claude",
            "CLAUDE.md",
            "CLAUDE.local.md",
            "VPNRouter.Core/CLAUDE.md",
            "VPNRouter.App/CLAUDE.md",
            "VPNRouter.Android/CLAUDE.md",
            "VPNRouter.CLI/CLAUDE.md",
            "VPNRouter.Service/CLAUDE.md",
            "VPNRouter.Tests/CLAUDE.md",
            ".github/workflows/CLAUDE.md",
            "packaging/CLAUDE.md",
            "plans/CLAUDE.md"
        };

        foreach (var relativePath in legacyPaths)
        {
            var fullPath = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Assert.False(File.Exists(fullPath) || Directory.Exists(fullPath),
                $"Legacy agent path should not exist: {relativePath}");
        }
    }

    [Fact]
    public void ActiveDshGuidance_DoesNotReferenceLegacyAgentPathsOrUnavailableTools()
    {
        var root = FindRoot();
        var activeFiles = new List<string>
        {
            "AGENTS.md",
            "AGENTS.local.md",
            "docs/agent-contract.md",
            "docs/test-workers.md",
            "docs/REVIEW_AGENT_PROMPT.md",
            ".github/SECRETS.md",
            "README-VM.md",
            "plans/project-cheatsheet.md",
            "plans/v3.0-execution-methodology.md",
            "plans/host-build-testvm-workflow.md",
            "VPNRouter.Core/AGENTS.md",
            "VPNRouter.App/AGENTS.md",
            "VPNRouter.Android/AGENTS.md",
            "VPNRouter.CLI/AGENTS.md",
            "VPNRouter.Service/AGENTS.md",
            "VPNRouter.Tests/AGENTS.md",
            "VPNRouter.GUI/AGENTS.md",
            "VPNRouter.Tools/AGENTS.md",
            "tools/AGENTS.md",
            ".github/workflows/AGENTS.md",
            ".githooks/AGENTS.md",
            "packaging/AGENTS.md",
            "plans/AGENTS.md",
            "design/AGENTS.md",
            ".dsh/AGENTS.md"
        };
        activeFiles.AddRange(Directory.EnumerateFiles(Path.Combine(root, ".dsh", "skills"), "*.md", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/')));

        var forbidden = new[] { "CLAUDE.md", "CLAUDE.local.md", ".claude/", ".agents/", "mcp__" };
        foreach (var activeFile in activeFiles)
        {
            var text = Read(activeFile.Split('/'));
            foreach (var token in forbidden)
                Assert.DoesNotContain(token, text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void PublicReadmes_ReferenceVersionedCredentialFreeScreenshots()
    {
        var readme = Read("README.md");
        var readmeRu = Read("README.ru.md");

        foreach (var screenshot in new[] { "page-simple.png", "page-applications.png", "page-tools.png" })
        {
            Assert.Contains($"VPNRouter.Tests/screenshots/{screenshot}", readme);
            Assert.Contains($"VPNRouter.Tests/screenshots/{screenshot}", readmeRu);
            Assert.True(File.Exists(Path.Combine(FindRoot(), "VPNRouter.Tests", "screenshots", screenshot)),
                $"README screenshot is missing: {screenshot}");
        }

        Assert.DoesNotContain("Screenshots coming soon", readme);
        Assert.DoesNotContain("Скриншоты скоро", readmeRu);
    }

    private static string[] ExpectedAgentDocs() =>
    [
        "AGENTS.md",
        "VPNRouter.Core/AGENTS.md",
        "VPNRouter.App/AGENTS.md",
        "VPNRouter.Android/AGENTS.md",
        "VPNRouter.CLI/AGENTS.md",
        "VPNRouter.Service/AGENTS.md",
        "VPNRouter.Tests/AGENTS.md",
        "VPNRouter.GUI/AGENTS.md",
        "VPNRouter.Tools/AGENTS.md",
        "tools/AGENTS.md",
        ".github/workflows/AGENTS.md",
        ".githooks/AGENTS.md",
        "packaging/AGENTS.md",
        "plans/AGENTS.md",
        "design/AGENTS.md",
        ".dsh/AGENTS.md"
    ];

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine(new[] { FindRoot() }.Concat(parts).ToArray()));

    private static string NormalizeWhitespace(string value) =>
        System.Text.RegularExpressions.Regex.Replace(value, @"\s+", " ");

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
