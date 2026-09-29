using System.IO;
using System.Text.Json;
using Serilog;
using VPNRouter.Core.Interfaces;
using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

public partial class VpnEngine
{
    internal async Task TryEngageSplitDriverAsync(
        AppSettings settings,
        CancellationToken ct)
    {
        if (_splitDriver is null)
        {
            SetTrueSplitState(TrueSplitState.NotApplicable, "No split-tunnel driver on this platform.");
            return;
        }

        var app = settings.App;
        bool hasExcluded = app.RoutingAppsExclude is { Count: > 0 };
        if (!SplitTunnelPolicy.ShouldEngage(OperatingSystem.IsWindows(), app.RoutingMode, app.RoutingAppsMode, hasExcluded, "auto"))
        {
            if (_splitDriver.IsEngaged) await _splitDriver.DisengageAsync(ct).ConfigureAwait(false);
            SetTrueSplitState(TrueSplitState.NotApplicable, "True split applies only to Windows split/exclude mode with excluded apps.");
            return;
        }

        if (!_splitDriver.IsAvailable)
        {
            SetTrueSplitState(TrueSplitState.DriverMissing, "True-split driver is not bundled in this build.");
            return;
        }

        SetTrueSplitState(TrueSplitState.Starting, "Starting true split...");
        var dosPaths = new List<string>();
        foreach (var name in app.RoutingAppsExclude)
        {
            var p = ProcessImagePath.ResolveRunningPath(name) ?? ProcessImagePath.ResolveNameToPath(name);
            if (!string.IsNullOrEmpty(p)) dosPaths.Add(p!);
            else _logger?.Information("[VpnEngine] Split-tunnel: '{Name}' not running/unresolved — post-capture rule covers it", name);
        }

        if (dosPaths.Count == 0)
        {
            if (_splitDriver.IsEngaged) await _splitDriver.DisengageAsync(ct).ConfigureAwait(false);
            _logger?.Information("[VpnEngine] True-split driver: 0 excluded path(s) resolved — not engaging (post-capture covers them)");
            SetTrueSplitState(TrueSplitState.Fallback, "True split needs a resolvable app path; ordinary split is active.");
            return;
        }

        var req = new SplitTunnelEngageRequest(
            dosPaths,
            settings.Tun?.Ipv4Address,
            TunnelIpv6: null);
        bool ok = await _splitDriver.EngageAsync(req, ct).ConfigureAwait(false);
        _logger?.Information("[VpnEngine] True-split driver engage={Ok} ({N} excluded path(s) resolved)", ok, dosPaths.Count);
        var failReason = _splitDriver.LastFailureReason;
        SetTrueSplitState(
            ok ? TrueSplitState.Active : TrueSplitState.Fallback,
            ok ? "True split active." : failReason ?? "True split did not start; ordinary split is active.");
    }

    public Task RestartTrueSplitAsync(AppSettings settings, CancellationToken ct = default) =>
        TryEngageSplitDriverAsync(settings, ct);

    private void SetTrueSplitState(TrueSplitState state, string reason)
    {
        CurrentTrueSplitState = state;
        TrueSplitStateChanged?.Invoke(state, reason);
    }
}
