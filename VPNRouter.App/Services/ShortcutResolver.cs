#nullable enable
using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Serilog;

namespace VPNRouter.App.Services;

[SupportedOSPlatform("windows")]
public static class ShortcutResolver
{
    public static string? ResolveToExeName(string? path, ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var p = path.Trim().Trim('"').Trim();

        try
        {
            if (p.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                var target = ResolveLnkTarget(p, logger);
                if (string.IsNullOrWhiteSpace(target)) return null;
                p = target.Trim();
            }

            if (!p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return null;

            var name = Path.GetFileName(p);
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }
        catch (Exception ex)
        {
            logger?.Warning(ex, "[ShortcutResolver] failed to resolve {Path}", path);
            return null;
        }
    }

    private static string? ResolveLnkTarget(string lnkPath, ILogger? logger)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType == null) return null;

        object? shell = null;
        object? lnk = null;
        try
        {
            shell = Activator.CreateInstance(shellType);
            if (shell == null) return null;

            lnk = shellType.InvokeMember("CreateShortcut",
                BindingFlags.InvokeMethod, null, shell, new object[] { lnkPath });
            if (lnk == null) return null;

            return lnk.GetType().InvokeMember("TargetPath",
                BindingFlags.GetProperty, null, lnk, null) as string;
        }
        finally
        {
            try { if (lnk != null) Marshal.FinalReleaseComObject(lnk); } catch { }
            try { if (shell != null) Marshal.FinalReleaseComObject(shell); } catch { }
        }
    }
}
