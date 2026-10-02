namespace VPNRouter.App.ViewModels;

internal enum RuntimeSyncAction
{
    None,
    AdoptRunningEngine,
    RestoreConnectedStatus,
    MarkConnectedViaService,
    MarkDisconnected,
}

internal enum ToggleAction
{
    Ignore,
    Stop,
    Start,
    AdoptRunningEngine,
}

// Decides what the window shows when the 2-second runtime poll and the button learn what the engine is doing.
// The window used to flip to "Not connected" while sing-box was being restarted for a routing change and never flip back,
// so the next press of "Connect" found a running engine and stopped it.
internal static class ConnectionSyncPolicy
{
    internal const double ConnectGraceSeconds = 8;

    internal static RuntimeSyncAction DecideRuntimeSync(
        bool vpnRunning,
        bool uiConnected,
        bool inTransition,
        bool failedStartStatus,
        bool inProcessEngineRunning,
        double secondsSinceConnect,
        bool hasConnectedBefore)
    {
        if (inTransition) return RuntimeSyncAction.None;

        if (vpnRunning)
        {
            if (inProcessEngineRunning)
            {
                if (!uiConnected) return RuntimeSyncAction.AdoptRunningEngine;
                return failedStartStatus ? RuntimeSyncAction.RestoreConnectedStatus : RuntimeSyncAction.None;
            }

            return !uiConnected || failedStartStatus ? RuntimeSyncAction.MarkConnectedViaService : RuntimeSyncAction.None;
        }

        if (!uiConnected) return RuntimeSyncAction.None;
        if (hasConnectedBefore && secondsSinceConnect < ConnectGraceSeconds) return RuntimeSyncAction.None;
        if (inProcessEngineRunning) return RuntimeSyncAction.None;
        return RuntimeSyncAction.MarkDisconnected;
    }

    internal static ToggleAction DecideToggle(bool uiConnected, bool inTransition, bool inProcessEngineRunning)
    {
        if (inTransition) return ToggleAction.Ignore;
        if (uiConnected) return ToggleAction.Stop;
        return inProcessEngineRunning ? ToggleAction.AdoptRunningEngine : ToggleAction.Start;
    }
}
