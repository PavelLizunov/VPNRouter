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
    // r5: separate "remove from VPN" verb (always a flat verb alongside the
    // Add verb). Kept as its own key so Unregister/uninstall can drop both.
    private const string UnverbKey = "VPNRouterUnroute";
    private static readonly string[] FileClasses = { "exefile", "lnkfile" };

    private static string MenuLabel =>
        VPNRouter.Core.Localization.Strings.ShellMenuRouteLabel;

    // r4: parent label for the cascading submenu (multi-category case).
    private static string ParentLabel =>
        VPNRouter.Core.Localization.Strings.ShellMenuParentLabel;

    // r5: "Remove from VPNRouter" verb label.
    private static string UnrouteLabel =>
        VPNRouter.Core.Localization.Strings.ShellMenuUnrouteLabel;

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

            // r2: the icon MUST come from VPNRouter.App.exe (the .NET app, which
            // carries the penguin Win32 icon resource), NOT VPNRouter.GUI.exe —
            // the Go stub launcher has no icon resource, so "GUI.exe,0" rendered
            // a blank/default icon in the context menu (user report 2026-05-28).
            // Same lesson as ShortcutSelfHeal's IconLocation. Command still
            // launches GUI.exe (the stub IS the entry point).
            var appExe = Path.Combine(dir, "VPNRouter.App.exe");
            var iconSource = File.Exists(appExe) ? appExe : gui;
            var icon = $"\"{iconSource}\",0";

            // r4/r6: normalize the category list. A name embedded into the
            // submenu command (--category "<name>") must be shell-safe: '"'
            // breaks the arg, '\' (esp. trailing) escapes the closing quote,
            // and '%' is Explorer-token-expanded (audit finding #5). AddCategory
            // strips these at the source; this is the backstop for any
            // legacy-persisted name. Distinct, order-preserved. ≤1 category →
            // flat one-click verb (common case, no regression). >1 → cascading
            // "VPNRouter ▸" submenu, one item per category.
            var cats = (categories ?? Array.Empty<string>())
                .Where(c => !string.IsNullOrWhiteSpace(c)
                    && !c.Contains('"') && !c.Contains('%') && !c.Contains('\\'))
                .Select(c => c.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            bool submenu = cats.Count > 1;

            foreach (var cls in FileClasses)
            {
                // Delete any prior structure first so flat<->submenu transitions
                // are clean (CreateSubKey alone leaves stale child keys behind).
                try
                {
                    Registry.CurrentUser.DeleteSubKeyTree(
                        $@"Software\Classes\{cls}\shell\{VerbKey}",
                        throwOnMissingSubKey: false);
                }
                catch { /* best-effort */ }

                using var verb = Registry.CurrentUser.CreateSubKey(
                    $@"Software\Classes\{cls}\shell\{VerbKey}");
                if (verb == null) continue;
                verb.SetValue("Icon", icon);

                if (!submenu)
                {
                    // Flat single verb (common case — 0 or 1 custom category).
                    verb.SetValue(null, MenuLabel);
                    using var cmd = verb.CreateSubKey("command");
                    cmd?.SetValue(null, $"\"{gui}\" --route-app \"%1\"");
                }
                else
                {
                    // Cascading "VPNRouter ▸" submenu: one child per category.
                    // Empty SubCommands + a nested `shell` subkey is the documented
                    // per-user cascade pattern (no COM / no package needed).
                    verb.SetValue("MUIVerb", ParentLabel);
                    verb.SetValue("SubCommands", string.Empty);
                    using var shell = verb.CreateSubKey("shell");
                    if (shell == null) continue;
                    for (int i = 0; i < cats.Count; i++)
                    {
                        using var child = shell.CreateSubKey($"cmd{i:D2}");
                        if (child == null) continue;
                        child.SetValue(null, cats[i]);   // menu item label = category name
                        child.SetValue("Icon", icon);
                        using var ccmd = child.CreateSubKey("command");
                        ccmd?.SetValue(null,
                            $"\"{gui}\" --route-app \"%1\" --category \"{cats[i]}\"");
                    }
                }
            }

            // r5: separate flat "Remove from VPNRouter" verb (always present,
            // both classes). No COM → can't conditionally hide, so it's always
            // visible and no-ops with a toast if the app wasn't routed. Own
            // foreach so the Add-verb loop's `continue`s never skip it.
            foreach (var cls in FileClasses)
            {
                try
                {
                    Registry.CurrentUser.DeleteSubKeyTree(
                        $@"Software\Classes\{cls}\shell\{UnverbKey}",
                        throwOnMissingSubKey: false);
                }
                catch { /* best-effort */ }

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

    /// <summary>Remove the verb from both classes. Idempotent, best-effort.</summary>
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
