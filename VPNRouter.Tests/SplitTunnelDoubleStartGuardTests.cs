#nullable enable

using System.IO;
using System.Linq;

namespace VPNRouter.Tests;

/// <summary>
/// Regression pins for the split-tunnel double-start crash:
/// ReconnectAsync + ToggleConnectionAsync must not race a second sing-box
/// launch into a live/starting TUN.
/// </summary>
public sealed class SplitTunnelDoubleStartGuardTests
{
    [Fact]
    public void VpnEngine_StartAsync_GuardsLiveOrStartingBeforeNewSession()
    {
        var src = LoadSource("VPNRouter.Core", "Services", "VpnEngine.cs");
        if (src == null) return;

        var start = src.IndexOf("public async Task StartAsync(AppSettings settings", System.StringComparison.Ordinal);
        Assert.True(start >= 0, "StartAsync method not found");

        var end = src.IndexOf("internal async Task StartAsyncInternal", start, System.StringComparison.Ordinal);
        Assert.True(end > start, "StartAsyncInternal boundary not found");

        var method = src.Substring(start, end - start);
        Assert.Contains("HasLiveOrStartingSingBox()", method);
        Assert.True(
            method.IndexOf("HasLiveOrStartingSingBox()", System.StringComparison.Ordinal)
            < method.IndexOf("_sessionCts?.Dispose()", System.StringComparison.Ordinal),
            "StartAsync must no-op live/starting duplicate starts before it creates a fresh session token.");

        Assert.Contains("SingBoxState.Starting", src);
        Assert.Contains("SingBoxState.Restarting", src);
    }

    [Fact]
    public void MainWindowViewModel_ToggleConnection_GuardsInFlightBeforeStartStop()
    {
        var src = LoadSource("VPNRouter.App", "ViewModels", "MainWindowViewModel.Connection.cs");
        if (src == null) return;

        var start = src.IndexOf("private async Task ToggleConnectionAsync()", System.StringComparison.Ordinal);
        Assert.True(start >= 0, "ToggleConnectionAsync method not found");

        var method = src[start..];
        var guardIdx = method.IndexOf("if (IsConnecting || IsApplying || _isReconnecting)", System.StringComparison.Ordinal);
        var branchIdx = method.IndexOf("if (IsConnected || _engine.IsRunning)", System.StringComparison.Ordinal);

        Assert.True(guardIdx >= 0, "ToggleConnectionAsync must guard in-flight connect/apply/reconnect.");
        Assert.True(branchIdx >= 0, "ToggleConnectionAsync start/stop branch not found.");
        Assert.True(guardIdx < branchIdx, "In-flight guard must run before start/stop branching.");
    }

    [Fact]
    public void MainWindowViewModel_RestartTrueSplit_StopsVpnRouterServiceBeforeRetry()
    {
        var src = LoadSource("VPNRouter.App", "ViewModels", "MainWindowViewModel.Connection.cs");
        if (src == null) return;

        var start = src.IndexOf("private async Task RestartTrueSplitAsync()", System.StringComparison.Ordinal);
        Assert.True(start >= 0, "RestartTrueSplitAsync method not found");

        var end = src.IndexOf("private async Task ToggleConnectionAsync()", start, System.StringComparison.Ordinal);
        Assert.True(end > start, "RestartTrueSplitAsync boundary not found");

        var method = src.Substring(start, end - start);
        var stopIdx = method.IndexOf("WindowsServiceHelper.Stop()", System.StringComparison.Ordinal);
        var retryIdx = method.IndexOf("_engine.RestartTrueSplitAsync", System.StringComparison.Ordinal);

        Assert.Contains("WindowsServiceHelper.IsRunning()", method);
        Assert.True(stopIdx >= 0, "True Split retry must stop VPNRouter Service when it is running.");
        Assert.True(retryIdx >= 0, "True Split retry must still re-engage the engine.");
        Assert.True(stopIdx < retryIdx, "VPNRouter Service must be stopped before true-split re-engage.");
    }

    [Fact]
    public void SplitTunnelManager_RuntimePath_DoesNotDeleteKernelService()
    {
        var manager = LoadSource("VPNRouter.Core", "Services", "SplitTunnelDriverManager.cs");
        var interop = LoadSource("VPNRouter.Core", "Services", "SplitTunnelDriverInterop.cs");
        if (manager == null || interop == null) return;

        Assert.DoesNotContain("DeleteService", manager);
        Assert.DoesNotContain("ControlService", manager);
        Assert.DoesNotContain("DeleteService", interop);
        Assert.Contains("Foreign split-tunnel kernel driver is running before our start path", manager);
    }

    [Fact]
    public void DiagnosticsExporter_CapturesAmneziaSplitDriverState()
    {
        var src = LoadSource("VPNRouter.Core", "Services", "Diagnostics", "DiagnosticsExporter.cs");
        if (src == null) return;

        Assert.Contains("AmneziaVPNSplitTunnel", src);
        Assert.Contains("recent System driver/service events", src);
        Assert.Contains("0x80320009", src);
        Assert.Contains("General failure", src);
    }

    [Fact]
    public void MainWindowViewModel_TrueSplitFallback_RecognizesWfpAlreadyExists()
    {
        var src = LoadSource("VPNRouter.App", "ViewModels", "MainWindowViewModel.Connection.cs");
        if (src == null) return;

        Assert.Contains("0x80320009", src);
        Assert.Contains("FormatTrueSplitFallback", src);
    }

    [Fact]
    public void MainWindowViewModel_Reconnect_UsesApplyBeforeStartFallback()
    {
        var src = LoadSource("VPNRouter.App", "ViewModels", "MainWindowViewModel.cs");
        if (src == null) return;

        var start = src.IndexOf("private async Task ReconnectAsync", System.StringComparison.Ordinal);
        Assert.True(start >= 0, "ReconnectAsync method not found");

        var end = src.IndexOf("// Phase 2B", start, System.StringComparison.Ordinal);
        Assert.True(end > start, "ReconnectAsync boundary not found");

        var method = src.Substring(start, end - start);
        var applyFlagIdx = method.IndexOf("var applyInPlace = _engine.IsRunning", System.StringComparison.Ordinal);
        var applyIdx = method.IndexOf("_engine.ApplyAsync(", System.StringComparison.Ordinal);
        var startIdx = method.IndexOf("_engine.StartAsync(", System.StringComparison.Ordinal);

        Assert.True(applyFlagIdx >= 0, "ReconnectAsync must detect live local engine ownership.");
        Assert.True(applyIdx >= 0, "ReconnectAsync must ApplyAsync when the engine is already running.");
        Assert.True(startIdx >= 0, "ReconnectAsync must retain StartAsync fallback for stopped/non-owned cases.");
        Assert.True(applyIdx < startIdx, "ApplyAsync must happen before the Stop+Start fallback.");
    }

    private static string? LoadSource(params string[] relativeParts)
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        for (var i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(new[] { dir.FullName }.Concat(relativeParts).ToArray());
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
        }

        return null;
    }
}
