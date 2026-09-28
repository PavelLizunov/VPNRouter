#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

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

    public static (bool Added, string? Normalized) TryAddProcessName(
        AppSettings? settings, string? exeNameOrPath)
    {
        if (settings?.App == null) return (false, null);
        if (string.IsNullOrWhiteSpace(exeNameOrPath)) return (false, null);

        var name = exeNameOrPath.Trim().Trim('"').Trim();
        int lastSep = name.LastIndexOfAny(new[] { '\\', '/' });
        if (lastSep >= 0 && lastSep < name.Length - 1)
            name = name.Substring(lastSep + 1);
        if (string.IsNullOrWhiteSpace(name)) return (false, null);

        if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return (false, null);

        settings.App.RoutingAppsInclude ??= new List<string>();
        var list = settings.App.RoutingAppsInclude;

        var existing = list.FirstOrDefault(
            e => string.Equals(e, name, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
            return (false, existing);

        list.Add(name);
        return (true, name);
    }

    public static (bool Removed, string? Normalized) TryRemoveProcessName(
        AppSettings? settings, string? exeNameOrPath)
    {
        if (settings?.App == null) return (false, null);
        if (string.IsNullOrWhiteSpace(exeNameOrPath)) return (false, null);

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
