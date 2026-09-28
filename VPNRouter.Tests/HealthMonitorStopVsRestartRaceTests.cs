#nullable enable

using System;
using System.IO;
using System.Linq;
using Xunit;

namespace VPNRouter.Tests;

public sealed class HealthMonitorStopVsRestartRaceTests
{
    [Fact]
    public void Source_AttemptRestart_RechecksStopGateBeforeRevival()
    {
        var src = ReadHealthMonitorSource();

        Assert.Contains("volatile bool _isStopping", src);

        Assert.Contains("aborting sing-box revival", src);

        var markerIdx = src.IndexOf("aborting sing-box revival", StringComparison.Ordinal);
        var window = src.Substring(Math.Max(0, markerIdx - 200), Math.Min(220, src.Length - Math.Max(0, markerIdx - 200)));
        Assert.Contains("_isStopping", window);
    }

    private static string ReadHealthMonitorSource()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "VPNRouter.Core", "Services", "HealthMonitor.cs");
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            dir = dir.Parent;
        }
        throw new FileNotFoundException("HealthMonitor.cs not reachable from test base dir");
    }
}
