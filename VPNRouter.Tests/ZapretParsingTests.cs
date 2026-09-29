#nullable enable

using VPNRouter.Core;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

/// <summary>
/// Characterization tests for the parts of the Zapret integration that turn Flowseal's release files into
/// data: the winws arguments inside a general*.bat, the strategy list and its order, the installed version,
/// the probe targets and the legacy argument presets. They pin today's behaviour; they do not judge it.
/// </summary>
public sealed class ZapretParsingTests : IDisposable
{
    private const string Bin = @"C:\zapret\bin\";
    private const string Lists = @"C:\zapret\lists\";

    private readonly string _originalDataDir;
    private readonly string _tempDataDir;

    public ZapretParsingTests()
    {
        _originalDataDir = AppPaths.DataDir;
        _tempDataDir = Path.Combine(Path.GetTempPath(), $"vpnrouter-zapret-parsing-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDataDir);
        AppPaths.OverrideDataDir(_tempDataDir);
    }

    public void Dispose()
    {
        AppPaths.OverrideDataDir(_originalDataDir);
        try { Directory.Delete(_tempDataDir, recursive: true); }
        catch { }
    }

    private static string ZapretPath(params string[] parts) =>
        Path.Combine(new[] { ZapretUpdater.ZapretDir }.Concat(parts).ToArray());

    private static void WriteZapretFile(string content, params string[] parts)
    {
        var path = ZapretPath(parts);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    // ---- ExtractWinwsArgsFromLines -------------------------------------------------------------

    [Fact]
    public void ExtractWinwsArgs_RealisticGeneralBat_ResolvesPathsDropsGameFilterAndGameOnlySegment()
    {
        var lines = new[]
        {
            "@echo off",
            "chcp 65001 > nul",
            "cd /d \"%~dp0\"",
            "set \"BIN=%~dp0bin\\\"",
            "start \"zapret: %~n0\" /min \"%BIN%winws.exe\" --wf-tcp=80,443,%GameFilterTCP% --wf-udp=443,50000-50100,%GameFilterUDP% ^",
            "--filter-udp=443 --hostlist=\"%LISTS%list-general.txt\" --dpi-desync=fake --dpi-desync-repeats=6 --dpi-desync-fake-quic=\"%BIN%quic_initial_www_google_com.bin\" --new ^",
            "--filter-udp=50000-50100 --filter-l7=discord,stun --dpi-desync=fake --dpi-desync-repeats=6 --new ^",
            "--filter-tcp=80,443,%GameFilterTCP% --hostlist=\"%LISTS%list-general.txt\" --dpi-desync=multisplit --dpi-desync-split-seqovl=568 --dpi-desync-split-pos=1 --new ^",
            "--filter-udp=%GameFilterUDP% --dpi-desync=fake --dpi-desync-autottl=2 --dpi-desync-repeats=12",
        };

        var args = ZapretUpdater.ExtractWinwsArgsFromLines(lines, Bin, Lists);

        Assert.Equal(
            "--wf-tcp=80,443 --wf-udp=443,50000-50100 " +
            "--filter-udp=443 --hostlist=\"C:\\zapret\\lists\\list-general.txt\" --dpi-desync=fake --dpi-desync-repeats=6 " +
            "--dpi-desync-fake-quic=\"C:\\zapret\\bin\\quic_initial_www_google_com.bin\" " +
            "--new --filter-udp=50000-50100 --filter-l7=discord,stun --dpi-desync=fake --dpi-desync-repeats=6 " +
            "--new --filter-tcp=80,443 --hostlist=\"C:\\zapret\\lists\\list-general.txt\" --dpi-desync=multisplit " +
            "--dpi-desync-split-seqovl=568 --dpi-desync-split-pos=1",
            args);
    }

    [Fact]
    public void ExtractWinwsArgs_NoWinwsLine_ReturnsNull()
    {
        Assert.Null(ZapretUpdater.ExtractWinwsArgsFromLines(
            new[] { "@echo off", "echo nothing to start here" }, Bin, Lists));
    }

    [Fact]
    public void ExtractWinwsArgs_EmptyInput_ReturnsNull()
    {
        Assert.Null(ZapretUpdater.ExtractWinwsArgsFromLines(Array.Empty<string>(), Bin, Lists));
    }

    [Fact]
    public void ExtractWinwsArgs_GameFilterInsideList_IsRemovedWithItsComma()
    {
        var args = ZapretUpdater.ExtractWinwsArgsFromLines(
            new[] { "start \"\" \"%BIN%winws.exe\" --wf-tcp=80,%GameFilterTCP%,443" }, Bin, Lists);

        Assert.Equal("--wf-tcp=80,443", args);
    }

    [Fact]
    public void ExtractWinwsArgs_PlaceholdersAreCaseInsensitive()
    {
        var args = ZapretUpdater.ExtractWinwsArgsFromLines(
            new[] { "start \"\" \"%bin%winws.exe\" --hostlist=\"%lists%a.txt\" --fake=\"%Bin%f.bin\"" }, Bin, Lists);

        Assert.Equal("--hostlist=\"C:\\zapret\\lists\\a.txt\" --fake=\"C:\\zapret\\bin\\f.bin\"", args);
    }

    [Fact]
    public void ExtractWinwsArgs_DoubledBackslashesCollapseToOne()
    {
        var args = ZapretUpdater.ExtractWinwsArgsFromLines(
            new[] { "start \"\" \"%BIN%winws.exe\" --hostlist=C:\\\\data\\\\a.txt" }, Bin, Lists);

        Assert.Equal("--hostlist=C:\\data\\a.txt", args);
    }

    [Fact]
    public void ExtractWinwsArgs_OnlyTheFirstWinwsCommandIsRead()
    {
        var args = ZapretUpdater.ExtractWinwsArgsFromLines(
            new[]
            {
                "start \"\" \"%BIN%winws.exe\" --first=1",
                "start \"\" \"%BIN%winws.exe\" --second=2",
            }, Bin, Lists);

        Assert.Equal("--first=1", args);
    }

    // ---- ParseStrategies ------------------------------------------------------------------------

    [Fact]
    public void ParseStrategies_NoZapretDirectory_ReturnsEmpty()
    {
        Assert.Empty(ZapretUpdater.ParseStrategies());
    }

    [Fact]
    public void ParseStrategies_OrdersAlt3ThenGeneralThenAltNumbersThenOthersThenSimpleThenFakeTls()
    {
        foreach (var name in new[]
                 {
                     "general (FAKE TLS AUTO)", "general (SIMPLE FAKE)", "general (custom)",
                     "general (ALT2)", "general", "general (ALT3)",
                 })
        {
            WriteZapretFile($"start \"\" \"%BIN%winws.exe\" --wf-tcp=443 --tag=\"{name}\"", name + ".bat");
        }

        var result = ZapretUpdater.ParseStrategies();

        Assert.Equal(
            new[]
            {
                "general (ALT3)", "general", "general (ALT2)",
                "general (custom)", "general (SIMPLE FAKE)", "general (FAKE TLS AUTO)",
            },
            result.Select(s => s.Name).ToArray());
    }

    [Fact]
    public void ParseStrategies_KeepsPlaceholdersAndBatPath_AndSkipsFilesWithoutWinws()
    {
        WriteZapretFile("start \"\" \"%BIN%winws.exe\" --hostlist=\"%LISTS%x.txt\"", "general.bat");
        WriteZapretFile("@echo off\r\necho no winws here", "general (ALT9).bat");
        WriteZapretFile("start \"\" \"%BIN%winws.exe\" --should-not-be-listed", "service.bat");

        var result = ZapretUpdater.ParseStrategies();

        var only = Assert.Single(result);
        Assert.Equal("general", only.Name);
        Assert.Equal("--hostlist=\"%LISTS%x.txt\"", only.Arguments);
        Assert.Equal(ZapretPath("general.bat"), only.BatPath);
    }

    // ---- GetLocalVersion ------------------------------------------------------------------------

    [Fact]
    public void GetLocalVersion_NothingInstalled_ReturnsNull()
    {
        Assert.Null(ZapretUpdater.GetLocalVersion());
    }

    [Fact]
    public void GetLocalVersion_VersionFileWinsAndIsTrimmed()
    {
        WriteZapretFile("  1.9.7b \r\n", "version.txt");
        WriteZapretFile("set \"LOCAL_VERSION=0.0.1\"", "service.bat");

        Assert.Equal("1.9.7b", ZapretUpdater.GetLocalVersion());
    }

    [Fact]
    public void GetLocalVersion_FallsBackToServiceBatHeader()
    {
        WriteZapretFile("@echo off\r\nset \"LOCAL_VERSION=1.9.6\"\r\nrem rest", "service.bat");

        Assert.Equal("1.9.6", ZapretUpdater.GetLocalVersion());
    }

    [Fact]
    public void GetLocalVersion_ServiceBatVersionBeyondFirstFiveLines_IsIgnored()
    {
        WriteZapretFile("1\r\n2\r\n3\r\n4\r\n5\r\nset \"LOCAL_VERSION=1.9.6\"", "service.bat");

        Assert.Null(ZapretUpdater.GetLocalVersion());
    }

    // ---- ZapretManager.BuildLegacyArgs ----------------------------------------------------------

    [Fact]
    public void BuildLegacyArgs_Multisplit_DefaultPort()
    {
        Assert.Equal(
            "--wf-tcp=443,8443 --wf-l3=ipv4 --dpi-desync=multisplit --dpi-desync-split-seqovl=2 --dpi-desync-split-pos=2",
            ZapretManager.BuildLegacyArgs("multisplit"));
    }

    [Fact]
    public void BuildLegacyArgs_FakePlusMultisplit_UsesTheGivenPort()
    {
        Assert.Equal(
            "--wf-tcp=8080,8443 --wf-l3=ipv4 --dpi-desync=fake,multisplit --dpi-desync-ttl=2 " +
            "--dpi-desync-split-seqovl=2 --dpi-desync-split-pos=2 --dpi-desync-fake-tls=0x00000000000000000000",
            ZapretManager.BuildLegacyArgs("fake+multisplit", targetPort: 8080));
    }

    [Fact]
    public void BuildLegacyArgs_UnknownStrategy_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() => ZapretManager.BuildLegacyArgs("general (ALT3)"));
        Assert.Contains("Unknown legacy strategy", ex.Message);
    }

    // ---- ZapretAutoStrategy.LoadTargets ---------------------------------------------------------

    [Fact]
    public void LoadTargets_NoTargetsFile_ReturnsBuiltInDefaults()
    {
        Assert.Equal(ZapretAutoStrategy.DefaultProbeTargets, ZapretAutoStrategy.LoadTargets(null));
    }

    [Fact]
    public void LoadTargets_ReadsHttpUrlsAndSkipsCommentsPingsAndJunk()
    {
        WriteZapretFile(
            "# Flowseal test targets\r\n" +
            "\r\n" +
            "DiscordMain = \"https://discord.com\"\r\n" +
            "  YouTubeWeb = \"https://www.youtube.com\"  \r\n" +
            "PlainHttp = \"http://example.org\"\r\n" +
            "CloudflareDNS1 = \"PING:1.1.1.1\"\r\n" +
            "NoEquals https://ignored.example\r\n" +
            "Ftp = \"ftp://ignored.example\"\r\n",
            "utils", "targets.txt");

        Assert.Equal(
            new[] { "https://discord.com", "https://www.youtube.com", "http://example.org" },
            ZapretAutoStrategy.LoadTargets(null));
    }

    [Fact]
    public void LoadTargets_MoreThanTwelve_KeepsTheFirstTwelve()
    {
        WriteZapretFile(
            string.Join("\r\n", Enumerable.Range(1, 15).Select(i => $"T{i} = \"https://t{i}.example\"")),
            "utils", "targets.txt");

        var targets = ZapretAutoStrategy.LoadTargets(null);

        Assert.Equal(12, targets.Count);
        Assert.Equal("https://t1.example", targets[0]);
        Assert.Equal("https://t12.example", targets[11]);
    }

    [Fact]
    public void LoadTargets_FileWithoutUsableUrls_FallsBackToDefaults()
    {
        WriteZapretFile("Ping1 = \"PING:1.1.1.1\"\r\n# nothing else", "utils", "targets.txt");

        Assert.Equal(ZapretAutoStrategy.DefaultProbeTargets, ZapretAutoStrategy.LoadTargets(null));
    }
}
