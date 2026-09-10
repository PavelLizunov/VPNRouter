using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

namespace VPNRouter.Tests;

public sealed class PlatformReleaseWorkflowTests
{
    [Theory]
    [InlineData("mac")]
    [InlineData("linux")]
    [InlineData("android")]
    public void PlatformWorkflow_ValidatesInputBeforeExactShaCheckout(string platform)
    {
        var steps = Steps(platform);
        var validate = steps[0];
        Assert.Equal("version", Text(validate, "id"));
        Assert.Equal("bash", Text(validate, "shell"));
        var env = (YamlMappingNode)validate.Children[new YamlScalarNode("env")];
        Assert.Equal("${{ inputs.version }}", Text(env, "INPUT_VERSION"));
        var script = Text(validate, "run");
        Assert.DoesNotContain("${{", script);
        Assert.Contains("set -euo pipefail", script);
        Assert.Contains("VERSION=\"$INPUT_VERSION\"", script);
        Assert.Contains("VERSION=\"${GITHUB_REF_NAME#v}\"", script);
        Assert.Contains("[ \"$GITHUB_REF\" != \"refs/tags/$TAG\" ]", script);
        Assert.Contains("--ref $TAG", script);
        Assert.Equal(2, Regex.Matches(script, "exit 1").Count);
        Assert.True(script.IndexOf("exit 1", StringComparison.Ordinal) <
                    script.IndexOf("GITHUB_OUTPUT", StringComparison.Ordinal));

        var grammar = Regex.Match(script, @"! ""\$VERSION"" =~ (.+) \]\]").Groups[1].Value;
        Assert.NotEmpty(grammar);
        foreach (var value in new[] { "0.0.0", "2.49.3", "2.49.4-r1", "10.20.300-r42" })
            Assert.Matches(grammar, value);
        foreach (var value in new[] { "", "v2.49.3", "2.49", "02.49.3", "2.49.3-r0", "2.49.3-r01", "2.49.3;id", "$(id)", "2.49.3\nother" })
            Assert.DoesNotMatch(grammar, value);

        Assert.StartsWith("actions/checkout@", Text(steps[1], "uses"));
        var checkout = (YamlMappingNode)steps[1].Children[new YamlScalarNode("with")];
        Assert.Equal("${{ github.sha }}", Text(checkout, "ref"));
        Assert.Equal("Verify requested tag and full AppVersion", Text(steps[2], "name"));
        var provenance = Text(steps[2], "run");
        Assert.Contains("git fetch --no-tags origin \"refs/tags/$TAG\"", provenance);
        Assert.Contains("git rev-parse 'FETCH_HEAD^{commit}'", provenance);
        Assert.Contains("[ \"$TAG_SHA\" != \"$GITHUB_SHA\" ]", provenance);
        Assert.Contains("[ \"$(git rev-parse HEAD)\" != \"$GITHUB_SHA\" ]", provenance);
        Assert.Contains("VPNRouter.Core/AppVersion.cs", provenance);
        Assert.Contains("[ \"$APPVER\" != \"$VERSION\" ]", provenance);
        Assert.Equal(2, Regex.Matches(provenance, "exit 1").Count);
    }

    [Theory]
    [InlineData("mac")]
    [InlineData("linux")]
    [InlineData("android")]
    public void PlatformWorkflow_UploadRequiresUnchangedTagAndExistingDraft(string platform)
    {
        var steps = Steps(platform);
        var upload = steps.Single(step => Text(step, "name") == "Upload to GitHub Release");
        Assert.Equal("github.event_name == 'push' || (github.event_name == 'workflow_dispatch' && inputs.upload_to_release)",
            Text(upload, "if").Trim());
        var script = Text(upload, "run");
        Assert.Contains("set -euo pipefail", script);
        var fetch = script.IndexOf("git fetch --no-tags origin \"refs/tags/$TAG\"", StringComparison.Ordinal);
        var peel = script.IndexOf("[ \"$(git rev-parse 'FETCH_HEAD^{commit}')\" != \"$GITHUB_SHA\" ]", StringComparison.Ordinal);
        var draft = script.IndexOf("IS_DRAFT=$(gh release view", StringComparison.Ordinal);
        var guard = script.IndexOf("[ \"$IS_DRAFT\" != \"true\" ]", StringComparison.Ordinal);
        var mutate = script.IndexOf("gh release upload", StringComparison.Ordinal);
        Assert.True(fetch >= 0 && peel > fetch && draft > peel && guard > draft && mutate > guard);
        Assert.Contains("--json isDraft --jq '.isDraft'", script);
        Assert.Equal(2, Regex.Matches(script, "exit 1").Count);
        Assert.DoesNotContain("exit 0", script);
        Assert.DoesNotContain("|| true", script);
        Assert.DoesNotContain("--clobber", script);
        Assert.DoesNotContain("gh release create", script);
        Assert.DoesNotContain("gh release edit", script);
        var artifactIndex = steps.FindIndex(step => Text(step, "uses").StartsWith("actions/upload-artifact@", StringComparison.Ordinal));
        Assert.True(artifactIndex >= 0 && artifactIndex < steps.IndexOf(upload));
    }

    [Fact]
    public void AndroidWorkflow_MissingSigningOrAarCannotBecomeGreenSkippedBuild()
    {
        var steps = Steps("android");
        var signing = Text(steps.Single(step => Text(step, "name") == "Decode signing keystore"), "run");
        Assert.Contains("[ -z \"${KEYSTORE_B64:-}\" ]", signing);
        Assert.Contains("exit 1", signing);
        Assert.Contains("test -s \"$GITHUB_WORKSPACE/vpnrouter.keystore\"", signing);
        var libbox = Text(steps.Single(step => Text(step, "name") == "Provision libbox.aar from tooling release"), "run");
        Assert.Contains("if ! gh release download", libbox);
        Assert.Contains("test -s VPNRouter.Android/Lib/libbox.aar", libbox);
        Assert.Contains("[ \"$ACTUAL_SHA\" != \"$LIBBOX_AAR_SHA256\" ]", libbox);
        Assert.Equal(2, Regex.Matches(libbox, "exit 1").Count);
        var publish = Text(steps.Single(step => Text(step, "name") == "dotnet publish (android-arm64, signed)"), "run");
        Assert.Contains("[ -z \"${KS_PASS:-}\" ]", publish);
        Assert.Contains("exit 1", publish);
        foreach (var step in steps)
        {
            Assert.DoesNotContain("outputs.skip", Text(step, "if"));
            Assert.DoesNotContain("exit 0", Text(step, "run"));
            Assert.DoesNotContain("skip=true", Text(step, "run"));
            Assert.NotEqual("true", Text(step, "continue-on-error"));
        }
    }

    [Fact]
    public void MacWorkflow_DraftStagingDoesNotNotifyHomebrew()
    {
        var script = Text(Steps("mac").Single(step => Text(step, "name") == "Trigger Homebrew Cask update (stable only)"), "run");
        Assert.Contains("--json isDraft --jq '.isDraft'", script);
        var guard = script.IndexOf("[ \"$IS_DRAFT\" = \"true\" ]", StringComparison.Ordinal);
        var stop = script.IndexOf("exit 0", guard, StringComparison.Ordinal);
        var dispatch = script.IndexOf("curl -fsSL -X POST", StringComparison.Ordinal);
        Assert.True(guard >= 0 && stop > guard && dispatch > stop);
    }

    private static List<YamlMappingNode> Steps(string platform)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "VPNRouter.sln")))
            root = root.Parent;
        Assert.NotNull(root);
        using var reader = File.OpenText(Path.Combine(root!.FullName, ".github", "workflows", $"build-{platform}.yml"));
        var yaml = new YamlStream();
        yaml.Load(reader);
        Assert.Single(yaml.Documents);
        var document = (YamlMappingNode)yaml.Documents[0].RootNode;
        var jobs = (YamlMappingNode)document.Children[new YamlScalarNode("jobs")];
        var build = (YamlMappingNode)jobs.Children[new YamlScalarNode("build")];
        return ((YamlSequenceNode)build.Children[new YamlScalarNode("steps")]).Children.Cast<YamlMappingNode>().ToList();
    }

    private static string Text(YamlMappingNode mapping, string key) =>
        mapping.Children.TryGetValue(new YamlScalarNode(key), out var node) ? ((YamlScalarNode)node).Value ?? "" : "";
}
