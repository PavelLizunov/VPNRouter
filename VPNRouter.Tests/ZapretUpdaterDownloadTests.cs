#nullable enable

using System.Buffers.Binary;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Serilog;
using VPNRouter.Core;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;

namespace VPNRouter.Tests;

/// <summary>
/// Characterization tests for <see cref="ZapretUpdater.DownloadAndExtractAsync"/>: how a Flowseal GitHub release
/// becomes an installed zapret folder, and which <see cref="ZapretErrorCategory"/> each failure gets.
/// The test host redirects the runtime to an isolated temporary directory.
/// </summary>
public sealed class ZapretUpdaterDownloadTests : IDisposable
{
    private const string ApiUrl = "https://api.github.com/repos/Flowseal/zapret-discord-youtube/releases/latest";
    private const string ZipUrl = "https://github.com/Flowseal/zapret-discord-youtube/releases/download/test/release.zip";

    private static readonly ILogger SilentLogger = new LoggerConfiguration().CreateLogger();

    private readonly string _originalDataDir;
    private readonly string _tempDataDir;

    public ZapretUpdaterDownloadTests()
    {
        _originalDataDir = AppPaths.DataDir;
        var tempRoot = ResolveTestDirectory(new DirectoryInfo(Path.GetTempPath()));
        _tempDataDir = Path.Combine(tempRoot, $"vpnrouter-zapret-download-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDataDir);
        AppPaths.OverrideDataDir(_tempDataDir);
    }

    private static string ResolveTestDirectory(DirectoryInfo directory)
    {
        if (directory.Parent is not { } parent) return directory.FullName;
        if (directory.ResolveLinkTarget(returnFinalTarget: true) is DirectoryInfo target) return ResolveTestDirectory(target);
        return Path.Combine(ResolveTestDirectory(parent), directory.Name);
    }

    public void Dispose()
    {
        AppPaths.OverrideDataDir(_originalDataDir);
        try { Directory.Delete(_tempDataDir, recursive: true); }
        catch { }
    }

    private static byte[] Zip(params (string Path, string Content)[] entries) =>
        ZipBytes(entries.Select(entry => (entry.Path, Encoding.UTF8.GetBytes(entry.Content))).ToArray());

    private static byte[] ZipBytes(params (string Path, byte[] Content)[] entries)
    {
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, content) in entries)
            {
                using var stream = archive.CreateEntry(path).Open();
                stream.Write(content);
            }
        }
        return ms.ToArray();
    }

    private const int PeOffset = 0x80;
    private const int CoffOffset = PeOffset + 4;
    private const int OptionalOffset = CoffOffset + 20;
    private const int SectionOffset = OptionalOffset + 240;

    private static byte[] PeImage(string component)
    {
        // One file-backed section of zeroes. These fixtures are only read as binary data.
        var driver = component.EndsWith(".sys", StringComparison.Ordinal);
        var bytes = new byte[1024];
        void Word(int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), value);
        void Dword(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
        void Qword(int offset, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(offset), value);

        "MZ"u8.CopyTo(bytes);
        Dword(0x3c, PeOffset);
        "PE\0\0"u8.CopyTo(bytes.AsSpan(PeOffset));
        Word(CoffOffset, 0x8664); // AMD64
        Word(CoffOffset + 2, 1);
        Word(CoffOffset + 16, 240);
        Word(CoffOffset + 18, component.EndsWith(".dll", StringComparison.Ordinal) ? (ushort)0x2022 : (ushort)0x0022);

        Word(OptionalOffset, 0x20b); // PE32+
        Dword(OptionalOffset + 4, 512); // SizeOfCode
        Dword(OptionalOffset + 16, 0x1000); // AddressOfEntryPoint
        Dword(OptionalOffset + 20, 0x1000); // BaseOfCode
        Qword(OptionalOffset + 24, 0x140000000);
        Dword(OptionalOffset + 32, 0x1000); // SectionAlignment
        Dword(OptionalOffset + 36, 512); // FileAlignment
        Word(OptionalOffset + 40, 6); // MajorOperatingSystemVersion
        Word(OptionalOffset + 48, 6); // MajorSubsystemVersion
        Dword(OptionalOffset + 56, 0x2000); // SizeOfImage
        Dword(OptionalOffset + 60, 512); // SizeOfHeaders
        Word(OptionalOffset + 68, driver ? (ushort)1 : (ushort)3);
        Qword(OptionalOffset + 72, 0x100000); // Stack reserve/commit and heap reserve/commit
        Qword(OptionalOffset + 80, 0x1000);
        Qword(OptionalOffset + 88, 0x100000);
        Qword(OptionalOffset + 96, 0x1000);
        Dword(OptionalOffset + 108, 16);
        Dword(OptionalOffset + 120, 0x1040); // Import directory with a terminating, zeroed descriptor
        Dword(OptionalOffset + 124, 20);
        Dword(OptionalOffset + 176, 0x1060); // GlobalPtr has an address but no size

        ".text"u8.CopyTo(bytes.AsSpan(SectionOffset));
        Dword(SectionOffset + 8, 512); // VirtualSize
        Dword(SectionOffset + 12, 0x1000); // VirtualAddress
        Dword(SectionOffset + 16, 512); // SizeOfRawData
        Dword(SectionOffset + 20, 512); // PointerToRawData
        Dword(SectionOffset + 36, 0x60000020); // Code, readable, executable
        return bytes;
    }

    private static byte[] Package(string prefix = "", string? service = null, string? strategy = null,
        Func<string, byte[]>? image = null)
    {
        image ??= PeImage;
        return ZipBytes((prefix + "bin/winws.exe", image("winws.exe")), (prefix + "bin/WinDivert.dll", image("WinDivert.dll")),
            (prefix + "bin/cygwin1.dll", image("cygwin1.dll")), (prefix + "bin/WinDivert64.sys", image("WinDivert64.sys")),
            (prefix + "service.bat", Encoding.UTF8.GetBytes(service ?? "@echo off")), (prefix + "utils/test zapret.ps1", "# fixture"u8.ToArray()),
            (prefix + "utils/check_updates.enabled", "enabled"u8.ToArray()), (prefix + "lists/list-general.txt", "example.com"u8.ToArray()),
            (prefix + "lists/ipset-all.txt", "203.0.113.0/24"u8.ToArray()),
            (prefix + "general.bat", Encoding.UTF8.GetBytes(strategy ?? "start \"\" \"%BIN%winws.exe\" --wf-tcp=443 --dpi-desync=fake")));
    }

    private static string ReleaseJson(string tag, string? zipUrl, byte[]? archive = null, string? zipball = null)
    {
        var assets = new List<object> { new { name = "release-notes.txt", browser_download_url = "https://x.test/notes.txt", size = 10 } };
        if (zipUrl != null)
            assets.Add(new { name = "zapret-discord-youtube.zip", browser_download_url = zipUrl, size = archive?.Length,
                digest = archive is null ? null : "sha256:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(archive)) });
        return zipball == null
            ? JsonSerializer.Serialize(new { tag_name = tag, assets })
            : JsonSerializer.Serialize(new { tag_name = tag, assets, zipball_url = zipball });
    }

    private static (ZapretUpdater Updater, FakeHttpClient Http, List<string> Status) Create(FakeHttpClient http)
    {
        var updater = new ZapretUpdater(SilentLogger, http);
        var status = new List<string>();
        updater.StatusChanged += status.Add;
        return (updater, http, status);
    }

    internal static FakeHttpClient ReleaseForGuiUpdate(bool validPackage)
    {
        var zip = validPackage ? Package() : Zip(("notes.txt", "fixture without a package"));
        return new FakeHttpClient().Setup("releases/latest", ReleaseJson("candidate", ZipUrl, zip)).SetupStream("release.zip", zip);
    }

    // ---- installation ----

    [Fact]
    public async Task Download_ReleaseWithSingleTopFolder_UnwrapsItInstallsFilesAndTakesVersionFromServiceBat()
    {
        var zip = Package("zapret-discord-youtube-1.9.7/", service: "@echo off\r\nset \"LOCAL_VERSION=1.9.7\"\r\n");
        var http = new FakeHttpClient()
            .Setup("releases/latest", ReleaseJson("v1.9.7", ZipUrl, zip))
            .SetupStream("release.zip", zip);
        var (updater, _, status) = Create(http);

        await updater.DownloadAndExtractAsync(TestContext.Current.CancellationToken);

        Assert.True(ZapretUpdater.IsInstalled());
        Assert.Equal("example.com", File.ReadAllText(Path.Combine(ZapretUpdater.ListsDir, "list-general.txt")));
        Assert.True(File.Exists(Path.Combine(ZapretUpdater.ZapretDir, "general.bat")));
        Assert.Equal("1.9.7", File.ReadAllText(ZapretUpdater.VersionFilePath));
        Assert.Equal("1.9.7", ZapretUpdater.GetLocalVersion());
        Assert.Equal(
            new[] { "Fetching release info...", "Downloading v1.9.7...", "Extracting...", "Installing...", "Installed 1.9.7" },
            status);

        var api = Assert.Single(http.SentRequests);
        Assert.Equal(ApiUrl, api.Uri.ToString());
        Assert.Equal("application/vnd.github.v3+json", api.Headers!["Accept"]);
        var download = Assert.Single(http.SentStreamingRequests);
        Assert.Equal(ZipUrl, download.Uri.ToString());
        Assert.Equal(TimeSpan.FromMinutes(5), download.Timeout);
    }

    [Fact]
    public async Task Download_FlatPackageWithoutEmbeddedVersion_UsesTheReleaseTag()
    {
        var zip = Package();
        var http = new FakeHttpClient()
            .Setup("releases/latest", ReleaseJson("v2.0.0", ZipUrl, zip))
            .SetupStream("release.zip", zip);
        var (updater, _, status) = Create(http);

        await updater.DownloadAndExtractAsync(TestContext.Current.CancellationToken);

        Assert.True(ZapretUpdater.IsInstalled());
        Assert.Equal("v2.0.0", File.ReadAllText(ZapretUpdater.VersionFilePath));
        Assert.Equal("Installed v2.0.0", status[^1]);
    }

    // ---- failures before the install step (run everywhere) ----------------------------------------

    [Fact]
    public async Task Download_ReleaseWithoutZipAssetOrZipball_IsInvalidAndDownloadsNothing()
    {
        var http = new FakeHttpClient().Setup("releases/latest", ReleaseJson("v1.9.7", zipUrl: null));
        var (updater, _, _) = Create(http);

        var ex = await Assert.ThrowsAsync<ZapretDownloadException>(
            () => updater.DownloadAndExtractAsync(TestContext.Current.CancellationToken));

        Assert.Equal(ZapretErrorCategory.Invalid, ex.Category);
        Assert.Contains("No ZIP asset", ex.Message);
        Assert.Empty(http.SentStreamingRequests);
    }

    [Fact]
    public async Task Download_UnverifiedZipballIsRejected()
    {
        const string zipball = "https://api.github.com/repos/Flowseal/zapret-discord-youtube/zipball/v1.9.7";
        var http = new FakeHttpClient()
            .Setup("releases/latest", ReleaseJson("v1.9.7", zipUrl: null, zipball: zipball))
            .SetupStream("zipball/v1.9.7", Zip(("docs/readme.md", "no winws in here")));
        var (updater, _, _) = Create(http);

        var ex = await Assert.ThrowsAsync<ZapretDownloadException>(
            () => updater.DownloadAndExtractAsync(TestContext.Current.CancellationToken));

        Assert.Equal(ZapretErrorCategory.Invalid, ex.Category);
        Assert.Empty(http.SentStreamingRequests);
    }

    [Fact]
    public async Task Download_ArchiveWithoutWinws_IsInvalid_ListsWhatItFoundAndInstallsNothing()
    {
        var zip = Zip(("docs/readme.md", "text"), ("lists/list-general.txt", "example.com"));
        var http = new FakeHttpClient()
            .Setup("releases/latest", ReleaseJson("v1.9.7", ZipUrl, zip))
            .SetupStream("release.zip", zip);
        var (updater, _, _) = Create(http);

        var ex = await Assert.ThrowsAsync<ZapretDownloadException>(
            () => updater.DownloadAndExtractAsync(TestContext.Current.CancellationToken));

        Assert.Equal(ZapretErrorCategory.Invalid, ex.Category);
        Assert.Contains("bin/winws.exe", ex.Message);
        Assert.Contains("docs", ex.Message);
        Assert.Contains("lists", ex.Message);
        Assert.False(ZapretUpdater.IsInstalled());
        Assert.False(Directory.Exists(ZapretUpdater.ZapretDir));
    }

    [Fact]
    public async Task Download_TopFolderPlusALooseRootFile_IsNotUnwrapped_SoWinwsIsNotFound()
    {
        var zip = Zip(("release-1.9.7/bin/winws.exe", "MZ"), ("README.txt", "loose file at the archive root"));
        var http = new FakeHttpClient()
            .Setup("releases/latest", ReleaseJson("v1.9.7", ZipUrl, zip))
            .SetupStream("release.zip", zip);
        var (updater, _, _) = Create(http);

        var ex = await Assert.ThrowsAsync<ZapretDownloadException>(
            () => updater.DownloadAndExtractAsync(TestContext.Current.CancellationToken));

        Assert.Equal(ZapretErrorCategory.Invalid, ex.Category);
        Assert.False(ZapretUpdater.IsInstalled());
    }

    [Fact]
    public async Task Download_IncompleteRuntimeCannotReplaceWorkingPackage()
    {
        var working = Package();
        var broken = Package(strategy: "rem no launch command");
        var (first, _, _) = Create(new FakeHttpClient().Setup("releases/latest", ReleaseJson("v1", ZipUrl, working)).SetupStream("release.zip", working));
        await first.DownloadAndExtractAsync(TestContext.Current.CancellationToken);
        var (next, _, _) = Create(new FakeHttpClient().Setup("releases/latest", ReleaseJson("v2", ZipUrl, broken)).SetupStream("release.zip", broken));
        await Assert.ThrowsAsync<ZapretDownloadException>(() => next.DownloadAndExtractAsync(TestContext.Current.CancellationToken));
        Assert.Equal("v1", ZapretUpdater.GetLocalVersion());
    }

    [Fact]
    public async Task Download_UpgradeKeepsDisabledUpdateChecks()
    {
        await InstallFixture("v1");
        var flag = Path.Combine(ZapretUpdater.ZapretDir, "utils", "check_updates.enabled");
        Assert.True(File.Exists(flag));
        File.Delete(flag);
        await InstallFixture("v2");
        Assert.False(File.Exists(flag));
    }

    [Theory]
    [InlineData("winws.exe", CoffOffset + 2, 0)] // No sections
    [InlineData("WinDivert.dll", SectionOffset + 16, 1024)] // Raw extent crosses EOF
    [InlineData("cygwin1.dll", SectionOffset + 8, 0x2000)] // Virtual extent exceeds SizeOfImage
    [InlineData("WinDivert64.sys", SectionOffset + 20, 0)] // Raw section overlaps headers
    [InlineData("winws.exe", OptionalOffset + 16, 0x1800)] // Entry in unmapped padding
    [InlineData("winws.exe", OptionalOffset + 120, 0x11ff)] // Import starts in .text but extends past it
    [InlineData("cygwin1.dll", OptionalOffset + 124, int.MaxValue)] // Oversized import directory
    [InlineData("WinDivert.dll", OptionalOffset + 180, 8)] // Nonzero GlobalPtr size
    [InlineData("winws.exe", OptionalOffset + 224, 0x3000)] // Unmapped CLR header in a native image
    public async Task Download_DamagedComponentCannotReplaceWorkingPackage(string component, int offset, int value)
    {
        var damaged = PeImage(component);
        BinaryPrimitives.WriteInt32LittleEndian(damaged.AsSpan(offset), value);
        await AssertRejectedUpdatePreservesComponent(component, damaged);
    }

    [Fact]
    public async Task Download_CertificateUsingAnRvaAsFileOffsetCannotReplaceWorkingDriver()
    {
        var damaged = PeImage("WinDivert64.sys");
        BinaryPrimitives.WriteUInt32LittleEndian(damaged.AsSpan(OptionalOffset + 144), 0x1000);
        BinaryPrimitives.WriteUInt32LittleEndian(damaged.AsSpan(OptionalOffset + 148), 16);
        await AssertRejectedUpdatePreservesComponent("WinDivert64.sys", damaged);
    }

    [Theory]
    [InlineData("winws.exe", "cygwin1.dll")]
    [InlineData("WinDivert.dll", "winws.exe")]
    [InlineData("WinDivert64.sys", "winws.exe")]
    public async Task Download_ComponentWithTheWrongImageRoleCannotReplaceWorkingPackage(string component, string otherRole)
    {
        await AssertRejectedUpdatePreservesComponent(component, PeImage(otherRole));
    }

    private static async Task AssertRejectedUpdatePreservesComponent(string component, byte[] replacement)
    {
        await InstallFixture("v1");
        var zip = Package(image: name => name == component ? replacement : PeImage(name));
        var error = await Assert.ThrowsAsync<ZapretDownloadException>(() => InstallFixture("v2", zip));

        Assert.Equal(ZapretErrorCategory.Invalid, error.Category);
        Assert.Contains(component, error.Message);
        Assert.Equal("v1", ZapretUpdater.GetLocalVersion());
        Assert.Equal(PeImage(component), File.ReadAllBytes(Path.Combine(ZapretUpdater.BinDir, component)));
    }

    [Theory]
    [InlineData(ZapretActions.IpSetMode.None)]
    [InlineData(ZapretActions.IpSetMode.Any)]
    public async Task Download_UpgradePreservesTheListForReturningToLoaded(ZapretActions.IpSetMode mode)
    {
        await InstallFixture("v1");
        const string loaded = "192.0.2.0/24\r\n198.51.100.0/24\r\n";
        var list = Path.Combine(ZapretUpdater.ListsDir, "ipset-all.txt");
        File.WriteAllText(list, loaded);
        ZapretActions.SetIpSetMode(mode);

        await InstallFixture("v2");

        Assert.Equal("v2", ZapretUpdater.GetLocalVersion());
        Assert.Equal(mode, ZapretActions.GetIpSetMode());
        Assert.Equal(mode == ZapretActions.IpSetMode.None ? "203.0.113.113/32" : "", File.ReadAllText(list));
        Assert.Equal(loaded, File.ReadAllText(list + ".backup"));
        ZapretActions.SetIpSetMode(ZapretActions.IpSetMode.Loaded);
        Assert.Equal(ZapretActions.IpSetMode.Loaded, ZapretActions.GetIpSetMode());
        Assert.Equal(loaded, File.ReadAllText(list));
        Assert.False(File.Exists(list + ".backup"));
    }

    private static Task InstallFixture(string tag, byte[]? zip = null)
    {
        zip ??= Package();
        var http = new FakeHttpClient()
            .Setup("releases/latest", ReleaseJson(tag, ZipUrl, zip))
            .SetupStream("release.zip", zip);
        return Create(http).Updater.DownloadAndExtractAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Download_DigestMismatchDoesNotCreateAnInstallation()
    {
        var zip = Zip(("bin/winws.exe", "fixture"));
        var different = (byte[])zip.Clone();
        different[^1] ^= 1;
        var http = new FakeHttpClient()
            .Setup("releases/latest", ReleaseJson("v1", ZipUrl, zip))
            .SetupStream("release.zip", different);
        var (updater, _, _) = Create(http);

        var ex = await Assert.ThrowsAsync<ZapretDownloadException>(
            () => updater.DownloadAndExtractAsync(TestContext.Current.CancellationToken));
        Assert.Equal(ZapretErrorCategory.Corrupted, ex.Category);
        Assert.False(ZapretUpdater.IsInstalled());
    }

    [Fact]
    public async Task Download_BytesThatAreNotAZip_AreCorrupted()
    {
        var invalidZip = "invalid archive"u8.ToArray();
        var http = new FakeHttpClient()
            .Setup("releases/latest", ReleaseJson("v1.9.7", ZipUrl, invalidZip))
            .SetupStream("release.zip", invalidZip);
        var (updater, _, _) = Create(http);

        var ex = await Assert.ThrowsAsync<ZapretDownloadException>(
            () => updater.DownloadAndExtractAsync(TestContext.Current.CancellationToken));

        Assert.Equal(ZapretErrorCategory.Corrupted, ex.Category);
    }

    [Fact]
    public async Task Download_GitHubRateLimit_IsRetriedTwiceThenReportedAsRateLimit()
    {
        var http = new FakeHttpClient().Setup("releases/latest", "{}", statusCode: 403);
        var (updater, _, status) = Create(http);

        var ex = await Assert.ThrowsAsync<ZapretDownloadException>(
            () => updater.DownloadAndExtractAsync(TestContext.Current.CancellationToken));

        Assert.Equal(ZapretErrorCategory.GitHubRateLimit, ex.Category);
        Assert.IsType<HttpRequestException>(ex.InnerException);
        Assert.Equal(3, http.SentRequests.Count);
        Assert.Equal(2, status.Count(s => s.StartsWith("Retry ", StringComparison.Ordinal)));
    }

    private static void WriteLegacy(string relative, string content)
    {
        var path = Path.Combine(AppPaths.DataDir, "zapret", relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    [Theory]
    [InlineData(null, ZapretActions.IpSetMode.Any, false)]
    [InlineData("", ZapretActions.IpSetMode.Any, false)]
    [InlineData("203.0.113.113/32", ZapretActions.IpSetMode.None, true)]
    [InlineData("198.51.100.0/24", ZapretActions.IpSetMode.Loaded, true)]
    public async Task FirstProtectedInstallImportsOnlyLegacyFilterData(string? activeList, ZapretActions.IpSetMode mode, bool checkUpdates)
    {
        WriteLegacy("utils/game_filter.enabled", "tcp");
        if (checkUpdates) WriteLegacy("utils/check_updates.enabled", "ENABLED");
        WriteLegacy("lists/list-general-user.txt", "\uFEFF# included domains\r\ninclude.example\r\n");
        WriteLegacy("lists/list-exclude-user.txt", "exclude.example");
        WriteLegacy("lists/ipset-exclude-user.txt", "192.0.2.0/24");
        if (activeList != null) WriteLegacy("lists/ipset-all.txt", activeList);
        WriteLegacy("lists/ipset-all.txt.backup", "198.51.100.0/24\r\n2001:db8::/32\r\n");
        WriteLegacy("lists/list-general.txt", "old-package.example");
        WriteLegacy("bin/winws.exe", "old binary fixture");
        WriteLegacy("service.bat", "old script fixture");
        WriteLegacy("utils/legacy.ps1", "old helper fixture");
        Assert.False(Directory.Exists(ZapretUpdater.PackageDisplayPath));

        await InstallFixture("v1");

        Assert.Equal("v1", ZapretUpdater.GetLocalVersion());
        Assert.Equal(ZapretActions.GameFilterMode.TcpOnly, ZapretActions.GetGameFilterMode());
        Assert.Equal(checkUpdates, ZapretActions.IsAutoUpdateCheckEnabled());
        var lists = ZapretUpdater.ListsDir;
        Assert.Contains("include.example", File.ReadAllLines(Path.Combine(lists, "list-general-user.txt")));
        Assert.Equal("exclude.example", File.ReadAllText(Path.Combine(lists, "list-exclude-user.txt")).Trim());
        Assert.Equal("192.0.2.0/24", File.ReadAllText(Path.Combine(lists, "ipset-exclude-user.txt")).Trim());
        Assert.Equal("example.com", File.ReadAllText(Path.Combine(lists, "list-general.txt")));
        Assert.Equal("@echo off", File.ReadAllText(Path.Combine(ZapretUpdater.ZapretDir, "service.bat")));
        Assert.False(File.Exists(Path.Combine(ZapretUpdater.ZapretDir, "utils", "legacy.ps1")));
        Assert.Equal(PeImage("winws.exe"), File.ReadAllBytes(ZapretUpdater.WinwsExePath));
        var list = Path.Combine(lists, "ipset-all.txt");
        Assert.Equal(mode, ZapretActions.GetIpSetMode());
        if (activeList != null) Assert.Equal(activeList, File.ReadAllText(list).Trim());
        string[] saved = ["198.51.100.0/24", "2001:db8::/32"];
        Assert.Equal(saved, File.ReadAllLines(list + ".backup"));
        if (mode != ZapretActions.IpSetMode.Loaded)
        {
            ZapretActions.SetIpSetMode(ZapretActions.IpSetMode.Loaded);
            Assert.Equal(saved, File.ReadAllLines(list));
        }
    }

    [Fact]
    public async Task MissingLegacySettingsDirectoriesKeepTheParentDefaults()
    {
        WriteLegacy("version.txt", "old");

        await InstallFixture("v1");

        Assert.Equal("v1", ZapretUpdater.GetLocalVersion());
        Assert.False(ZapretActions.IsAutoUpdateCheckEnabled());
        Assert.False(ZapretActions.IsGameFilterConfigured);
        Assert.Equal(ZapretActions.IpSetMode.Any, ZapretActions.GetIpSetMode());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FirstInstallHonorsOnlyTheFlagDisabledThroughItsSetter(bool disableGame)
    {
        WriteLegacy("utils/game_filter.enabled", " MODE = ALL \r\n UDP = 5000-5010 \r\n TCP = 443, 8443 \r\n");
        WriteLegacy("utils/check_updates.enabled", "ENABLED");
        if (disableGame)
        {
            ZapretActions.SetGameFilterMode(ZapretActions.GameFilterMode.UdpOnly);
            ZapretActions.SetGameFilterMode(ZapretActions.GameFilterMode.Off);
            Assert.False(ZapretActions.IsGameFilterConfigured);
        }
        else
        {
            ZapretActions.SetAutoUpdateCheck(true);
            ZapretActions.SetAutoUpdateCheck(false);
            Assert.False(ZapretActions.IsAutoUpdateCheckEnabled());
        }

        await InstallFixture("v1");

        Assert.Equal(disableGame, ZapretActions.IsAutoUpdateCheckEnabled());
        Assert.Equal(!disableGame, ZapretActions.IsGameFilterConfigured);
        if (!disableGame)
            Assert.Equal((ZapretActions.GameFilterMode.All, "443,8443", "5000-5010"), ZapretActions.ReadGameFilter());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FirstInstallKeepsReenabledFlagWithTheOtherLegacyFlagOff(bool enableGame)
    {
        WriteLegacy("version.txt", "old");
        if (enableGame)
        {
            ZapretActions.SetGameFilterMode(ZapretActions.GameFilterMode.Off);
            ZapretActions.SetGameFilterMode(ZapretActions.GameFilterMode.TcpOnly);
        }
        else
        {
            ZapretActions.SetAutoUpdateCheck(false);
            ZapretActions.SetAutoUpdateCheck(true);
        }

        await InstallFixture("v1");

        Assert.Equal(!enableGame, ZapretActions.IsAutoUpdateCheckEnabled());
        Assert.Equal(enableGame ? ZapretActions.GameFilterMode.TcpOnly : ZapretActions.GameFilterMode.Off, ZapretActions.GetGameFilterMode());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FirstInstallMergesUserListsAndIpSetBackupByFile(bool protectInclude)
    {
        WriteLegacy("lists/list-general-user.txt", "old-include.example");
        WriteLegacy("lists/list-exclude-user.txt", "old-exclude.example");
        WriteLegacy("lists/ipset-exclude-user.txt", "192.0.2.1");
        WriteLegacy("lists/ipset-all.txt", "198.51.100.1");
        WriteLegacy("lists/ipset-all.txt.backup", "198.51.100.2");
        WriteLegacy("utils/game_filter.enabled", "tcp");
        var lists = Directory.CreateDirectory(ZapretUpdater.ListsDir).FullName;
        File.WriteAllText(Path.Combine(lists, protectInclude ? "list-general-user.txt" : "list-exclude-user.txt"),
            protectInclude ? "" : "new-exclude.example");
        if (protectInclude) File.WriteAllText(Path.Combine(lists, "ipset-exclude-user.txt"), "192.0.2.2");
        else File.WriteAllText(Path.Combine(lists, "ipset-all.txt.backup"), "192.0.2.3");
        ZapretActions.SetAutoUpdateCheck(true); // Both protected subdirectories now exist.

        await InstallFixture("v1");

        string Read(string name) => File.ReadAllText(Path.Combine(lists, name)).Trim();
        Assert.Equal(protectInclude ? "" : "old-include.example", Read("list-general-user.txt"));
        Assert.Equal(protectInclude ? "old-exclude.example" : "new-exclude.example", Read("list-exclude-user.txt"));
        Assert.Equal(protectInclude ? "192.0.2.2" : "192.0.2.1", Read("ipset-exclude-user.txt"));
        Assert.Equal("198.51.100.1", Read("ipset-all.txt"));
        Assert.Equal(protectInclude ? "198.51.100.2" : "192.0.2.3", Read("ipset-all.txt.backup"));
        Assert.True(ZapretActions.IsAutoUpdateCheckEnabled());
        Assert.Equal(ZapretActions.GameFilterMode.TcpOnly, ZapretActions.GetGameFilterMode());
    }

    [Theory]
    [InlineData(ZapretActions.IpSetMode.Any, true)]
    [InlineData(ZapretActions.IpSetMode.Any, false)]
    [InlineData(ZapretActions.IpSetMode.None, true)]
    [InlineData(ZapretActions.IpSetMode.None, false)]
    public async Task ExplicitIpSetModeKeepsTheLegacyRestoreList(ZapretActions.IpSetMode mode, bool hasBackup)
    {
        WriteLegacy("lists/list-general-user.txt", "legacy.example");
        WriteLegacy("lists/ipset-all.txt", "192.0.2.1");
        if (hasBackup) WriteLegacy("lists/ipset-all.txt.backup", "192.0.2.2");
        ZapretActions.SetIpSetMode(ZapretActions.IpSetMode.Loaded);
        ZapretActions.SetIpSetMode(mode);

        await InstallFixture("v1");

        var list = Path.Combine(ZapretUpdater.ListsDir, "ipset-all.txt");
        Assert.Equal(mode, ZapretActions.GetIpSetMode());
        Assert.Equal("legacy.example", File.ReadAllText(Path.Combine(ZapretUpdater.ListsDir, "list-general-user.txt")).Trim());
        var expected = hasBackup ? "192.0.2.2" : "192.0.2.1";
        Assert.Equal(expected, File.ReadAllText(list + ".backup").Trim());
        ZapretActions.SetIpSetMode(ZapretActions.IpSetMode.Loaded);
        Assert.Equal(expected, File.ReadAllText(list).Trim());
    }

    [Theory]
    [InlineData("192.0.2.1", "192.0.2.2", false, "192.0.2.1")]
    [InlineData("192.0.2.1", "192.0.2.2", true, "192.0.2.3")]
    [InlineData("203.0.113.113/32", "192.0.2.2", false, "192.0.2.2")]
    [InlineData("", null, false, "203.0.113.0/24")]
    public async Task LoadedSelectedBeforeDownloadUsesAvailableList(string active, string? backup, bool protectedBackup, string expected)
    {
        WriteLegacy("lists/ipset-all.txt", active);
        if (backup != null) WriteLegacy("lists/ipset-all.txt.backup", backup);
        ZapretActions.SetIpSetMode(ZapretActions.IpSetMode.None);
        ZapretActions.SetIpSetMode(ZapretActions.IpSetMode.Loaded);
        var list = Path.Combine(ZapretUpdater.ListsDir, "ipset-all.txt");
        if (protectedBackup) File.WriteAllText(list + ".backup", "192.0.2.3");

        await InstallFixture("v1");

        Assert.Equal(ZapretActions.IpSetMode.Loaded, ZapretActions.GetIpSetMode());
        Assert.Equal(expected, File.ReadAllText(list).Trim());
        if (backup != null) Assert.Equal(protectedBackup ? "192.0.2.3" : backup, File.ReadAllText(list + ".backup").Trim());
    }

    [Theory]
    [InlineData(false, ZapretActions.IpSetMode.None)]
    [InlineData(false, ZapretActions.IpSetMode.Loaded)]
    [InlineData(true, ZapretActions.IpSetMode.None)]
    [InlineData(true, ZapretActions.IpSetMode.Loaded)]
    public async Task ExistingProtectedSettingsTakePrecedenceOverLegacyFiles(bool installed, ZapretActions.IpSetMode mode)
    {
        if (installed) await InstallFixture("v1");
        ZapretActions.SetGameFilterMode(ZapretActions.GameFilterMode.UdpOnly);
        var lists = Directory.CreateDirectory(ZapretUpdater.ListsDir).FullName;
        var userList = Path.Combine(lists, "list-general-user.txt");
        File.WriteAllText(userList, "protected.example");
        var active = Path.Combine(lists, "ipset-all.txt");
        File.WriteAllText(active, "192.0.2.0/24");
        ZapretActions.SetIpSetMode(mode);
        WriteLegacy("utils/game_filter.enabled", "tcp");
        WriteLegacy("lists/list-general-user.txt", "legacy.example");
        WriteLegacy("lists/ipset-all.txt", "");
        WriteLegacy("lists/ipset-all.txt.backup", "198.51.100.0/24");

        await InstallFixture("v2");

        Assert.Equal("v2", ZapretUpdater.GetLocalVersion());
        Assert.Equal(ZapretActions.GameFilterMode.UdpOnly, ZapretActions.GetGameFilterMode());
        Assert.Equal("protected.example", File.ReadAllText(userList));
        Assert.Equal(mode, ZapretActions.GetIpSetMode());
        ZapretActions.SetIpSetMode(ZapretActions.IpSetMode.Loaded);
        var expected = installed && mode == ZapretActions.IpSetMode.Loaded ? "203.0.113.0/24" : "192.0.2.0/24";
        Assert.Equal(expected, File.ReadAllText(active));
    }

    [Fact]
    public async Task FirstProtectedInstallSkipsMalformedAndOversizedLegacySettings()
    {
        WriteLegacy("lists/list-general-user.txt", "valid.example\nnot a domain");
        const string entry = "oversized.example\n";
        WriteLegacy("lists/list-exclude-user.txt", string.Concat(Enumerable.Repeat(entry, 4 * 1024 * 1024 / entry.Length + 1)));
        WriteLegacy("lists/ipset-exclude-user.txt", "192.0.2.0/33");
        WriteLegacy("lists/ipset-all.txt", "not an address");
        WriteLegacy("lists/ipset-all.txt.backup", "this is not an address");
        WriteLegacy("utils/game_filter.enabled", "tcp" + new string(' ', 4096));

        await InstallFixture("v1");

        var lists = ZapretUpdater.ListsDir;
        Assert.Equal("domain.example.abc", File.ReadAllText(Path.Combine(lists, "list-general-user.txt")).Trim());
        Assert.Equal("domain.example.abc", File.ReadAllText(Path.Combine(lists, "list-exclude-user.txt")).Trim());
        Assert.Equal("203.0.113.113/32", File.ReadAllText(Path.Combine(lists, "ipset-exclude-user.txt")).Trim());
        Assert.Equal("203.0.113.0/24", File.ReadAllText(Path.Combine(lists, "ipset-all.txt")));
        Assert.False(File.Exists(Path.Combine(lists, "ipset-all.txt.backup")));
        Assert.False(ZapretActions.IsGameFilterConfigured);
        Assert.Equal("v1", ZapretUpdater.GetLocalVersion());
    }

    [Theory]
    [InlineData("zapret")]
    [InlineData("lists")]
    [InlineData("list-general-user.txt")]
    public async Task FirstProtectedInstallSkipsRedirectedLegacyData(string linkedPart)
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows() || ZapretAutoStrategy.IsRunningAsAdmin(),
            "Creating Windows symbolic links requires a privileged test account.");
        var legacy = Path.Combine(AppPaths.DataDir, "zapret");
        var target = Path.Combine(_tempDataDir, "redirect-target");
        var targetLists = Directory.CreateDirectory(Path.Combine(target, "lists")).FullName;
        var file = "list-general-user.txt";
        File.WriteAllText(Path.Combine(targetLists, file), "redirected.example");
        switch (linkedPart)
        {
            case "zapret":
                Directory.CreateSymbolicLink(legacy, target);
                break;
            case "lists":
                Directory.CreateDirectory(legacy);
                Directory.CreateSymbolicLink(Path.Combine(legacy, "lists"), targetLists);
                break;
            default:
                Directory.CreateDirectory(Path.Combine(legacy, "lists"));
                File.CreateSymbolicLink(Path.Combine(legacy, "lists", file), Path.Combine(targetLists, file));
                break;
        }

        await InstallFixture("v1");

        Assert.Equal("domain.example.abc", File.ReadAllText(Path.Combine(ZapretUpdater.ListsDir, file)).Trim());
        Assert.Equal("redirected.example", File.ReadAllText(Path.Combine(targetLists, file)));
    }
}
