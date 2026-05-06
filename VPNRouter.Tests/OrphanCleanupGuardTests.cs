using System.IO;
using System.Linq;

namespace VPNRouter.Tests;

/// <summary>
/// v2.31.9 night-shift F-4 regression pin (2026-05-06).
///
/// <para>OrphanCleanup.KillOrphans() was killing OTHER VPNRouter.App.exe
/// processes by name (line `KillByName("VPNRouter.App", selfPid)`).
/// This is a pre-v2.31.7-r2 design artifact: before SingleInstance,
/// every new launch had to nuke leftovers itself. With SingleInstance
/// in place (Mutex + named-pipe IPC, v2.31.7-r2), this kill is at best
/// redundant and at worst a footgun: if the SingleInstance check has
/// any race (e.g. mutex briefly released during config reload, or a
/// cross-session token issue), the OrphanCleanup will gleefully kill
/// the original instance and leave the brand-new one as sole survivor
/// — the OPPOSITE of what SingleInstance is supposed to guarantee.</para>
///
/// <para>Live evidence (this VM, 2026-05-06 01:34): pid 9060 was the
/// running App; <c>Start-Process VPNRouter.App.exe</c> resulted in pid
/// 7996 surviving and 9060 killed. Trace pinned to
/// <c>OrphanCleanup.KillByName("VPNRouter.App", selfPid)</c>.</para>
///
/// <para>This test is a SOURCE-STRING PIN that fails loudly if anyone
/// adds back a VPNRouter.App kill in OrphanCleanup. The test is
/// intentionally string-based because OrphanCleanup is a procedural
/// static helper that would require process-mocking infrastructure to
/// behaviour-test.</para>
/// </summary>
public sealed class OrphanCleanupGuardTests
{
    [Fact]
    public void OrphanCleanup_DoesNotKillVPNRouterAppProcesses()
    {
        var src = LoadOrphanCleanupSource();
        if (src == null) return; // Source not available — partial CI checkout

        // Strip C# // comments + intentional namespace-name occurrences
        // from the explanatory text. We're checking that no actual code
        // calls KillByName("VPNRouter.App", ...).
        var stripped = string.Join("\n",
            src.Split('\n').Select(l =>
                l.Contains("//") ? l[..l.IndexOf("//")] : l));

        // The dangerous pattern: a literal call killing VPNRouter.App by
        // name. Anything matching this brings back F-4.
        Assert.DoesNotContain(
            "KillByName(\"VPNRouter.App\"",
            stripped);

        // The safe (intended) kills: sing-box and the GUI stub. Pin them
        // so a future refactor doesn't accidentally remove them too.
        Assert.Contains("KillByName(\"sing-box\"", stripped);
        Assert.Contains("KillByName(\"VPNRouter.GUI\"", stripped);
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
