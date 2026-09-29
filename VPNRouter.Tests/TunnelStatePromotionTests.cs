#nullable enable

using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

public sealed class TunnelStatePromotionTests
{
    private static VpnStateSnapshot Record(VpnConnectionState state) =>
        new(state, null, OwnerPid: 1, DateTimeOffset.UnixEpoch);

    [Fact]
    public void Promote_CardOffButServiceConnectedLiveAndTransportActive_ShowsConnected()
    {
        Assert.True(TunnelStateResync.TryPromoteOnResume(
            intendedConnected: false, Record(VpnConnectionState.Connected), serviceTunnelLive: true, vpnTransportActive: true));
    }

    [Theory]
    [InlineData(VpnConnectionState.Disconnected)]
    [InlineData(VpnConnectionState.Connecting)]
    [InlineData(VpnConnectionState.Error)]
    public void Promote_NeverFromAStateOtherThanConnected(VpnConnectionState state)
    {
        Assert.False(TunnelStateResync.TryPromoteOnResume(false, Record(state), true, true));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public void Promote_NeedsTheLiveFlagAndAnActiveVpnTransport(bool live, bool transport)
    {
        Assert.False(TunnelStateResync.TryPromoteOnResume(false, Record(VpnConnectionState.Connected), live, transport));
    }

    [Fact]
    public void Promote_CardAlreadyOn_IsNotAPromotion()
    {
        Assert.False(TunnelStateResync.TryPromoteOnResume(true, Record(VpnConnectionState.Connected), true, true));
    }

    [Fact]
    public void Promote_AStaleRecordFromADeadProcessCannotPromote()
    {
        var stale = VpnStateResolver.Resolve(
            new VpnStateSnapshot(VpnConnectionState.Connected, null, OwnerPid: 111, DateTimeOffset.UnixEpoch),
            currentPid: 222, DateTimeOffset.UnixEpoch);

        Assert.False(TunnelStateResync.TryPromoteOnResume(false, stale, true, true));
    }

    [Fact]
    public void TileIntentContract_KeepsTheStringsTheTileAndTheActivityShare()
    {
        Assert.Equal("com.ninitux.vpnrouter.OPEN_FROM_TILE", TileIntentContract.ActionOpenFromTile);
        Assert.Equal("tile_reason", TileIntentContract.ExtraReason);
        Assert.Equal(
            new[] { "permission", "setup", "connect" },
            new[] { TileIntentContract.ReasonPermission, TileIntentContract.ReasonSetup, TileIntentContract.ReasonConnect });
    }
}
