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
}
