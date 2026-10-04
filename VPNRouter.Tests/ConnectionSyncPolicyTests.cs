using VPNRouter.App.ViewModels;
using Xunit;

namespace VPNRouter.Tests;

public sealed class ConnectionSyncPolicyTests
{
    private static RuntimeSyncAction Sync(
        bool vpnRunning = false, bool uiConnected = false, bool inTransition = false, bool failedStart = false,
        bool engineRunning = false, double secondsSinceConnect = 600, bool connectedBefore = true) =>
        ConnectionSyncPolicy.DecideRuntimeSync(
            vpnRunning, uiConnected, inTransition, failedStart, engineRunning, secondsSinceConnect, connectedBefore);

    [Fact]
    public void RoutingChangeRestart_TunnelGap_DoesNotFlipTheWindowToNotConnected()
    {
        Assert.Equal(RuntimeSyncAction.None, Sync(vpnRunning: false, uiConnected: true, inTransition: true));
    }

    [Fact]
    public void AfterTheRestart_RunningEngineBehindANotConnectedWindow_IsAdopted()
    {
        Assert.Equal(RuntimeSyncAction.AdoptRunningEngine, Sync(vpnRunning: true, uiConnected: false, engineRunning: true));
    }

    [Fact]
    public void Poll_DuringAnyTransition_ChangesNothing()
    {
        Assert.Equal(RuntimeSyncAction.None, Sync(vpnRunning: true, uiConnected: false, inTransition: true, engineRunning: true));
        Assert.Equal(RuntimeSyncAction.None, Sync(vpnRunning: false, uiConnected: true, inTransition: true));
    }

    [Fact]
    public void ServiceOwnedTunnel_IsShownAsConnectedViaService()
    {
        Assert.Equal(RuntimeSyncAction.MarkConnectedViaService, Sync(vpnRunning: true, uiConnected: false, engineRunning: false));
        Assert.Equal(RuntimeSyncAction.MarkConnectedViaService, Sync(vpnRunning: true, uiConnected: true, failedStart: true, engineRunning: false));
    }

    [Fact]
    public void ConnectedWindow_WithRunningEngine_IsLeftAloneUnlessItShowsAFailedStart()
    {
        Assert.Equal(RuntimeSyncAction.None, Sync(vpnRunning: true, uiConnected: true, engineRunning: true));
        Assert.Equal(RuntimeSyncAction.RestoreConnectedStatus, Sync(vpnRunning: true, uiConnected: true, failedStart: true, engineRunning: true));
    }

    [Fact]
    public void TunnelGone_AfterTheGracePeriod_MarksDisconnected()
    {
        Assert.Equal(RuntimeSyncAction.MarkDisconnected, Sync(vpnRunning: false, uiConnected: true, secondsSinceConnect: 60));
    }

    [Fact]
    public void TunnelGone_WithinTheGracePeriodOrWithAnEngineStillUp_IsIgnored()
    {
        Assert.Equal(RuntimeSyncAction.None, Sync(vpnRunning: false, uiConnected: true, secondsSinceConnect: 3));
        Assert.Equal(RuntimeSyncAction.None, Sync(vpnRunning: false, uiConnected: true, engineRunning: true));
        Assert.Equal(RuntimeSyncAction.None, Sync(vpnRunning: false, uiConnected: false));
    }

    [Fact]
    public void Connect_PressedWhileTheEngineRuns_ShowsConnectedInsteadOfStoppingIt()
    {
        Assert.Equal(ToggleAction.AdoptRunningEngine, ConnectionSyncPolicy.DecideToggle(uiConnected: false, inTransition: false, inProcessEngineRunning: true));
    }

    [Theory]
    [InlineData(true, false, false, ToggleAction.Stop)]
    [InlineData(true, false, true, ToggleAction.Stop)]
    [InlineData(false, false, false, ToggleAction.Start)]
    [InlineData(false, true, true, ToggleAction.Ignore)]
    [InlineData(true, true, true, ToggleAction.Ignore)]
    public void Toggle_FollowsTheWindowStateAndIgnoresPressesDuringATransition(
        bool uiConnected, bool inTransition, bool engineRunning, ToggleAction expected)
    {
        Assert.Equal(expected, ConnectionSyncPolicy.DecideToggle(uiConnected, inTransition, engineRunning));
    }
}
