#nullable enable

using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

public sealed class VpnTileLogicTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);
    private const int Pid = 4242;

    private static VpnStateSnapshot Stored(VpnConnectionState state, int pid = Pid, TimeSpan? age = null, string? reason = null) =>
        new(state, reason, pid, Now - (age ?? TimeSpan.Zero));

    // ---- resolver -------------------------------------------------------------------------------

    [Fact]
    public void Resolve_NothingStored_IsDisconnected()
    {
        var r = VpnStateResolver.Resolve(null, Pid, Now);

        Assert.Equal(VpnConnectionState.Disconnected, r.State);
        Assert.Null(r.Reason);
    }

    [Theory]
    [InlineData(VpnConnectionState.Connected)]
    [InlineData(VpnConnectionState.Disconnected)]
    [InlineData(VpnConnectionState.Error)]
    public void Resolve_StateOfTheSameProcess_IsKept(VpnConnectionState state)
    {
        var r = VpnStateResolver.Resolve(Stored(state, reason: "x"), Pid, Now);

        Assert.Equal(state, r.State);
        Assert.Equal("x", r.Reason);
    }

    [Theory]
    [InlineData(VpnConnectionState.Connected)]
    [InlineData(VpnConnectionState.Connecting)]
    public void Resolve_LiveStateFromADeadProcess_MeansDisconnected(VpnConnectionState state)
    {
        var r = VpnStateResolver.Resolve(Stored(state, pid: 111), Pid, Now);

        Assert.Equal(VpnConnectionState.Disconnected, r.State);
        Assert.Equal(VpnStateResolver.ReasonInterrupted, r.Reason);
    }

    [Theory]
    [InlineData(VpnConnectionState.Error)]
    [InlineData(VpnConnectionState.Disconnected)]
    public void Resolve_ErrorOrOffFromAnotherProcess_IsKept(VpnConnectionState state)
    {
        Assert.Equal(state, VpnStateResolver.Resolve(Stored(state, pid: 111), Pid, Now).State);
    }

    [Fact]
    public void Resolve_ConnectingFreshOrExactlyAtTheLimit_StaysConnecting()
    {
        Assert.Equal(VpnConnectionState.Connecting,
            VpnStateResolver.Resolve(Stored(VpnConnectionState.Connecting, age: TimeSpan.FromSeconds(5)), Pid, Now).State);
        Assert.Equal(VpnConnectionState.Connecting,
            VpnStateResolver.Resolve(Stored(VpnConnectionState.Connecting, age: VpnStateResolver.ConnectingTimeout), Pid, Now).State);
    }

    [Fact]
    public void Resolve_ConnectingLongerThanTheLimit_BecomesATimeoutError()
    {
        var r = VpnStateResolver.Resolve(
            Stored(VpnConnectionState.Connecting, age: VpnStateResolver.ConnectingTimeout + TimeSpan.FromSeconds(1)), Pid, Now);

        Assert.Equal(VpnConnectionState.Error, r.State);
        Assert.Equal(VpnStateResolver.ReasonConnectTimeout, r.Reason);
    }

    [Fact]
    public void Resolve_ConnectingStampedFarInTheFuture_BecomesATimeoutError()
    {
        var r = VpnStateResolver.Resolve(
            Stored(VpnConnectionState.Connecting, age: -TimeSpan.FromMinutes(10)), Pid, Now);

        Assert.Equal(VpnConnectionState.Error, r.State);
    }

    [Fact]
    public void Resolve_ConnectedNeverTimesOut()
    {
        Assert.Equal(VpnConnectionState.Connected,
            VpnStateResolver.Resolve(Stored(VpnConnectionState.Connected, age: TimeSpan.FromDays(3)), Pid, Now).State);
    }

    // ---- codec ----------------------------------------------------------------------------------

    [Theory]
    [InlineData(VpnConnectionState.Disconnected, "disconnected")]
    [InlineData(VpnConnectionState.Connecting, "connecting")]
    [InlineData(VpnConnectionState.Connected, "connected")]
    [InlineData(VpnConnectionState.Error, "error")]
    public void Codec_RoundTripsEveryState(VpnConnectionState state, string word)
    {
        Assert.Equal(word, VpnStateCodec.Encode(state));

        var parsed = VpnStateCodec.TryParse(word, "why", Pid, Now.ToUnixTimeMilliseconds());

        Assert.NotNull(parsed);
        Assert.Equal(state, parsed!.State);
        Assert.Equal("why", parsed.Reason);
        Assert.Equal(Pid, parsed.OwnerPid);
        Assert.Equal(Now, parsed.UpdatedAt);
    }

    [Theory]
    [InlineData(null, 5L)]
    [InlineData("", 5L)]
    [InlineData("on", 5L)]
    [InlineData("connected", 0L)]
    [InlineData("connected", -1L)]
    public void Codec_UnknownStateOrMissingTimestamp_IsNull(string? word, long at)
    {
        Assert.Null(VpnStateCodec.TryParse(word, null, Pid, at));
    }

    [Fact]
    public void Codec_IsCaseAndWhitespaceTolerant_AndBlankReasonBecomesNull()
    {
        var parsed = VpnStateCodec.TryParse("  Connected ", "  ", Pid, 1000);

        Assert.Equal(VpnConnectionState.Connected, parsed!.State);
        Assert.Null(parsed.Reason);
    }

    // ---- click planner --------------------------------------------------------------------------

    [Theory]
    [InlineData(VpnConnectionState.Connecting, true, true, TileClickAction.Ignore)]
    [InlineData(VpnConnectionState.Connecting, false, false, TileClickAction.Ignore)]
    [InlineData(VpnConnectionState.Connected, true, true, TileClickAction.StopVpn)]
    [InlineData(VpnConnectionState.Connected, false, false, TileClickAction.StopVpn)]
    [InlineData(VpnConnectionState.Disconnected, true, true, TileClickAction.StartVpn)]
    [InlineData(VpnConnectionState.Error, true, true, TileClickAction.StartVpn)]
    [InlineData(VpnConnectionState.Disconnected, false, true, TileClickAction.OpenAppForPermission)]
    [InlineData(VpnConnectionState.Error, false, false, TileClickAction.OpenAppForPermission)]
    [InlineData(VpnConnectionState.Disconnected, true, false, TileClickAction.OpenAppForSetup)]
    [InlineData(VpnConnectionState.Error, true, false, TileClickAction.OpenAppForSetup)]
    public void Planner_DecidesTheTapAction(VpnConnectionState state, bool permission, bool config, TileClickAction expected)
    {
        Assert.Equal(expected, TileClickPlanner.Plan(state, permission, config));
    }

    // ---- appearance -----------------------------------------------------------------------------

    [Theory]
    [InlineData(VpnConnectionState.Disconnected, true, TileVisual.Off, "Выключен")]
    [InlineData(VpnConnectionState.Disconnected, false, TileVisual.Off, "Off")]
    [InlineData(VpnConnectionState.Connecting, true, TileVisual.Busy, "Подключается…")]
    [InlineData(VpnConnectionState.Connecting, false, TileVisual.Busy, "Connecting…")]
    [InlineData(VpnConnectionState.Connected, true, TileVisual.On, "Подключён")]
    [InlineData(VpnConnectionState.Connected, false, TileVisual.On, "Connected")]
    [InlineData(VpnConnectionState.Error, true, TileVisual.Error, "Ошибка")]
    [InlineData(VpnConnectionState.Error, false, TileVisual.Error, "Error")]
    public void Appearance_MapsEachStateToAVisualAndASubtitle(VpnConnectionState state, bool russian, TileVisual visual, string subtitle)
    {
        var appearance = TileAppearanceFactory.For(Stored(state), russian);

        Assert.Equal(visual, appearance.Visual);
        Assert.Equal(subtitle, appearance.Subtitle);
    }

    [Theory]
    [InlineData("foreground-start-blocked", true, "Откройте приложение")]
    [InlineData("foreground-start-blocked", false, "Open the app")]
    [InlineData("connect-timeout", true, "Нет ответа")]
    [InlineData("connect-timeout", false, "Timed out")]
    [InlineData("no-permission", false, "Permission needed")]
    [InlineData("no-config", true, "Нет настройки")]
    [InlineData("no-network", false, "No network")]
    public void ReasonText_KnownReasonsAreShortAndLocalised(string reason, bool russian, string expected)
    {
        Assert.Equal(expected, TileAppearanceFactory.ReasonText(reason, russian));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("IllegalStateException: token=abc123 at libbox")]
    public void ReasonText_UnknownReasonStaysGeneric_SoRawErrorTextNeverReachesTheTile(string? reason)
    {
        Assert.Equal("Error", TileAppearanceFactory.ReasonText(reason, russian: false));
        Assert.Equal("Ошибка", TileAppearanceFactory.ReasonText(reason, russian: true));
    }

    [Fact]
    public void Appearance_ErrorShowsTheMappedReason()
    {
        var resolved = VpnStateResolver.Resolve(
            Stored(VpnConnectionState.Connecting, age: TimeSpan.FromMinutes(5)), Pid, Now);

        Assert.Equal("Timed out", TileAppearanceFactory.For(resolved, russian: false).Subtitle);
    }
}
