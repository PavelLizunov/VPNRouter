#nullable enable
// Phase 2G sub-wave 7a-1 — HostsManager CRITICAL coverage. Pins the
// Discord-voice hosts-file path: wrong entries here break the user's
// resolution entirely. Drives the instance API (Wave 6 2D-2 commit
// 0480c58 made HostsManager ctor-injectable) with InMemoryFileSystem
// so no real %SystemRoot%\...\hosts mutation occurs.
//
// Brief: plans/phase2-2G-untested-services-2026-05-17.md sub-wave 7a-1.

using System.Text;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;

namespace VPNRouter.Tests;

/// <summary>
/// Pin the Discord-hosts entry shape, idempotency, failure surfacing, and
/// round-trip semantics on the instance API.
/// </summary>
public sealed class HostsManagerTests
{
    private const string FakeHostsPath = @"C:\Test\Windows\System32\drivers\etc\hosts";

    private const string DiscordMarkerStart = "# === VPNRouter Discord hosts START ===";
    private const string DiscordMarkerEnd = "# === VPNRouter Discord hosts END ===";
    private const string FlowsealMarkerStart = "# === VPNRouter Flowseal hosts START ===";
    private const string FlowsealMarkerEnd = "# === VPNRouter Flowseal hosts END ===";
    private const int FinlandRangeStart = 10000;
    private const int FinlandRangeEnd = 10199;
    private const int FinlandCount = FinlandRangeEnd - FinlandRangeStart + 1; // 200
    private const string DiscordIp = "104.25.158.178";

    private static HostsManager NewManager(InMemoryFileSystem fs)
        => new(fs, FakeHostsPath);

    // ── Happy path ────────────────────────────────────────────────────────────

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

    // ── Idempotency ───────────────────────────────────────────────────────────

    [Fact]
    public void Install_CalledTwice_NeverDuplicatesDiscordBlock()
    {
        // CRITICAL invariant: must NEVER append a second copy of the block.
        // Duplication on a long-running install would blow the hosts file
        // up to megabytes over time.
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

        // File untouched.
        Assert.Equal("127.0.0.1 localhost\n", fs.ReadAllText(FakeHostsPath));
    }

    // ── Failure modes ─────────────────────────────────────────────────────────

    [Fact]
    public void Install_WhenAppendThrowsIO_SurfacesError()
    {
        // Disk full / file locked by AV / general I/O failure: service must
        // NOT throw — must return (false, error) so the UI can surface a
        // toast without crashing.
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
        // Hosts file requires admin. If we lost elevation, AppendAllLines
        // throws UnauthorizedAccessException — dedicated catch surfaces a
        // user-friendly message.
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
        // User may have hand-edited hosts to block ads, redirect a dev
        // server, etc. We must keep every existing line untouched and only
        // append our marked block.
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
        // No seed — hosts file simply doesn't exist in the fake.
        var fs = new InMemoryFileSystem();
        var sut = NewManager(fs);

        var result = sut.IsInstalledInstance();

        Assert.False(result);
    }

    // ── Round-trip ────────────────────────────────────────────────────────────

    [Fact]
    public void RoundTrip_InstallThenUninstall_RestoresOriginalContent()
    {
        // CRITICAL: after a full install+uninstall the file is
        // byte-equivalent to the original (modulo our block). User edits
        // MUST survive.
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
        // Realistic scenario: user installs block, then hand-edits a line
        // AFTER our block. Uninstall must scoop only its own block.
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
        // Unit pin on the internal helper that powers Uninstall.
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

    // ── Discord × Flowseal dedup ───────────────────────────────────────────────
    //
    // The upstream Flowseal/zapret-discord-youtube .service/hosts file bundles
    // the SAME finland*.discord.media voice entries our native Discord block
    // writes. With both features enabled older builds wrote ~200 identical
    // lines twice. The native Discord block is the canonical owner; the
    // Flowseal copy must be suppressed regardless of install order.

    [Fact]
    public async Task BothFeatures_DiscordThenFlowseal_NoDiscordMediaLineDuplicated()
    {
        // Dominant order (OneTap auto-installs Discord first, then Flowseal).
        var fs = new InMemoryFileSystem();
        fs.Seed(FakeHostsPath, "127.0.0.1 localhost\n");
        var sut = NewManagerWithFlowseal(fs, BuildFlowsealBodyWithDiscordRange());

        Assert.True(sut.InstallInstance().success);
        Assert.True((await sut.InstallFlowsealInstanceAsync()).success);

        var content = fs.ReadAllText(FakeHostsPath);
        AssertNoDiscordMediaDuplicates(content);
        // The native Discord block keeps the full range exactly once.
        Assert.Equal(FinlandCount, CountDiscordMediaHostLines(content));
        // Non-discord Flowseal overrides survive the dedup.
        Assert.Contains("www.youtube.com", content);
    }

    [Fact]
    public async Task BothFeatures_FlowsealThenDiscord_NoDiscordMediaLineDuplicated()
    {
        // Reverse order (user toggles Flowseal hosts before Discord hosts).
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
        // Flowseal alone must still fix Discord voice — we only suppress the
        // discord.media copy when the native block is present to own it.
        var fs = new InMemoryFileSystem();
        fs.Seed(FakeHostsPath, "127.0.0.1 localhost\n");
        var sut = NewManagerWithFlowseal(fs, BuildFlowsealBodyWithDiscordRange());

        Assert.True((await sut.InstallFlowsealInstanceAsync()).success);

        var content = fs.ReadAllText(FakeHostsPath);
        Assert.False(sut.IsInstalledInstance()); // Discord block NOT installed
        Assert.Contains($"finland{FinlandRangeStart}.discord.media", content);
        Assert.Contains($"finland{FinlandRangeEnd}.discord.media", content);
        Assert.Equal(FinlandCount, CountDiscordMediaHostLines(content));
    }

    [Fact]
    public void Reconcile_PreDuplicatedFile_StripsFlowsealCopyKeepsNativeOwner()
    {
        // Heal a file an older build produced: BOTH blocks carry the range.
        var fs = new InMemoryFileSystem();
        fs.Seed(FakeHostsPath, BuildPreDuplicatedHostsFile());
        var sut = NewManagerWithRunner(fs);

        // Before: each finland host appears twice.
        Assert.Equal(FinlandCount * 2, CountDiscordMediaHostLines(fs.ReadAllText(FakeHostsPath)));

        var (changed, _) = sut.ReconcileDiscordDuplicatesInstance();

        Assert.True(changed);
        var content = fs.ReadAllText(FakeHostsPath);
        AssertNoDiscordMediaDuplicates(content);
        Assert.Equal(FinlandCount, CountDiscordMediaHostLines(content));
        // Both blocks' markers round-trip intact.
        Assert.Contains(DiscordMarkerStart, content);
        Assert.Contains(DiscordMarkerEnd, content);
        Assert.Contains(FlowsealMarkerStart, content);
        Assert.Contains(FlowsealMarkerEnd, content);
        // Non-discord Flowseal override survived.
        Assert.Contains("www.youtube.com", content);

        // Idempotent: a second pass finds nothing left to reconcile.
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
        // Round-trip after dedup: removing both blocks leaves the original
        // file with zero finland/marker residue.
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
    [InlineData("104.25.158.178 finland10000.discord.media.", true)] // trailing-dot FQDN
    [InlineData("104.16.0.2 www.youtube.com", false)]
    [InlineData("# 104.25.158.178 finland10000.discord.media", false)] // comment
    [InlineData("127.0.0.1 discord.media.evil.com", false)] // look-alike, not a subdomain
    [InlineData("104.25.158.178", false)] // IP only, no host
    [InlineData("", false)]
    public void IsDiscordMediaHostLine_ClassifiesLine(string line, bool expected)
        => Assert.Equal(expected, HostsManager.IsDiscordMediaHostLine(line));

    // ── GitHub update-path pin strip ───────────────────────────────────────────
    //
    // The upstream Flowseal hosts file pins release-assets.githubusercontent.com
    // (the 302 target of every github.com/.../releases/download/... asset URL) to
    // a single hardcoded GitHub-Pages Fastly anycast IP. Carrying that pin into
    // our block can silently break VPNRouter's OWN auto-updater (stale IP / POP
    // that doesn't front release assets / censor-null-route) — stranding the user
    // on an old build. We strip update-path GitHub hosts but keep the
    // Discord/Telegram DPI-bypass entries and raw.githubusercontent.com.
    // Found from a real problem-user's hosts file 2026-06-11.

    [Theory]
    [InlineData("185.199.109.133 release-assets.githubusercontent.com", false)] // current asset 302 target
    [InlineData("185.199.109.133 RELEASE-ASSETS.GitHubUserContent.com", false)] // case-insensitive
    [InlineData("185.199.109.133 release-assets.githubusercontent.com.", false)] // trailing-dot FQDN
    [InlineData("185.199.109.133 objects.githubusercontent.com", false)]        // alternate asset target
    [InlineData("140.82.121.3 github.com", false)]                              // initial 302 source
    [InlineData("140.82.121.6 api.github.com", false)]                          // release-list endpoint
    [InlineData("185.199.109.133 raw.githubusercontent.com", true)]             // KEPT — recoverable + DPI value
    [InlineData("185.199.108.133 avatars.githubusercontent.com", true)]        // KEPT — VPNRouter never resolves it
    [InlineData("149.154.167.220 telegram.org", true)]                          // KEPT — Telegram DPI bypass
    [InlineData("104.25.158.178 finland10000.discord.media", true)]             // KEPT — Discord DPI bypass
    [InlineData("127.0.0.1 localhost", true)]                                   // KEPT — unrelated user entry
    public void StripUpdatePathGitHubPins_KeepsExpectedHosts(string line, bool kept)
    {
        var result = HostsManager.StripUpdatePathGitHubPins(new[] { line });
        // A kept line round-trips verbatim; a stripped single-host line vanishes.
        Assert.Equal(kept, result.Count == 1 && result[0] == line);
    }

    [Fact]
    public void StripUpdatePathGitHubPins_MultiHostLine_KeepsSurvivorsDropsCriticalHost()
    {
        // Defensive: hosts syntax allows several hostnames per line. Flowseal
        // ships one-per-line today, but if a future line co-locates an
        // update-critical host with an innocent one, drop only the critical
        // hostname and keep the survivor — never the whole line.
        var input = new[]
        {
            "185.199.109.133 release-assets.githubusercontent.com keep.example.com",
            "140.82.121.3 api.github.com", // whole line is update-critical
        };

        var result = HostsManager.StripUpdatePathGitHubPins(input);

        Assert.Contains("185.199.109.133 keep.example.com", result);
        Assert.DoesNotContain(result, l => l.Contains("release-assets", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result, l => l.Contains("api.github.com", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task InstallFlowseal_DropsReleaseAssetsPin_ButKeepsDpiBypassEntries()
    {
        // End-to-end through the real install path: the written Flowseal block
        // must NOT pin release-assets.githubusercontent.com (would break our own
        // updater) but MUST keep Telegram/Discord DPI-bypass + raw.githubusercontent.
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
        Assert.Contains("149.154.167.220 telegram.org", content);              // Telegram bypass kept
        Assert.Contains("185.199.109.133 raw.githubusercontent.com", content); // raw kept (DPI value)
        Assert.Contains("185.199.108.133 avatars.githubusercontent.com", content); // not our dep — kept
        Assert.Contains("finland10000.discord.media", content);                // Discord bypass kept
        // THE FIX: our own updater's redirect-target host is never pinned.
        Assert.DoesNotContain("release-assets.githubusercontent.com", content);
    }

    // ── Test helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Manager wired to a FakeHttpClient serving <paramref name="flowsealBody"/>
    /// and a FakeProcessRunner stubbing ipconfig /flushdns, so the Flowseal
    /// install path runs without real network or process spawns.
    /// </summary>
    private static HostsManager NewManagerWithFlowseal(InMemoryFileSystem fs, string flowsealBody)
    {
        var http = new FakeHttpClient().Setup("Flowseal/zapret-discord-youtube", flowsealBody);
        return new HostsManager(fs, FakeHostsPath, http, StubRunner());
    }

    /// <summary>Manager with a stub ipconfig runner but the default HTTP seam
    /// (used by paths that never fetch, e.g. Reconcile / Install).</summary>
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

    /// <summary>
    /// Synthesize an upstream Flowseal hosts file that — like the real one —
    /// bundles the full finland10000-10199.discord.media range our native
    /// Discord block also writes, plus a couple of unrelated Cloudflare
    /// overrides that must survive the dedup.
    /// </summary>
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

    /// <summary>
    /// Build a hosts file as an older (pre-dedup) build would have left it:
    /// both the Discord and Flowseal blocks carry the full finland range.
    /// </summary>
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

    /// <summary>Count non-comment hosts lines mapping a *.discord.media host.</summary>
    private static int CountDiscordMediaHostLines(string content) => content
        .Split('\n')
        .Count(l => !l.TrimStart().StartsWith("#", StringComparison.Ordinal)
                    && l.Contains(".discord.media", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Assert that no *.discord.media hostname is mapped more than once across
    /// the whole file (the core invariant) AND that at least one such mapping
    /// survives (guard against an over-aggressive strip removing them all).
    /// </summary>
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

    /// <summary>
    /// Count non-overlapping occurrences of <paramref name="needle"/> in
    /// <paramref name="haystack"/>. Used for "block appears exactly once"
    /// assertions where Assert.Single isn't enough (substring rather than
    /// list-element semantics).
    /// </summary>
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
