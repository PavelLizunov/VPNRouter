using System.Text.RegularExpressions;

namespace VPNRouter.Tests;

/// <summary>
/// The Android adb test hook (plans/phase-h68-android-test-hook-2026-09-30.md) exposes an exported receiver that can
/// change the saved config and start the tunnel. These checks keep it out of every release path: it exists only when
/// a build is started by hand with -p:VpnRouterTestHook=true.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Layer", "Core")]
public sealed class AndroidTestHookGuardTests
{
    private const string Property = "VpnRouterTestHook";
    private const string Symbol = "VPNROUTER_TESTHOOK";

    private static readonly string[] HookSources =
    {
        "VPNRouter.Android/TestHookReceiver.cs",
        "VPNRouter.Android/AndroidApp.TestHook.cs",
    };

    [Fact]
    public void Workflows_NeverEnableTheTestHook()
    {
        var root = FindRepoRoot();
        var workflows = Directory.EnumerateFiles(Path.Combine(root, ".github", "workflows"), "*.y*ml");
        foreach (var file in workflows)
        {
            var text = File.ReadAllText(file);
            Assert.False(
                text.Contains(Property, StringComparison.OrdinalIgnoreCase) ||
                text.Contains(Symbol, StringComparison.OrdinalIgnoreCase),
                $"{Path.GetRelativePath(root, file)} must not mention the test hook.");
        }
    }

    [Fact]
    public void AndroidCsproj_EnablesTheHookOnlyThroughTheExplicitProperty()
    {
        var csproj = File.ReadAllText(Path.Combine(FindRepoRoot(), "VPNRouter.Android", "VPNRouter.Android.csproj"));

        Assert.False(
            Regex.IsMatch(csproj, $@"<{Property}[\s>]", RegexOptions.IgnoreCase),
            "The csproj must not set VpnRouterTestHook itself; it is only a command-line switch.");

        var defineLines = csproj.Split('\n').Where(l => l.Contains(Symbol, StringComparison.Ordinal)).ToList();
        Assert.Single(defineLines);
        Assert.Contains("<DefineConstants", defineLines[0], StringComparison.Ordinal);
        Assert.Contains($"Condition=\"'$({Property})' == 'true'\"", defineLines[0], StringComparison.Ordinal);
    }

    [Fact]
    public void BuildFilesOutsideTheAndroidCsproj_NeverSetTheProperty()
    {
        var root = FindRepoRoot();
        var androidCsproj = Path.GetFullPath(Path.Combine(root, "VPNRouter.Android", "VPNRouter.Android.csproj"));
        var patterns = new[] { "*.csproj", "*.props", "*.targets", "*.ps1", "*.sh", "*.yml", "*.yaml", "*.json" };

        foreach (var file in patterns.SelectMany(p => EnumerateRepoFiles(root, p)))
        {
            if (string.Equals(Path.GetFullPath(file), androidCsproj, StringComparison.OrdinalIgnoreCase)) continue;
            var text = File.ReadAllText(file);
            Assert.False(
                text.Contains(Property, StringComparison.OrdinalIgnoreCase) ||
                text.Contains(Symbol, StringComparison.OrdinalIgnoreCase),
                $"{Path.GetRelativePath(root, file)} must not mention the test hook.");
        }
    }

    [Fact]
    public void HookSources_AreWhollyInsideTheCompileGate()
    {
        var root = FindRepoRoot();
        foreach (var relative in HookSources)
        {
            var lines = File.ReadAllLines(Path.Combine(root, relative))
                .Select(l => l.Trim())
                .Where(l => l.Length > 0)
                .ToList();

            Assert.Equal($"#if {Symbol}", lines.First());
            Assert.Equal("#endif", lines.Last());
            Assert.Equal(1, lines.Count(l => l.StartsWith("#if", StringComparison.Ordinal)));
            Assert.Equal(1, lines.Count(l => l.StartsWith("#endif", StringComparison.Ordinal)));
        }
    }

    [Fact]
    public void OtherAndroidSources_MentionTheSymbolOnlyInsideAGate()
    {
        var root = FindRepoRoot();
        var androidDir = Path.Combine(root, "VPNRouter.Android");
        foreach (var file in EnumerateRepoFiles(androidDir, "*.cs"))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (HookSources.Contains(relative)) continue;

            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (!lines[i].Contains(Symbol, StringComparison.Ordinal)) continue;
                Assert.Equal($"#if {Symbol}", lines[i].Trim());
                var closes = lines.Skip(i + 1).Any(l => l.Trim() == "#endif");
                Assert.True(closes, $"{relative}:{i + 1} opens the gate without closing it.");
            }
        }
    }

    private static IEnumerable<string> EnumerateRepoFiles(string directory, string pattern) =>
        Directory.EnumerateFiles(directory, pattern, SearchOption.AllDirectories)
            .Where(f => !f.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(segment => segment is "bin" or "obj" or ".git" or ".claude" or "node_modules" or ".dsh-cache"));

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "VPNRouter.sln")))
            directory = directory.Parent;

        Assert.NotNull(directory);
        return directory!.FullName;
    }
}
