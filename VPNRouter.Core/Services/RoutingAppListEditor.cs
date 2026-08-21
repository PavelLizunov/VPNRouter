#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

/// <summary>
/// Pure editor for the split-tunnel app include list
/// (<see cref="AppConfig.RoutingAppsInclude"/>).
///
/// <para>Introduced for the Explorer context-menu "route this app through
/// VPN" feature (v2.38.0) — see
/// <c>plans/feature-shell-context-menu-add-app.md</c> — but kept UI-free so
/// it is unit-testable and reusable by the Applications page.</para>
///
/// <para>In include mode (the default), <see cref="ConfigGenerator"/> uses
/// <see cref="AppConfig.RoutingAppsInclude"/> verbatim when non-empty, so
/// adding an entry here is what actually routes the app.</para>
/// </summary>
public static class RoutingAppListEditor
{
    public static string? NormalizeManualProcessName(string? value, bool windows)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var name = value.Trim().Trim('"').Trim();
        var lastSeparator = name.LastIndexOfAny(['\\', '/']);
        if (lastSeparator >= 0)
            name = lastSeparator < name.Length - 1 ? name[(lastSeparator + 1)..] : string.Empty;
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(['*', '?', '"']) >= 0)
            return null;

        if (windows)
        {
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return name;
            return Path.HasExtension(name) ? null : name + ".exe";
        }

        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? name[..^4]
            : name;
    }

    /// <summary>
    /// Add an executable's process-name to the split-tunnel include list.
    /// Idempotent and case-insensitive for dedup, but preserves the on-disk
    /// casing of the entry (sing-box <c>process_name</c> matching is
    /// case-sensitive — CLAUDE golden rule #7: <c>Discord.exe</c> ≠
    /// <c>discord.exe</c>, so we never lowercase).
    /// </summary>
    /// <param name="settings">Settings to mutate in place.</param>
    /// <param name="exeNameOrPath">An <c>.exe</c> filename or full path. A
    /// full path is reduced to its filename; the on-disk casing the caller
    /// passes is preserved.</param>
    /// <returns>
    /// <c>Added</c> = <c>true</c> when a new entry was inserted; <c>false</c>
    /// when the input was invalid (null/blank/non-exe) or already present.
    /// <c>Normalized</c> = the basename used (the pre-existing entry's casing
    /// when already present), or <c>null</c> when the input was invalid.
    /// </returns>
    public static (bool Added, string? Normalized) TryAddProcessName(
        AppSettings? settings, string? exeNameOrPath)
    {
        if (settings?.App == null) return (false, null);
        if (string.IsNullOrWhiteSpace(exeNameOrPath)) return (false, null);

        // Reduce to a bare filename — accept a full path or a quoted path
        // defensively (the shell verb passes "%1" which can be either).
        //
        // Split on BOTH '\' and '/' explicitly — NOT Path.GetFileName, which
        // only treats the HOST OS separator as a delimiter. The shell verb
        // always hands us a Windows path, so on a Linux test runner
        // Path.GetFileName(@"C:\…\Game.exe") returns the whole string and the
        // .exe-basename contract breaks. LastIndexOfAny is OS-independent.
        var name = exeNameOrPath.Trim().Trim('"').Trim();
        int lastSep = name.LastIndexOfAny(new[] { '\\', '/' });
        if (lastSep >= 0 && lastSep < name.Length - 1)
            name = name.Substring(lastSep + 1);
        if (string.IsNullOrWhiteSpace(name)) return (false, null);

        // process_name routing only matches executables.
        if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return (false, null);

        settings.App.RoutingAppsInclude ??= new List<string>();
        var list = settings.App.RoutingAppsInclude;

        var existing = list.FirstOrDefault(
            e => string.Equals(e, name, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
            return (false, existing); // already routed — caller toasts "already in list"

        list.Add(name);
        return (true, name);
    }

    /// <summary>
    /// Remove an executable's process-name from the split-tunnel include list.
    /// Mirror of <see cref="TryAddProcessName"/> for the Explorer "remove from
    /// VPN" context-menu verb (v2.38.0-r5). Case-insensitive match; removes
    /// every duplicate of the basename.
    /// </summary>
    /// <param name="settings">Settings to mutate in place.</param>
    /// <param name="exeNameOrPath">An <c>.exe</c> filename or full path. A full
    /// path is reduced to its filename (OS-independent on both <c>\</c> and
    /// <c>/</c> — same contract as <see cref="TryAddProcessName"/>).</param>
    /// <returns>
    /// <c>Removed</c> = <c>true</c> when at least one entry was removed;
    /// <c>false</c> when the input was invalid (null/blank/non-exe) or the
    /// entry wasn't present. <c>Normalized</c> = the basename used, or
    /// <c>null</c> when the input was invalid.
    /// </returns>
    public static (bool Removed, string? Normalized) TryRemoveProcessName(
        AppSettings? settings, string? exeNameOrPath)
    {
        if (settings?.App == null) return (false, null);
        if (string.IsNullOrWhiteSpace(exeNameOrPath)) return (false, null);

        // Same basename reduction as TryAddProcessName — split on BOTH
        // separators so a Windows path passed on a Linux test runner still
        // reduces correctly (LastIndexOfAny is OS-independent).
        var name = exeNameOrPath.Trim().Trim('"').Trim();
        int lastSep = name.LastIndexOfAny(new[] { '\\', '/' });
        if (lastSep >= 0 && lastSep < name.Length - 1)
            name = name.Substring(lastSep + 1);
        if (string.IsNullOrWhiteSpace(name)) return (false, null);

        if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return (false, null);

        var list = settings.App.RoutingAppsInclude;
        if (list == null || list.Count == 0) return (false, name);

        int removed = list.RemoveAll(
            e => string.Equals(e, name, StringComparison.OrdinalIgnoreCase));
        return (removed > 0, name);
    }

    /// <summary>
    /// v2.40.0-r2 (regression review #1): <c>true</c> when another surviving
    /// checked app still routes <paramref name="processName"/>, so the UI scrub
    /// (<c>MainWindowViewModel.ScrubRoutingForApp</c>) must NOT strip that name
    /// from <see cref="AppConfig.RoutingAppsInclude"/> / <c>RoutingAppsExclude</c>.
    ///
    /// <para>The same process name can live as separate AppItems across groups
    /// (the shell-verb add and a profile group both reference <c>Discord.exe</c>;
    /// dedup is within-group only), and <c>SaveSettings</c> never rebuilds the
    /// routing lists from VM state — so an unconditional <c>RemoveAll</c> on one
    /// group's removal silently un-routes an app the user keeps checked elsewhere
    /// (leak-from-intent in reverse: a wanted route is dropped, or an exclude entry
    /// the user still wants is removed). The caller passes the names of all OTHER
    /// still-checked AppItems; we keep the entry if any of them matches.</para>
    ///
    /// <para>Case-insensitive, matching the raw name and its <c>.exe</c>-stripped
    /// form so a cross-platform mix (<c>Discord</c> vs <c>Discord.exe</c>) still
    /// counts as the same app.</para>
    /// </summary>
    public static bool IsStillRoutedByAnother(
        string? processName, IEnumerable<string?>? survivingCheckedNames)
    {
        if (string.IsNullOrWhiteSpace(processName) || survivingCheckedNames == null)
            return false;

        static string Bare(string n) =>
            n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? n.Substring(0, n.Length - 4) : n;

        var name = processName!;
        var bare = Bare(name);
        foreach (var p in survivingCheckedNames)
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            if (string.Equals(p, name, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Bare(p!), bare, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
