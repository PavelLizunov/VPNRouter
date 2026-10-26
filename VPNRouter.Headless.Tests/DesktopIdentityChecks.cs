using System.Text.Json;
using VPNRouter.Headless.Features;
using VPNRouter.Headless.Storage;

namespace VPNRouter.Headless.Tests;

/// <summary>Offline desktop identity regression fixtures; never launches a process or desktop app.</summary>
public static class DesktopIdentityChecks
{
    public static Task RunAsync()
    {
        if (!OperatingSystem.IsLinux()) return Task.CompletedTask;
        var root = Path.Combine(Path.GetTempPath(), "vpnrouter-desktop-identity-" + Guid.NewGuid().ToString("N"));
        var user = Path.Combine(root, "user");
        var system = Path.Combine(root, "system");
        var proc = Path.Combine(root, "proc", "1");
        Directory.CreateDirectory(user);
        Directory.CreateDirectory(system);
        Directory.CreateDirectory(proc);
        try
        {
            File.WriteAllText(Path.Combine(proc, "status"), "Uid:\t1000\t1000\t1000\t1000\n");
            File.WriteAllText(Path.Combine(proc, "cmdline"), "ghostty\0");
            File.CreateSymbolicLink(Path.Combine(proc, "exe"), "/usr/bin/ghostty");
            Write(user, "dsh.desktop", "Name=DSH TUI\nIcon=dsh\nExec=ghostty -e dsh\n");
            Write(system, "com.mitchellh.ghostty.desktop", "Name=Ghostty\nIcon=com.mitchellh.ghostty\nExec=/usr/bin/ghostty\n");
            foreach (var dirs in new[] { new[] { user, system }, new[] { system, user } })
            {
                var app = LinuxRunningApps.Match(new[] { "GhOsTtY" }, LinuxRunningApps.ReadDesktopIndex(dirs)).Single();
                Assert(app == new RunningApp("GhOsTtY", "Ghostty", "com.mitchellh.ghostty",
                    "com.mitchellh.ghostty.desktop", false), "Reverse-DNS identity beats custom Exec in either order");
            }
            var scanner = new LinuxRunningApps(Path.GetDirectoryName(proc)!, 1000, new[] { user, system });
            var list = new AppRoutingFeature(new ConfigStorage(Path.Combine(root, "config")), scanner).List();
            Console.WriteLine("DESKTOP_IDENTITY_SAMPLE apps.list " + JsonSerializer.Serialize(list));
            File.Delete(Path.Combine(system, "com.mitchellh.ghostty.desktop"));
            Write(system, "other.desktop", "Name=Other Terminal Launcher\nIcon=other\nExec=ghostty -e other\n");
            foreach (var dirs in new[] { new[] { user, system }, new[] { system, user } })
            {
                var app = LinuxRunningApps.Match(new[] { "ghostty" }, LinuxRunningApps.ReadDesktopIndex(dirs)).Single();
                Assert(app == new RunningApp("ghostty", "ghostty", null, null, false), "Ambiguous Exec has no metadata");
            }
            var ambiguous = new AppRoutingFeature(new ConfigStorage(Path.Combine(root, "ambiguous")),
                new LinuxRunningApps(Path.GetDirectoryName(proc)!, 1000, new[] { user, system })).List();
            Console.WriteLine("DESKTOP_IDENTITY_SAMPLE apps.list.ambiguous " + JsonSerializer.Serialize(ambiguous));
            File.Delete(Path.Combine(system, "other.desktop"));
            Assert(Match("ghostty").desktopId == "dsh.desktop", "Unique Exec fallback");
            Write(system, "ghostty.desktop", "Name=Exact ID\nExec=unrelated\n");
            Assert(Match("GHOSTTY").displayName == "Exact ID", "Case-insensitive desktop ID beats Exec");
            File.Delete(Path.Combine(system, "ghostty.desktop"));
            Write(system, "terminal.desktop", "Name=WMClass\nStartupWMClass=GHOSTTY\nExec=unrelated\n");
            Assert(Match("ghostty").displayName == "WMClass", "Case-insensitive WMClass beats Exec");
            Write(system, "second.desktop", "Name=Second WMClass\nStartupWMClass=ghostty\nExec=another\n");
            Assert(Match("ghostty").desktopId == null, "Ambiguous identity is not arbitrary or an Exec fallback");
            foreach (var exec in new[] { "sh -c \"ghostty -e dsh\"", "bash -c ghostty", "env -u NAME ghostty",
                "env --chdir /tmp ghostty", "env -- sh -c ghostty", "flatpak run com.mitchellh.ghostty",
                "gtk-launch com.mitchellh.ghostty", "uwsm app -- ghostty", "omarchy-launch-terminal ghostty",
                "env X=1 omarchy-launch-browser", "env", "env --", "env %U" })
                Assert(LinuxRunningApps.ExecBasename(exec) == null, "Ignore unsupported wrapper: " + exec);
            Assert(LinuxRunningApps.ExecBasename("/usr/bin/env -i MODE=test /usr/bin/ghostty %U") == "ghostty", "Simple env unwrapped");
            Assert(LinuxRunningApps.ExecBasename("env -- /usr/bin/ghostty") == "ghostty", "env option terminator");
            Assert(LinuxRunningApps.ExecBasename("\"/opt/Space App\" %U") == "Space App", "Quoted target");
            Write(system, "wrapped.desktop", "Name=Wrapped\nExec=sh -c ghostty\n");
            Assert(Match("sh").desktopId == null, "Shell wrapper process gets no desktop metadata");
            Console.WriteLine("[DesktopIdentityChecks] Desktop identity, ambiguity and wrapper fixtures passed");

            RunningApp Match(string name) => LinuxRunningApps.Match(new[] { name },
                LinuxRunningApps.ReadDesktopIndex(new[] { user, system })).Single();
        }
        finally { Directory.Delete(root, recursive: true); }
        return Task.CompletedTask;
    }

    private static void Write(string dir, string id, string fields) =>
        File.WriteAllText(Path.Combine(dir, id), "[Desktop Entry]\nType=Application\n" + fields);

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
