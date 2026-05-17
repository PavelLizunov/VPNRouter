#nullable enable
// Phase 2G sub-wave 7a-1 — HostsManager CRITICAL coverage. Pins the
// Discord-voice hosts-file path: wrong entries here break the user's
// resolution entirely. Drives the instance API (Wave 6 2D-2 commit
// 0480c58 made HostsManager ctor-injectable) with InMemoryFileSystem
// so no real %SystemRoot%\...\hosts mutation occurs.
//
// Brief: plans/phase2-2G-untested-services-2026-05-17.md sub-wave 7a-1.

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

    // ── Test helpers ──────────────────────────────────────────────────────────

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
