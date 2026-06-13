#nullable enable
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// Pins the Android resume re-sync decision (demote-only, dual-signal). Guards
/// against (a) a future change that would make the resume re-sync promote
/// Off → On from a stale flag, and (b) regressing the silent-tun-death coverage
/// (no <c>vpnTransportActive</c> → demote even when the persisted flag is
/// stale-true). See
/// <c>plans/android-status-card-stale-lifecycle-investigation-2026-06-13.md</c>.
/// </summary>
public class TunnelStateResyncTests
{
    [Theory]
    // intended, tunnel_live, vpnActive  => expect (act, corrected)
    // --- demote cases (card falsely Connected) ---
    [InlineData(true,  false, false, true,  false)] // explicit stop: flag down + no vpn
    [InlineData(true,  false, true,  true,  false)] // flag down (lost TUNNEL_DOWN) even if a vpn lingers
    [InlineData(true,  true,  false, true,  false)] // SILENT TUN DEATH: flag stale-true but no active vpn
    // --- no-op cases ---
    [InlineData(true,  true,  true,  false, true)]  // genuinely connected
    [InlineData(false, true,  true,  false, false)] // Off + live: do NOT promote
    [InlineData(false, true,  false, false, false)] // Off + stale flag: do NOT promote
    [InlineData(false, false, false, false, false)] // Off + down: in sync
    public void Resolve(bool intended, bool live, bool vpnActive, bool expectAct, bool expectCorrected)
    {
        var act = TunnelStateResync.TryResolveOnResume(intended, live, vpnActive, out var corrected);
        Assert.Equal(expectAct, act);
        Assert.Equal(expectCorrected, corrected);
    }

    [Fact]
    public void SilentTunDeath_IsTheRegressionGuard()
    {
        // The A101BM case: tunnel torn down by the OEM without onRevoke, so the
        // service never set tunnel_live=false. The vpn-transport ground truth
        // must still demote the stale "Connected" card.
        var act = TunnelStateResync.TryResolveOnResume(
            intendedConnected: true, serviceTunnelLive: true, vpnTransportActive: false, out var corrected);
        Assert.True(act);
        Assert.False(corrected);
    }
}
