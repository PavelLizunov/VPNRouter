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
        var publish = steps.FindIndex(s => Text(s, "name") == "Publish gh-pages (single orphan commit, force-push)");
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
        Assert.Contains("git push --force origin HEAD:gh-pages", Text(steps[publish], "run"));
        foreach (var step in steps)
        {
            Assert.DoesNotContain("${{", Text(step, "run"));
            Assert.NotEqual("true", Text(step, "continue-on-error"));
        }
        var site = Script("Publish install scripts (Linux + Windows) to gh-pages root");
        foreach (var file in new[] { "install.sh", "install.ps1", "uninstall.ps1", "diagnose.ps1", "repair.cmd", "android/index.html", "index.html" })
            Assert.Contains("gh-pages-dir/" + file, site);
    }

    private static string Script(string name) => Text(Steps().Single(s => Text(s, "name") == name), "run");

    private static List<YamlMappingNode> Steps()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "VPNRouter.sln")))
            root = root.Parent;
        Assert.NotNull(root);
        using var reader = File.OpenText(Path.Combine(root!.FullName, ".github", "workflows", "publish-apt.yml"));
        var yaml = new YamlStream();
        yaml.Load(reader);
        Assert.Single(yaml.Documents);
        var document = (YamlMappingNode)yaml.Documents[0].RootNode;
        var jobs = (YamlMappingNode)document.Children[new YamlScalarNode("jobs")];
        var publish = (YamlMappingNode)jobs.Children[new YamlScalarNode("publish")];
        return ((YamlSequenceNode)publish.Children[new YamlScalarNode("steps")]).Children.Cast<YamlMappingNode>().ToList();
    }

    private static string Text(YamlMappingNode mapping, string key) =>
        mapping.Children.TryGetValue(new YamlScalarNode(key), out var node) ? ((YamlScalarNode)node).Value ?? "" : "";
}
