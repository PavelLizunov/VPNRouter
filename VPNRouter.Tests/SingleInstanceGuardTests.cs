using System.IO;
using System.Linq;

namespace VPNRouter.Tests;

public sealed class SingleInstanceGuardTests
{
    [Fact]
    public void TryAcquireOrSignal_AlwaysCallsWaitOne_OnFreshMutex()
    {
        var src = LoadSingleInstanceSource();
        if (src == null) return;

        var stripped = string.Join("\n",
            src.Split('\n').Select(l =>
                l.Contains("//") ? l[..l.IndexOf("//")] : l));

        Assert.Contains("_mutex.WaitOne(0)", stripped);

        Assert.DoesNotContain(
            "!createdNew && !_mutex.WaitOne",
            stripped);
        Assert.DoesNotContain(
            "!createdNew && !_mutex?.WaitOne",
            stripped);

        Assert.Contains("AbandonedMutexException", stripped);
    }

    [Fact]
    public void TryAcquireOrSignal_StillSignalsExistingInstanceOnContention()
    {
        var src = LoadSingleInstanceSource();
        if (src == null) return;

        var stripped = string.Join("\n",
            src.Split('\n').Select(l =>
                l.Contains("//") ? l[..l.IndexOf("//")] : l));

        Assert.Contains("TrySignalShow(logger);", stripped);

        Assert.Contains("_mutex.Dispose();", stripped);
    }

    private static string? LoadSingleInstanceSource()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(
                dir.FullName, "VPNRouter.App", "Services", "SingleInstance.cs");
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
        }
        return null;
    }
}
