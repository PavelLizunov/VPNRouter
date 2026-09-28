#nullable enable

using System.Text;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;

namespace VPNRouter.Tests;

public sealed class HostsManagerTests
{
    private const string FakeHostsPath = @"C:\Test\Windows\System32\drivers\etc\hosts";

    private const string DiscordMarkerStart = "# === VPNRouter Discord hosts START ===";
    private const string DiscordMarkerEnd = "# === VPNRouter Discord hosts END ===";
    private const string FlowsealMarkerStart = "# === VPNRouter Flowseal hosts START ===";
    private const string FlowsealMarkerEnd = "# === VPNRouter Flowseal hosts END ===";
    private const int FinlandRangeStart = 10000;
    private const int FinlandRangeEnd = 10199;
    private const int FinlandCount = FinlandRangeEnd - FinlandRangeStart + 1;
    private const string DiscordIp = "104.25.158.178";

    private static HostsManager NewManager(InMemoryFileSystem fs)
        => new(fs, FakeHostsPath);

    [Fact]
    public void Install_FreshHostsFile_AppendsSignedDiscordBlock()
    {
        var fs = new InMemoryFileSystem();
        fs.Seed(FakeHostsPath, "127.0.0.1 localhost\n");
        var (ok, msg) = NewManager(fs).InstallInstance();

        Assert.True(ok);
        Assert.Contains("Added", msg);

        var content = fs.ReadAllText(FakeHostsPath);
        Assert.Contains("127.0.0.1 localhost", content);
        Assert.Contains(DiscordMarkerStart, content);
        Assert.Contains(DiscordMarkerEnd, content);
        Assert.Contains($"{DiscordIp} finland{FinlandRangeStart}.discord.media", content);
        Assert.Contains($"{DiscordIp} finland{FinlandRangeEnd}.discord.media", content);

        Assert.Equal(FinlandCount, content.Split('\n')
            .Count(l => l.StartsWith(DiscordIp, StringComparison.Ordinal)));
    }

    [Fact]
    public void IsInstalled_AfterInstall_ReportsTrue()
    {
        var fs = new InMemoryFileSystem();
        fs.Seed(FakeHostsPath, "127.0.0.1 localhost\n");
        var sut = NewManager(fs);

        Assert.False(sut.IsInstalledInstance());

        sut.InstallInstance();

        Assert.True(sut.IsInstalledInstance());
    }

    [Fact]
    public void Install_CalledTwice_NeverDuplicatesDiscordBlock()
    {
        var fs = new InMemoryFileSystem();
        fs.Seed(FakeHostsPath, "127.0.0.1 localhost\n");
        var sut = NewManager(fs);

        var first = sut.InstallInstance();
        var second = sut.InstallInstance();

        Assert.True(first.success);
        Assert.True(second.success);
        Assert.Equal("Already installed", second.message);

        var content = fs.ReadAllText(FakeHostsPath);
        Assert.Equal(1, CountOccurrences(content, DiscordMarkerStart));
        Assert.Equal(1, CountOccurrences(content, DiscordMarkerEnd));

        var ipLines = content
            .Split('\n')
            .Count(l => l.StartsWith(DiscordIp, StringComparison.Ordinal));
        Assert.Equal(FinlandCount, ipLines);
    }

    [Fact]
    public void Uninstall_WhenNotInstalled_NoOps()
    {
        var fs = new InMemoryFileSystem();
        fs.Seed(FakeHostsPath, "127.0.0.1 localhost\n");
        var sut = NewManager(fs);

        var (ok, msg) = sut.UninstallInstance();

        Assert.True(ok);
        Assert.Equal("Not installed", msg);

        Assert.Equal("127.0.0.1 localhost\n", fs.ReadAllText(FakeHostsPath));
    }

    [Fact]
    public void Install_WhenAppendThrowsIO_SurfacesError()
    {
        var fs = new ThrowingFileSystem
        {
            ThrowOnAppendAllLines = new IOException("simulated disk full")
        };
        fs.Seed(FakeHostsPath, "127.0.0.1 localhost\n");
        var sut = new HostsManager(fs, FakeHostsPath);

        var (ok, msg) = sut.InstallInstance();

        Assert.False(ok);
        Assert.Contains("Error", msg, StringComparison.Ordinal);
        Assert.Contains("simulated disk full", msg, StringComparison.Ordinal);
    }

    [Fact]
    public void Install_WhenUnauthorizedAccess_SurfacesAccessDeniedMessage()
    {
        var fs = new ThrowingFileSystem
        {
            ThrowOnAppendAllLines = new UnauthorizedAccessException("denied")
        };
        fs.Seed(FakeHostsPath, "127.0.0.1 localhost\n");
        var sut = new HostsManager(fs, FakeHostsPath);

        var (ok, msg) = sut.InstallInstance();

        Assert.False(ok);
        Assert.Contains("Access denied", msg, StringComparison.Ordinal);
        Assert.Contains("administrator", msg, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Install_PreservesExistingUserEntries()
    {
        var userHosts =
            "127.0.0.1 localhost\n" +
            "::1 localhost\n" +
            "127.0.0.1 dev.example.com\n" +
            "10.0.0.5 corp-vpn-internal\n" +
            "# user's custom comment\n" +
            "0.0.0.0 ads.evil.tld\n";
        var fs = new InMemoryFileSystem();
        fs.Seed(FakeHostsPath, userHosts);
        var sut = NewManager(fs);

        var (ok, _) = sut.InstallInstance();
        Assert.True(ok);

        var contentAfter = fs.ReadAllText(FakeHostsPath);
        Assert.StartsWith(userHosts, contentAfter);
        Assert.Contains(DiscordMarkerStart, contentAfter);
        Assert.Contains(DiscordMarkerEnd, contentAfter);
    }

    [Fact]
    public void IsInstalled_WhenHostsFileMissing_ReturnsFalseInsteadOfThrowing()
    {
        var fs = new InMemoryFileSystem();
        var sut = NewManager(fs);

        var result = sut.IsInstalledInstance();

        Assert.False(result);
    }

    [Fact]
    public void RoundTrip_InstallThenUninstall_RestoresOriginalContent()
    {
        var original =
            "127.0.0.1 localhost\n" +
            "::1 localhost\n" +
            "127.0.0.1 dev.example.com\n" +
            "0.0.0.0 ads.evil.tld\n";
        var fs = new InMemoryFileSystem();
        fs.Seed(FakeHostsPath, original);
        var sut = NewManager(fs);

        Assert.True(sut.InstallInstance().success);
        Assert.Contains(DiscordMarkerStart, fs.ReadAllText(FakeHostsPath));

        Assert.True(sut.UninstallInstance().success);
        var afterUninstall = fs.ReadAllText(FakeHostsPath);

        Assert.DoesNotContain(DiscordMarkerStart, afterUninstall);
        Assert.DoesNotContain(DiscordMarkerEnd, afterUninstall);
        Assert.DoesNotContain("finland", afterUninstall);

        foreach (var line in original.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            Assert.Contains(line, afterUninstall);
    }

    [Fact]
    public void Uninstall_DoesNotTouchUserCustomEntriesAddedAfterInstall()
    {
        var fs = new InMemoryFileSystem();
        fs.Seed(FakeHostsPath, "127.0.0.1 localhost\n");
        var sut = NewManager(fs);

        Assert.True(sut.InstallInstance().success);

        var withUserAppended = fs.ReadAllText(FakeHostsPath).TrimEnd('\r', '\n')
            + Environment.NewLine
            + "# user-added later" + Environment.NewLine
            + "10.0.0.7 some-internal-host" + Environment.NewLine;
        fs.Seed(FakeHostsPath, withUserAppended);

        Assert.True(sut.UninstallInstance().success);

        var after = fs.ReadAllText(FakeHostsPath);
        Assert.Contains("127.0.0.1 localhost", after);
        Assert.Contains("# user-added later", after);
        Assert.Contains("10.0.0.7 some-internal-host", after);
        Assert.DoesNotContain(DiscordMarkerStart, after);
        Assert.DoesNotContain("finland", after);
    }

    [Fact]
    public void StripBlock_RemovesOnlyMarkedRange()
    {
        var input = new List<string>
        {
            "127.0.0.1 localhost",
            "",
            DiscordMarkerStart,
            "104.25.158.178 finland10000.discord.media",
            "104.25.158.178 finland10001.discord.media",
            DiscordMarkerEnd,
            "10.0.0.1 keep-me-too"
        };

        var result = HostsManager.StripBlock(input, DiscordMarkerStart, DiscordMarkerEnd);

        Assert.Contains("127.0.0.1 localhost", result);
        Assert.Contains("10.0.0.1 keep-me-too", result);
        Assert.DoesNotContain(DiscordMarkerStart, result);
        Assert.DoesNotContain(DiscordMarkerEnd, result);
        Assert.DoesNotContain("104.25.158.178 finland10000.discord.media", result);
        Assert.DoesNotContain("104.25.158.178 finland10001.discord.media", result);
    }

    [Fact]
    public async Task BothFeatures_DiscordThenFlowseal_NoDiscordMediaLineDuplicated()
    {
        var fs = new InMemoryFileSystem();
        fs.Seed(FakeHostsPath, "127.0.0.1 localhost\n");
        var sut = NewManagerWithFlowseal(fs, BuildFlowsealBodyWithDiscordRange());

        Assert.True(sut.InstallInstance().success);
        Assert.True((await sut.InstallFlowsealInstanceAsync()).success);

        var content = fs.ReadAllText(FakeHostsPath);
        AssertNoDiscordMediaDuplicates(content);
        Assert.Equal(FinlandCount, CountDiscordMediaHostLines(content));
        Assert.Contains("www.youtube.com", content);
    }

    [Fact]
    public async Task BothFeatures_FlowsealThenDiscord_NoDiscordMediaLineDuplicated()
    {
        var fs = new InMemoryFileSystem();
        fs.Seed(FakeHostsPath, "127.0.0.1 localhost\n");
        var sut = NewManagerWithFlowseal(fs, BuildFlowsealBodyWithDiscordRange());

        Assert.True((await sut.InstallFlowsealInstanceAsync()).success);
        Assert.True(sut.InstallInstance().success);

        var content = fs.ReadAllText(FakeHostsPath);
        AssertNoDiscordMediaDuplicates(content);
        Assert.Equal(FinlandCount, CountDiscordMediaHostLines(content));
        Assert.Contains("www.youtube.com", content);
    }

    [Fact]
    public async Task FlowsealOnly_WithoutDiscordBlock_RetainsDiscordMediaEntries()
    {
        var fs = new InMemoryFileSystem();
        fs.Seed(FakeHostsPath, "127.0.0.1 localhost\n");
        var sut = NewManagerWithFlowseal(fs, BuildFlowsealBodyWithDiscordRange());

        Assert.True((await sut.InstallFlowsealInstanceAsync()).success);

        var content = fs.ReadAllText(FakeHostsPath);
        Assert.False(sut.IsInstalledInstance());
        Assert.Contains($"finland{FinlandRangeStart}.discord.media", content);
        Assert.Contains($"finland{FinlandRangeEnd}.discord.media", content);
        Assert.Equal(FinlandCount, CountDiscordMediaHostLines(content));
    }

    [Fact]
    public void Reconcile_PreDuplicatedFile_StripsFlowsealCopyKeepsNativeOwner()
    {
        var fs = new InMemoryFileSystem();
        fs.Seed(FakeHostsPath, BuildPreDuplicatedHostsFile());
        var sut = NewManagerWithRunner(fs);

        Assert.Equal(FinlandCount * 2, CountDiscordMediaHostLines(fs.ReadAllText(FakeHostsPath)));

        var (changed, _) = sut.ReconcileDiscordDuplicatesInstance();

        Assert.True(changed);
        var content = fs.ReadAllText(FakeHostsPath);
        AssertNoDiscordMediaDuplicates(content);
        Assert.Equal(FinlandCount, CountDiscordMediaHostLines(content));
        Assert.Contains(DiscordMarkerStart, content);
        Assert.Contains(DiscordMarkerEnd, content);
        Assert.Contains(FlowsealMarkerStart, content);
        Assert.Contains(FlowsealMarkerEnd, content);
        Assert.Contains("www.youtube.com", content);

        Assert.False(sut.ReconcileDiscordDuplicatesInstance().changed);
    }

    [Fact]
    public void Reconcile_OnlyDiscordBlock_IsNoOp()
    {
        var fs = new InMemoryFileSystem();
        fs.Seed(FakeHostsPath, "127.0.0.1 localhost\n");
        var sut = NewManagerWithRunner(fs);
        Assert.True(sut.InstallInstance().success);

        var (changed, msg) = sut.ReconcileDiscordDuplicatesInstance();

        Assert.False(changed);
        Assert.Equal("Nothing to reconcile", msg);
        Assert.Equal(FinlandCount, CountDiscordMediaHostLines(fs.ReadAllText(FakeHostsPath)));
    }

    [Fact]
    public async Task BothFeatures_UninstallBoth_RestoresOriginalNoFinlandRemains()
    {
        var fs = new InMemoryFileSystem();
        fs.Seed(FakeHostsPath, "127.0.0.1 localhost\n");
        var sut = NewManagerWithFlowseal(fs, BuildFlowsealBodyWithDiscordRange());

        Assert.True(sut.InstallInstance().success);
        Assert.True((await sut.InstallFlowsealInstanceAsync()).success);

        Assert.True(sut.UninstallFlowsealInstance().success);
        Assert.True(sut.UninstallInstance().success);

        var after = fs.ReadAllText(FakeHostsPath);
        Assert.DoesNotContain("finland", after);
        Assert.DoesNotContain(DiscordMarkerStart, after);
        Assert.DoesNotContain(FlowsealMarkerStart, after);
        Assert.Contains("127.0.0.1 localhost", after);
    }

    [Theory]
    [InlineData("104.25.158.178 finland10000.discord.media", true)]
    [InlineData("104.25.158.178 discord.media", true)]
    [InlineData("   104.25.158.178   finland10042.discord.media   ", true)]
    [InlineData("104.25.158.178 finland10000.discord.media.", true)]
    [InlineData("104.16.0.2 www.youtube.com", false)]
    [InlineData("# 104.25.158.178 finland10000.discord.media", false)]
    [InlineData("127.0.0.1 discord.media.evil.com", false)]
    [InlineData("104.25.158.178", false)]
    [InlineData("", false)]
    public void IsDiscordMediaHostLine_ClassifiesLine(string line, bool expected)
        => Assert.Equal(expected, HostsManager.IsDiscordMediaHostLine(line));

    [Theory]
    [InlineData("185.199.109.133 release-assets.githubusercontent.com", false)]
    [InlineData("185.199.109.133 RELEASE-ASSETS.GitHubUserContent.com", false)]
    [InlineData("185.199.109.133 release-assets.githubusercontent.com.", false)]
    [InlineData("185.199.109.133 objects.githubusercontent.com", false)]
    [InlineData("140.82.121.3 github.com", false)]
    [InlineData("140.82.121.6 api.github.com", false)]
    [InlineData("185.199.109.133 raw.githubusercontent.com", true)]
    [InlineData("185.199.108.133 avatars.githubusercontent.com", true)]
    [InlineData("149.154.167.220 telegram.org", true)]
    [InlineData("104.25.158.178 finland10000.discord.media", true)]
    [InlineData("127.0.0.1 localhost", true)]
    public void StripUpdatePathGitHubPins_KeepsExpectedHosts(string line, bool kept)
    {
        var result = HostsManager.StripUpdatePathGitHubPins(new[] { line });
        Assert.Equal(kept, result.Count == 1 && result[0] == line);
    }

    [Fact]
    public void StripUpdatePathGitHubPins_MultiHostLine_KeepsSurvivorsDropsCriticalHost()
    {
        var input = new[]
        {
            "185.199.109.133 release-assets.githubusercontent.com keep.example.com",
            "140.82.121.3 api.github.com",
        };

        var result = HostsManager.StripUpdatePathGitHubPins(input);

        Assert.Contains("185.199.109.133 keep.example.com", result);
        Assert.DoesNotContain(result, l => l.Contains("release-assets", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result, l => l.Contains("api.github.com", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task InstallFlowseal_DropsReleaseAssetsPin_ButKeepsDpiBypassEntries()
    {
        var body =
            "# Flowseal zapret-discord-youtube hosts\n" +
            "149.154.167.220 telegram.org\n" +
            "149.154.167.220 t.me\n" +
            "185.199.109.133 raw.githubusercontent.com\n" +
            "185.199.109.133 release-assets.githubusercontent.com\n" +
            "185.199.108.133 avatars.githubusercontent.com\n" +
            "104.25.158.178 finland10000.discord.media\n";
        var fs = new InMemoryFileSystem();
        fs.Seed(FakeHostsPath, "127.0.0.1 localhost\n");
        var sut = NewManagerWithFlowseal(fs, body);

        Assert.True((await sut.InstallFlowsealInstanceAsync()).success);

        var content = fs.ReadAllText(FakeHostsPath);
        Assert.Contains(FlowsealMarkerStart, content);
        Assert.Contains("149.154.167.220 telegram.org", content);
        Assert.Contains("185.199.109.133 raw.githubusercontent.com", content);
        Assert.Contains("185.199.108.133 avatars.githubusercontent.com", content);
        Assert.Contains("finland10000.discord.media", content);
        Assert.DoesNotContain("release-assets.githubusercontent.com", content);
    }

    private static HostsManager NewManagerWithFlowseal(InMemoryFileSystem fs, string flowsealBody)
    {
        var http = new FakeHttpClient().Setup("Flowseal/zapret-discord-youtube", flowsealBody);
        return new HostsManager(fs, FakeHostsPath, http, StubRunner());
    }

    private static HostsManager NewManagerWithRunner(InMemoryFileSystem fs)
        => new(fs, FakeHostsPath, http: null, runner: StubRunner());

    private static FakeProcessRunner StubRunner()
    {
        var runner = new FakeProcessRunner();
        runner.OnRun(_ => true,
            new ProcessResult(ExitCode: 0, Stdout: "", Stderr: "",
                Duration: TimeSpan.FromMilliseconds(10), TimedOut: false));
        return runner;
    }

    private static string BuildFlowsealBodyWithDiscordRange()
    {
        var sb = new StringBuilder();
        sb.Append("# Flowseal zapret-discord-youtube hosts\n");
        sb.Append("104.16.0.1 youtubei.googleapis.com\n");
        sb.Append("104.16.0.2 www.youtube.com\n");
        for (int i = FinlandRangeStart; i <= FinlandRangeEnd; i++)
            sb.Append(DiscordIp).Append(" finland").Append(i).Append(".discord.media\n");
        return sb.ToString();
    }

    private static string BuildPreDuplicatedHostsFile()
    {
        var sb = new StringBuilder();
        sb.Append("127.0.0.1 localhost\n\n");

        sb.Append(DiscordMarkerStart).Append('\n');
        for (int i = FinlandRangeStart; i <= FinlandRangeEnd; i++)
            sb.Append(DiscordIp).Append(" finland").Append(i).Append(".discord.media\n");
        sb.Append(DiscordMarkerEnd).Append("\n\n");

        sb.Append(FlowsealMarkerStart).Append('\n');
        sb.Append("104.16.0.2 www.youtube.com\n");
        for (int i = FinlandRangeStart; i <= FinlandRangeEnd; i++)
            sb.Append(DiscordIp).Append(" finland").Append(i).Append(".discord.media\n");
        sb.Append(FlowsealMarkerEnd).Append('\n');

        return sb.ToString();
    }

    private static int CountDiscordMediaHostLines(string content) => content
        .Split('\n')
        .Count(l => !l.TrimStart().StartsWith("#", StringComparison.Ordinal)
                    && l.Contains(".discord.media", StringComparison.OrdinalIgnoreCase));

    private static void AssertNoDiscordMediaDuplicates(string content)
    {
        var hosts = content
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith("#", StringComparison.Ordinal))
            .Select(l => l.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .Where(t => t.Length >= 2 && t[1].EndsWith(".discord.media", StringComparison.OrdinalIgnoreCase))
            .Select(t => t[1].ToLowerInvariant())
            .ToList();

        var dups = hosts.GroupBy(h => h).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.True(dups.Count == 0,
            $"discord.media host(s) duplicated: {string.Join(", ", dups)}");
        Assert.NotEmpty(hosts);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        if (string.IsNullOrEmpty(needle)) return 0;
        var count = 0;
        var idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) != -1)
        {
            count++;
            idx += needle.Length;
        }
        return count;
    }
}
