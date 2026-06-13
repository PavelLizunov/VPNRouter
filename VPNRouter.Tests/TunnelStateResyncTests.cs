#nullable enable
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// Pins the Android resume re-sync decision (demote-only). Guards against a
/// future change that would make the resume re-sync promote Off → On from the
/// persisted <c>tunnel_live</c> flag — which would reintroduce a falsely-
/// "Connected" card on a fresh process whose flag is a stale true after an
/// unclean kill. See
/// <c>plans/android-status-card-stale-lifecycle-investigation-2026-06-13.md</c>.
/// </summary>
public class TunnelStateResyncTests
{
    [Fact]
    public void FalselyConnected_CorrectsDown()
    {
        // Card shows Connected, but the service persisted live=down
        // (a lost TUNNEL_DOWN). Must correct down.
        var act = TunnelStateResync.TryResolveOnResume(
            intendedConnected: true, serviceTunnelLive: false, out var corrected);
        Assert.True(act);
        Assert.False(corrected);
    }

    [Fact]
    public void Connected_And_Live_IsNoOp()
    {
        var act = TunnelStateResync.TryResolveOnResume(
            intendedConnected: true, serviceTunnelLive: true, out _);
        Assert.False(act);
    }

    [Fact]
    public void Disconnected_And_StaleLive_DoesNotPromote()
    {
        // The dangerous case: a stale tunnel_live=true (process killed without
        // a clean teardown) must NOT flip a fresh Off card to Connected.
        var act = TunnelStateResync.TryResolveOnResume(
            intendedConnected: false, serviceTunnelLive: true, out var corrected);
        Assert.False(act);
        Assert.False(corrected);
    }

    [Fact]
    public void Disconnected_And_Down_IsNoOp()
    {
        var act = TunnelStateResync.TryResolveOnResume(
            intendedConnected: false, serviceTunnelLive: false, out _);
        Assert.False(act);
    }
}
