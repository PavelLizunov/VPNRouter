#nullable enable

using VPNRouter.Core;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

/// <summary>
/// Characterization tests for the small file-based switches next to Flowseal's install: the game filter flag,
/// the ipset list mode and the update-check flag. They mirror what Flowseal's service.bat keeps in
/// utils\game_filter.enabled, lists\ipset-all.txt(.backup) and utils\check_updates.enabled.
/// </summary>
public sealed class ZapretModesTests : IDisposable
{
    private const string NoneSentinel = "203.0.113.113/32";

    private readonly string _originalDataDir;
    private readonly string _tempDataDir;

    public ZapretModesTests()
    {
        _originalDataDir = AppPaths.DataDir;
        _tempDataDir = Path.Combine(Path.GetTempPath(), $"vpnrouter-zapret-modes-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDataDir);
        AppPaths.OverrideDataDir(_tempDataDir);
    }

    public void Dispose()
    {
        AppPaths.OverrideDataDir(_originalDataDir);
        try { Directory.Delete(_tempDataDir, recursive: true); }
        catch { }
    }

    private static string GameFlag => Path.Combine(ZapretUpdater.ZapretDir, "utils", "game_filter.enabled");
    private static string UpdateFlag => Path.Combine(ZapretUpdater.ZapretDir, "utils", "check_updates.enabled");
    private static string IpSetList => Path.Combine(ZapretUpdater.ZapretDir, "lists", "ipset-all.txt");
    private static string IpSetBackup => IpSetList + ".backup";

    private static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    // ---- game filter ----------------------------------------------------------------------------

    [Fact]
    public void GameFilter_NoFlagFile_IsOffAndNotConfigured()
    {
        Assert.Equal(ZapretActions.GameFilterMode.Off, ZapretActions.GetGameFilterMode());
        Assert.False(ZapretActions.IsGameFilterConfigured);
    }

    [Theory]
    [InlineData(ZapretActions.GameFilterMode.All, "all")]
    [InlineData(ZapretActions.GameFilterMode.TcpOnly, "tcp")]
    [InlineData(ZapretActions.GameFilterMode.UdpOnly, "udp")]
    public void GameFilter_SetWritesTheWordAndGetReadsItBack(ZapretActions.GameFilterMode mode, string word)
    {
        ZapretActions.SetGameFilterMode(mode);

        Assert.Equal(word, File.ReadAllText(GameFlag));
        Assert.True(ZapretActions.IsGameFilterConfigured);
        Assert.Equal(mode, ZapretActions.GetGameFilterMode());
    }

    [Fact]
    public void GameFilter_SetOff_DeletesTheFlagFile()
    {
        ZapretActions.SetGameFilterMode(ZapretActions.GameFilterMode.All);

        ZapretActions.SetGameFilterMode(ZapretActions.GameFilterMode.Off);

        Assert.False(File.Exists(GameFlag));
        Assert.Equal(ZapretActions.GameFilterMode.Off, ZapretActions.GetGameFilterMode());
    }

    [Theory]
    [InlineData("  ALL \r\n", ZapretActions.GameFilterMode.All)]
    [InlineData("Tcp", ZapretActions.GameFilterMode.TcpOnly)]
    [InlineData("UDP\n", ZapretActions.GameFilterMode.UdpOnly)]
    [InlineData("something else", ZapretActions.GameFilterMode.Off)]
    [InlineData("", ZapretActions.GameFilterMode.Off)]
    public void GameFilter_GetIsCaseAndWhitespaceTolerant_UnknownWordMeansOff(string content, ZapretActions.GameFilterMode expected)
    {
        Write(GameFlag, content);

        Assert.Equal(expected, ZapretActions.GetGameFilterMode());
        Assert.True(ZapretActions.IsGameFilterConfigured);
    }

    // ---- ipset ----------------------------------------------------------------------------------

    [Fact]
    public void IpSet_NoListFile_IsAny()
    {
        Assert.Equal(ZapretActions.IpSetMode.Any, ZapretActions.GetIpSetMode());
    }

    [Theory]
    [InlineData("", ZapretActions.IpSetMode.Any)]
    [InlineData("  \r\n ", ZapretActions.IpSetMode.Any)]
    [InlineData(NoneSentinel, ZapretActions.IpSetMode.None)]
    [InlineData(" 203.0.113.113/32\r\n", ZapretActions.IpSetMode.None)]
    [InlineData("1.2.3.0/24\r\n5.6.7.8", ZapretActions.IpSetMode.Loaded)]
    [InlineData("203.0.113.113/32\n1.2.3.0/24", ZapretActions.IpSetMode.Loaded)]
    public void IpSet_GetClassifiesTheListContent(string content, ZapretActions.IpSetMode expected)
    {
        Write(IpSetList, content);

        Assert.Equal(expected, ZapretActions.GetIpSetMode());
    }

    [Fact]
    public void IpSet_LoadedToNone_BacksUpTheListAndWritesTheSentinel()
    {
        Write(IpSetList, "1.2.3.0/24");

        ZapretActions.SetIpSetMode(ZapretActions.IpSetMode.None);

        Assert.Equal(NoneSentinel, File.ReadAllText(IpSetList));
        Assert.Equal("1.2.3.0/24", File.ReadAllText(IpSetBackup));
        Assert.Equal(ZapretActions.IpSetMode.None, ZapretActions.GetIpSetMode());
    }

    [Fact]
    public void IpSet_LoadedToAny_BacksUpTheListAndEmptiesIt()
    {
        Write(IpSetList, "1.2.3.0/24");

        ZapretActions.SetIpSetMode(ZapretActions.IpSetMode.Any);

        Assert.Equal("", File.ReadAllText(IpSetList));
        Assert.Equal("1.2.3.0/24", File.ReadAllText(IpSetBackup));
        Assert.Equal(ZapretActions.IpSetMode.Any, ZapretActions.GetIpSetMode());
    }

    [Fact]
    public void IpSet_RoundTripThroughNoneAndAny_RestoresTheLoadedList()
    {
        Write(IpSetList, "1.2.3.0/24\r\n5.6.7.8");

        ZapretActions.SetIpSetMode(ZapretActions.IpSetMode.None);
        ZapretActions.SetIpSetMode(ZapretActions.IpSetMode.Loaded);

        Assert.Equal("1.2.3.0/24\r\n5.6.7.8", File.ReadAllText(IpSetList));
        Assert.False(File.Exists(IpSetBackup));
        Assert.Equal(ZapretActions.IpSetMode.Loaded, ZapretActions.GetIpSetMode());
    }

    [Fact]
    public void IpSet_LoadedWithoutABackup_DoesNothing()
    {
        Write(IpSetList, "");

        ZapretActions.SetIpSetMode(ZapretActions.IpSetMode.Loaded);

        Assert.Equal("", File.ReadAllText(IpSetList));
        Assert.Equal(ZapretActions.IpSetMode.Any, ZapretActions.GetIpSetMode());
    }

    [Fact]
    public void IpSet_AnyToNone_DoesNotCreateABackup()
    {
        Write(IpSetList, "");

        ZapretActions.SetIpSetMode(ZapretActions.IpSetMode.None);

        Assert.Equal(NoneSentinel, File.ReadAllText(IpSetList));
        Assert.False(File.Exists(IpSetBackup));
    }

    [Fact]
    public void IpSet_SettingTheCurrentMode_LeavesFilesUntouched()
    {
        Write(IpSetList, "1.2.3.0/24");
        Write(IpSetBackup, "older backup");

        ZapretActions.SetIpSetMode(ZapretActions.IpSetMode.Loaded);

        Assert.Equal("1.2.3.0/24", File.ReadAllText(IpSetList));
        Assert.Equal("older backup", File.ReadAllText(IpSetBackup));
    }

    [Fact]
    public void IpSet_LoadedToNone_ReplacesAnOlderBackup()
    {
        Write(IpSetList, "1.2.3.0/24");
        Write(IpSetBackup, "older backup");

        ZapretActions.SetIpSetMode(ZapretActions.IpSetMode.None);

        Assert.Equal("1.2.3.0/24", File.ReadAllText(IpSetBackup));
    }

    // ---- update-check flag ----------------------------------------------------------------------

    [Fact]
    public void AutoUpdateCheck_DefaultsToDisabled()
    {
        Assert.False(ZapretActions.IsAutoUpdateCheckEnabled());
    }

    [Fact]
    public void AutoUpdateCheck_EnableWritesTheFlag_DisableDeletesIt_BothAreIdempotent()
    {
        ZapretActions.SetAutoUpdateCheck(true);
        ZapretActions.SetAutoUpdateCheck(true);
        Assert.True(ZapretActions.IsAutoUpdateCheckEnabled());
        Assert.Equal("ENABLED", File.ReadAllText(UpdateFlag));

        ZapretActions.SetAutoUpdateCheck(false);
        ZapretActions.SetAutoUpdateCheck(false);
        Assert.False(ZapretActions.IsAutoUpdateCheckEnabled());
        Assert.False(File.Exists(UpdateFlag));
    }
}
