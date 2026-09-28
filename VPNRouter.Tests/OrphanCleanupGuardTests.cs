using System.IO;
using System.Linq;

namespace VPNRouter.Tests;

public sealed class OrphanCleanupGuardTests
{
    [Fact]
    public void OrphanCleanup_DoesNotKillVPNRouterAppProcesses()
    {
        var src = LoadOrphanCleanupSource();
        if (src == null) return;

        var stripped = string.Join("\n",
            src.Split('\n').Select(l =>
                l.Contains("//") ? l[..l.IndexOf("//")] : l));

        Assert.DoesNotContain(
            "KillByName(\"VPNRouter.App\"",
            stripped);

        Assert.Contains("KillByName(\"sing-box\"", stripped);
        Assert.Contains("KillByName(\"VPNRouter.GUI\"", stripped);

        Assert.Contains("KillByName(\"slipstream-client\"", stripped);
    }

    private static string? LoadOrphanCleanupSource()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(
                dir.FullName, "VPNRouter.Core", "Services", "OrphanCleanup.cs");
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
        }
        return null;
    }
}
