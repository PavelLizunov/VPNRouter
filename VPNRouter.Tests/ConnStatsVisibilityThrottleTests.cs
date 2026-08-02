using System;
using System.IO;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// Source-shape pin for the ConnStats visibility/minimized throttle
/// (OPEN-DEFECTS.md:108): the hidden/minimized guard must sit ahead of the
/// in-flight compare/exchange, which must sit ahead of the API dispatch.
/// </summary>
public class ConnStatsVisibilityThrottleTests
{
    [Fact]
    public void MaybePollConnStats_GuardsVisibilityBeforeInFlightAndApiCall()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../VPNRouter.App/ViewModels/MainWindowViewModel.ConnStats.cs"));
        var src = File.ReadAllText(path);

        var start = src.IndexOf("private void MaybePollConnStats()", StringComparison.Ordinal);
        var end = src.IndexOf("private async Task PollConnStatsAsync()", StringComparison.Ordinal);
        var method = src.Substring(start, end - start);

        var idxWindow = method.IndexOf("GetMainWindow()", StringComparison.Ordinal);
        var idxNull = method.IndexOf("window is null", StringComparison.Ordinal);
        var idxVisible = method.IndexOf("!window.IsVisible", StringComparison.Ordinal);
        var idxMin = method.IndexOf("WindowState.Minimized", StringComparison.Ordinal);
        var idxInFlight = method.IndexOf("Interlocked.CompareExchange(ref _statsInFlight", StringComparison.Ordinal);
        var idxApi = method.IndexOf("PollConnStatsAsync()", StringComparison.Ordinal);

        Assert.True(
            start >= 0 && idxWindow >= 0 && idxNull > idxWindow && idxVisible > idxWindow
            && idxMin > idxWindow && idxWindow < idxInFlight && idxInFlight < idxApi,
            "ConnStats throttle ordering broken: expected GetMainWindow() + null/IsVisible/WindowState.Minimized "
            + "guard BEFORE the in-flight CompareExchange, BEFORE the PollConnStatsAsync dispatch "
            + $"(start={start}, window={idxWindow}, null={idxNull}, visible={idxVisible}, min={idxMin}, inFlight={idxInFlight}, api={idxApi}).");
    }
}
