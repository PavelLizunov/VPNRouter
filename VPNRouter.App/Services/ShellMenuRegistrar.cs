#nullable enable
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using Serilog;

namespace VPNRouter.App.Services;

/// <summary>
/// Registers / unregisters the Explorer "route through VPN" context-menu
/// verb (v2.38.0 — see plans/feature-shell-context-menu-add-app.md).
///
/// <para>Per-user (<c>HKCU\Software\Classes</c>) so it needs no admin and
/// uninstalls cleanly. The verb is attached to <c>exefile</c> + <c>lnkfile</c>
/// only (NOT <c>*</c>) and runs <c>VPNRouter.GUI.exe --route-app "%1"</c>.
/// On Windows 10 it shows at the top level of the right-click menu; on
/// Windows 11 it lands under "Show more options" (legacy menu) — the modern
/// top-level menu would need an IExplorerCommand + MSIX/sparse package, which
/// is deferred.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class ShellMenuRegistrar
{
    private const string VerbKey = "VPNRouterRoute";
    private static readonly string[] FileClasses = { "exefile", "lnkfile" };

    private static string MenuLabel =>
        VPNRouter.Core.Localization.Strings.ShellMenuRouteLabel;

    // r3: after writing/removing the verb, tell Explorer that file
    // associations changed so the RUNNING shell re-reads the verb (label +
    // icon) on the spot instead of serving the stale MUI verb-name cache
    // until the next reboot. r2 fixed WHAT we write (App.exe icon + localized
    // label); without this notify the label can lag for a RU user upgrading
    // from an r1 that wrote the English label. SHCNE_ASSOCCHANGED is the
    // documented signal for "shell associations were added/changed/removed".
    private const int SHCNE_ASSOCCHANGED = 0x08000000;
    private const uint SHCNF_IDLIST = 0x0000;

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(
        int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

    private static void NotifyShellAssocChanged()
    {
        try { SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero); }
        catch { /* best-effort: a failed notify just means a slightly stale menu */ }
    }

    /// <summary>
    /// Register (or refresh) the verb. Idempotent — safe to call on every
    /// startup. Best-effort: never throws (a locked-down HKCU just means the
    /// menu entry won't appear; the app still runs).
    /// </summary>
    public static void Register(ILogger? logger = null)
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

            // r2: the icon MUST come from VPNRouter.App.exe (the .NET app, which
            // carries the penguin Win32 icon resource), NOT VPNRouter.GUI.exe —
            // the Go stub launcher has no icon resource, so "GUI.exe,0" rendered
            // a blank/default icon in the context menu (user report 2026-05-28).
            // Same lesson as ShortcutSelfHeal's IconLocation. Command still
            // launches GUI.exe (the stub IS the entry point).
            var appExe = Path.Combine(dir, "VPNRouter.App.exe");
            var iconSource = File.Exists(appExe) ? appExe : gui;

            var command = $"\"{gui}\" --route-app \"%1\"";
            var icon = $"\"{iconSource}\",0";
            foreach (var cls in FileClasses)
            {
                using var verb = Registry.CurrentUser.CreateSubKey(
                    $@"Software\Classes\{cls}\shell\{VerbKey}");
                if (verb == null) continue;
                verb.SetValue(null, MenuLabel);
                verb.SetValue("Icon", icon);
                using var cmd = verb.CreateSubKey("command");
                cmd?.SetValue(null, command);
            }
            NotifyShellAssocChanged();
            logger?.Information("[ShellMenu] registered verb on exefile + lnkfile → {Cmd}", command);
        }
        catch (Exception ex)
        {
            logger?.Warning(ex, "[ShellMenu] register failed (non-fatal)");
        }
    }

    /// <summary>Remove the verb from both classes. Idempotent, best-effort.</summary>
    public static void Unregister(ILogger? logger = null)
    {
        if (!OperatingSystem.IsWindows()) return;
        foreach (var cls in FileClasses)
        {
            try
            {
                Registry.CurrentUser.DeleteSubKeyTree(
                    $@"Software\Classes\{cls}\shell\{VerbKey}",
                    throwOnMissingSubKey: false);
            }
            catch (Exception ex)
            {
                logger?.Debug(ex, "[ShellMenu] unregister {Class} failed", cls);
            }
        }
        NotifyShellAssocChanged();
        logger?.Information("[ShellMenu] unregistered context-menu verb");
    }
}
