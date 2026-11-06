using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

namespace VPNRouter.Tests;

public sealed class AptReleaseWorkflowTests
{
    [Fact]
    public void RunIdentity_RequiresMatchingTagBeforeExactShaCheckout()
    {
        var steps = Steps();
        var guard = Text(steps[0], "run");
        Assert.Contains("set -euo pipefail", guard);
        Assert.Contains("[ \"$GITHUB_REF_TYPE\" != \"tag\" ]", guard);
        Assert.Contains("[ \"$INPUT_TAG\" != \"$GITHUB_REF_NAME\" ]", guard);
        Assert.Contains("[ \"$EVENT_TAG\" != \"$GITHUB_REF_NAME\" ]", guard);
        Assert.Contains("workflow_dispatch)", guard);
        Assert.Contains("release)", guard);
        Assert.Contains("*) exit 1", guard);
        var grammar = Regex.Match(guard, @"! ""\$GITHUB_REF_NAME"" =~ (.+) \]\]").Groups[1].Value;
        Assert.NotEmpty(grammar);
        Assert.Matches(grammar, "v2.49.3");
        Assert.Matches(grammar, "v2.49.4-r1");
        foreach (var invalid in new[] { "main", "v02.49.3", "v2.49.3-r0", "v2.49.3;id", "$(id)" })
            Assert.DoesNotMatch(grammar, invalid);
        var checkout = (YamlMappingNode)steps[1].Children[new YamlScalarNode("with")];
        Assert.Equal("${{ github.sha }}", Text(checkout, "ref"));
        var provenance = Text(steps[2], "run");
        Assert.Contains("git fetch --no-tags origin \"refs/tags/$GITHUB_REF_NAME\"", provenance);
        Assert.Contains("test \"$(git rev-parse 'FETCH_HEAD^{commit}')\" = \"$GITHUB_SHA\"", provenance);
        Assert.Contains("test \"$(git rev-parse HEAD)\" = \"$GITHUB_SHA\"", provenance);
    }

    [Fact]
    public void StableSelection_IsPaginatedAndNeverUsesCandidateInputAsPackageTag()
    {
        var script = Script("Download and validate latest published stable DEB");
        Assert.Contains("gh api --paginate", script);
        Assert.DoesNotContain("--limit 30", script);
        Assert.DoesNotContain("$INPUT_TAG", script);
        Assert.DoesNotContain("$EVENT_TAG", script);
        Assert.Contains(".draft == false and .prerelease == false", script);
        Assert.Contains(".isDraft == false and .isPrerelease == false", script);
        Assert.Contains("sort_by(.published_at) | last", script);
        Assert.Contains("error(\"No published stable release\")", script);
        var grammar = Regex.Match(script, @"\[\[ ""\$TAG"" =~ (.+) \]\]").Groups[1].Value;
        Assert.NotEmpty(grammar);
        Assert.Matches(grammar, "v2.49.3");
        foreach (var invalid in new[] { "v2.49.3-r1", "v02.49.3", "v2.49.3-beta", "v2.49", "v2.49.3;id" })
            Assert.DoesNotMatch(grammar, invalid);
        Assert.Contains("DEB=\"VPNRouter-$TAG-linux-amd64.deb\"", script);
        Assert.Contains("--pattern \"$DEB\" --pattern \"$DEB.sha256\"", script);
        Assert.Contains("test \"$FILE\" = \"$DEB\"", script);
        Assert.Contains("test \"$(wc -l < \"$DEB.sha256\")\" -eq 1", script);
        Assert.Contains("sha256sum --check --strict -", script);
        Assert.Contains("test \"$(dpkg-deb --field \"$DEB\" Package)\" = \"vpnrouter\"", script);
        Assert.Contains("test \"$(dpkg-deb --field \"$DEB\" Version)\" = \"${TAG#v}\"", script);
        Assert.Contains("test \"$(dpkg-deb --field \"$DEB\" Architecture)\" = \"amd64\"", script);
        Assert.DoesNotContain("||", script);
        Assert.DoesNotContain("exit 0", script);
    }

    [Fact]
    public void Publication_RequiresNonemptyValidatedSignedIndexesAndPreservesSiteOutputs()
    {
        var steps = Steps();
        var download = steps.FindIndex(s => Text(s, "name") == "Download and validate latest published stable DEB");
        var include = steps.FindIndex(s => Text(s, "name") == "Add validated DEB and verify signed indexes");
        var publish = steps.FindIndex(s => Text(s, "name") == "Upload validated Pages artifact");
        Assert.True(download < include && include < publish);
        Assert.DoesNotContain("rm -rf", Script("Lay out apt repo skeleton"));
        var script = Text(steps[include], "run");
        Assert.Contains("test \"${#debs[@]}\" -eq 1", script);
        Assert.Contains("reprepro -Vb gh-pages-dir/apt includedeb stable", script);
        Assert.Contains("reprepro -b gh-pages-dir/apt check", script);
        Assert.Contains("test -s gh-pages-dir/apt/dists/stable/main/binary-amd64/Packages", script);
        Assert.Contains("gpgv --keyring", script);
        Assert.Contains("Release.gpg gh-pages-dir/apt/dists/stable/Release", script);
        Assert.Contains("cmp .apt-signed-release gh-pages-dir/apt/dists/stable/Release", script);
        Assert.Contains("sha256sum --check --strict -", script);
        Assert.DoesNotContain("||", script);
        Assert.DoesNotContain("exit 0", script);
        AssertPagesDeployment(Workflow("publish-pages.yml"), "publish");
        foreach (var step in steps)
        {
            Assert.DoesNotContain("${{", Text(step, "run"));
            Assert.NotEqual("true", Text(step, "continue-on-error"));
        }
        var site = Script("Publish install scripts (Linux + Windows) to gh-pages root");
        foreach (var file in new[] { "install.sh", "install.ps1", "uninstall.ps1", "diagnose.ps1", "repair.cmd", "android/index.html", "index.html" })
            Assert.Contains("gh-pages-dir/" + file, site);
    }

    [Fact]
    public void PagesPublication_UsesFreshStagingAndNeverWritesABranch()
    {
        var workflow = Workflow("publish-pages.yml");
        Assert.Equal("Publish APT and Pages", Text(workflow, "name"));
        Assert.Equal("read", Text(Map(workflow, "permissions"), "contents"));
        Assert.Equal("mkdir gh-pages-dir", Script("Create empty Pages staging directory").Split('\n', StringSplitOptions.RemoveEmptyEntries)[1].Trim());
        var script = string.Join("\n", Steps().Select(s => Text(s, "run")));
        Assert.DoesNotContain("git push", script);
        Assert.DoesNotContain("git worktree", script);
        Assert.DoesNotContain("--orphan", script);
        AssertPagesDeployment(workflow, "publish");
        var verifier = Read("tools/verify-last-commit-ci.ps1");
        Assert.Contains("'Publish APT and Pages' = 'publish-pages.yml'", verifier);
        Assert.Contains("'publish' = @('publish-pages.yml')", verifier);
        Assert.DoesNotContain("publish-apt.yml", verifier);
    }

    [Fact]
    public void Migration_IsManualMainOnlyAndVerifiesFixedArchiveBeforeDeployment()
    {
        var workflow = Workflow("migrate-pages.yml");
        var triggers = Map(workflow, "on");
        Assert.Single(triggers.Children);
        Assert.True(triggers.Children.ContainsKey(new YamlScalarNode("workflow_dispatch")));
        Assert.Equal("read", Text(Map(workflow, "permissions"), "contents"));
        Assert.Equal("vpnrouter-pages", Text(Map(workflow, "concurrency"), "group"));
        var steps = Steps(workflow, "snapshot");
        var guard = Text(steps[0], "run");
        Assert.Contains("test \"$GITHUB_REPOSITORY\" = 'PavelLizunov/VPNRouter'", guard);
        Assert.Contains("test \"$GITHUB_EVENT_NAME\" = 'workflow_dispatch'", guard);
        Assert.Contains("test \"$GITHUB_REF_TYPE\" = 'branch'", guard);
        Assert.Contains("test \"$GITHUB_REF\" = 'refs/heads/main'", guard);
        Assert.Equal("${{ github.sha }}", Text(Map(steps[1], "with"), "ref"));
        Assert.Equal("false", Text(Map(steps[1], "with"), "persist-credentials"));
        var export = steps.Single(s => Text(s, "name") == "Export and verify fixed existing Pages snapshot");
        Assert.Equal("93abe24c28bef534d6bfe0f0f211c2fb9600f48f", Text(Map(export, "env"), "SNAPSHOT_SHA"));
        var archive = Text(export, "run");
        Assert.Contains("test \"$(git rev-parse HEAD)\" = \"$GITHUB_SHA\"", archive);
        Assert.Contains("test \"$(git rev-parse 'FETCH_HEAD^{commit}')\" = \"$SNAPSHOT_SHA\"", archive);
        Assert.Contains("git archive --format=tar \"$SNAPSHOT_SHA\"", archive);
        Assert.Contains("path.is_absolute() or '..' in path.parts", archive);
        Assert.Contains("('.git', '.github')", archive);
        Assert.Contains("not (member.isfile() or member.isdir())", archive);
        Assert.Contains("len(files) != 37", archive);
        Assert.Contains("56800902", archive);
        Assert.True(archive.IndexOf("Unsafe Pages snapshot entry", StringComparison.Ordinal) < archive.IndexOf("archive.extractall(root)", StringComparison.Ordinal));
        Assert.Contains("path.stat().st_nlink != 1", archive);
        Assert.Contains("'vpn.ninitux.com'", archive);
        var verify = steps.FindIndex(s => Text(s, "name") == "Verify existing signed APT snapshot");
        var upload = steps.FindIndex(s => Text(s, "name") == "Upload verified existing Pages artifact");
        Assert.True(2 < verify && verify < upload);
        var signature = Text(steps[verify], "run");
        Assert.Contains("gpgv --keyring", signature);
        Assert.Contains("Release.gpg gh-pages-dir/apt/dists/stable/Release", signature);
        Assert.Contains("cmp .apt-signed-release gh-pages-dir/apt/dists/stable/Release", signature);
        Assert.Contains("sha256sum --check --strict -", signature);
        var raw = Read(".github/workflows/migrate-pages.yml");
        foreach (var forbidden in new[] { "secrets.", "git push", "git worktree", "--orphan", "reprepro", "gh release", "dotnet ", "sudo apt-get" })
            Assert.DoesNotContain(forbidden, raw);
        AssertPagesDeployment(workflow, "snapshot");
    }

    private static void AssertPagesDeployment(YamlMappingNode workflow, string producer)
    {
        var jobs = Map(workflow, "jobs");
        var deploy = Map(jobs, "deploy");
        Assert.Equal(producer, Text(deploy, "needs"));
        var permissions = Map(deploy, "permissions");
        Assert.Equal("read", Text(permissions, "contents"));
        Assert.Equal("write", Text(permissions, "pages"));
        Assert.Equal("write", Text(permissions, "id-token"));
        Assert.Equal("github-pages", Text(Map(deploy, "environment"), "name"));
        var upload = Assert.Single(Steps(workflow, producer).Where(s => Text(s, "uses").StartsWith("actions/upload-pages-artifact@", StringComparison.Ordinal)));
        Assert.Matches("^actions/upload-pages-artifact@[a-f0-9]{40}$", Text(upload, "uses"));
        Assert.Equal("github-pages", Text(Map(upload, "with"), "name"));
        Assert.Equal("gh-pages-dir", Text(Map(upload, "with"), "path"));
        Assert.Equal("1", Text(Map(upload, "with"), "retention-days"));
        var deployment = Assert.Single(Steps(workflow, "deploy"));
        Assert.Matches("^actions/deploy-pages@[a-f0-9]{40}$", Text(deployment, "uses"));
        Assert.Equal("deployment", Text(deployment, "id"));
    }

    private static string Script(string name) => Text(Steps().Single(s => Text(s, "name") == name), "run");

    private static List<YamlMappingNode> Steps() => Steps(Workflow("publish-pages.yml"), "publish");

    private static List<YamlMappingNode> Steps(YamlMappingNode workflow, string job) =>
        ((YamlSequenceNode)Map(Map(workflow, "jobs"), job).Children[new YamlScalarNode("steps")]).Children.Cast<YamlMappingNode>().ToList();

    private static YamlMappingNode Workflow(string file)
    {
        using var reader = new StringReader(Read(".github/workflows/" + file));
        var yaml = new YamlStream();
        yaml.Load(reader);
        Assert.Single(yaml.Documents);
        return (YamlMappingNode)yaml.Documents[0].RootNode;
    }

    private static string Read(string path)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "VPNRouter.sln")))
            root = root.Parent;
        Assert.NotNull(root);
        return File.ReadAllText(Path.Combine(root!.FullName, path));
    }

    private static YamlMappingNode Map(YamlMappingNode node, string key) =>
        (YamlMappingNode)node.Children[new YamlScalarNode(key)];

    private static string Text(YamlMappingNode mapping, string key) =>
        mapping.Children.TryGetValue(new YamlScalarNode(key), out var node) ? ((YamlScalarNode)node).Value ?? "" : "";
}
