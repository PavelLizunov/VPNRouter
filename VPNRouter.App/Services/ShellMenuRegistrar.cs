#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using Serilog;

namespace VPNRouter.App.Services;

[SupportedOSPlatform("windows")]
public static class ShellMenuRegistrar
{
    private const string VerbKey = "VPNRouterRoute";
    private const string UnverbKey = "VPNRouterUnroute";
    private static readonly string[] FileClasses = { "exefile", "lnkfile" };

    private static string MenuLabel =>
        VPNRouter.Core.Localization.Strings.ShellMenuRouteLabel;

    private static string ParentLabel =>
        VPNRouter.Core.Localization.Strings.ShellMenuParentLabel;

    private static string UnrouteLabel =>
        VPNRouter.Core.Localization.Strings.ShellMenuUnrouteLabel;

    private const int SHCNE_ASSOCCHANGED = 0x08000000;
    private const uint SHCNF_IDLIST = 0x0000;

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(
        int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

    private static void NotifyShellAssocChanged()
    {
        try { SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero); }
        catch {  }
    }

    public static void Register(IReadOnlyList<string>? categories = null, ILogger? logger = null)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var dir = AppContext.BaseDirectory.TrimEnd('\\');
            var gui = Path.Combine(dir, "VPNRouter.GUI.exe");
            if (!File.Exists(gui))
            {
                logger?.Debug("[ShellMenu] GUI exe not at {Path} — skip register", gui);
                return;
            }

            var appExe = Path.Combine(dir, "VPNRouter.App.exe");
            var iconSource = File.Exists(appExe) ? appExe : gui;
            var icon = $"\"{iconSource}\",0";

            var cats = (categories ?? Array.Empty<string>())
                .Where(c => !string.IsNullOrWhiteSpace(c)
                    && !c.Contains('"') && !c.Contains('%') && !c.Contains('\\'))
                .Select(c => c.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            bool submenu = cats.Count > 1;

            foreach (var cls in FileClasses)
            {
                try
                {
                    Registry.CurrentUser.DeleteSubKeyTree(
                        $@"Software\Classes\{cls}\shell\{VerbKey}",
                        throwOnMissingSubKey: false);
                }
                catch {  }

                using var verb = Registry.CurrentUser.CreateSubKey(
                    $@"Software\Classes\{cls}\shell\{VerbKey}");
                if (verb == null) continue;
                verb.SetValue("Icon", icon);

                if (!submenu)
                {
                    verb.SetValue(null, MenuLabel);
                    using var cmd = verb.CreateSubKey("command");
                    cmd?.SetValue(null, $"\"{gui}\" --route-app \"%1\"");
                }
                else
                {
                    verb.SetValue("MUIVerb", ParentLabel);
                    verb.SetValue("SubCommands", string.Empty);
                    using var shell = verb.CreateSubKey("shell");
                    if (shell == null) continue;
                    for (int i = 0; i < cats.Count; i++)
                    {
                        using var child = shell.CreateSubKey($"cmd{i:D2}");
                        if (child == null) continue;
                        child.SetValue(null, cats[i]);
                        child.SetValue("Icon", icon);
                        using var ccmd = child.CreateSubKey("command");
                        ccmd?.SetValue(null,
                            $"\"{gui}\" --route-app \"%1\" --category \"{cats[i]}\"");
                    }
                }
            }

            foreach (var cls in FileClasses)
            {
                try
                {
                    Registry.CurrentUser.DeleteSubKeyTree(
                        $@"Software\Classes\{cls}\shell\{UnverbKey}",
                        throwOnMissingSubKey: false);
                }
                catch {  }

                using var un = Registry.CurrentUser.CreateSubKey(
                    $@"Software\Classes\{cls}\shell\{UnverbKey}");
                if (un == null) continue;
                un.SetValue(null, UnrouteLabel);
                un.SetValue("Icon", icon);
                using var uncmd = un.CreateSubKey("command");
                uncmd?.SetValue(null, $"\"{gui}\" --unroute-app \"%1\"");
            }

            NotifyShellAssocChanged();
            logger?.Information("[ShellMenu] registered verbs (route={Mode} + unroute) on exefile + lnkfile",
                submenu ? $"submenu/{cats.Count}" : "flat");
        }
        catch (Exception ex)
        {
            logger?.Warning(ex, "[ShellMenu] register failed (non-fatal)");
        }
    }

    public static void Unregister(ILogger? logger = null)
    {
        if (!OperatingSystem.IsWindows()) return;
        foreach (var cls in FileClasses)
        {
            foreach (var key in new[] { VerbKey, UnverbKey })
            {
                try
                {
                    Registry.CurrentUser.DeleteSubKeyTree(
                        $@"Software\Classes\{cls}\shell\{key}",
                        throwOnMissingSubKey: false);
                }
                catch (Exception ex)
                {
                    logger?.Debug(ex, "[ShellMenu] unregister {Class}\\{Key} failed", cls, key);
                }
            }
        }
        NotifyShellAssocChanged();
        logger?.Information("[ShellMenu] unregistered context-menu verbs");
    }
}
