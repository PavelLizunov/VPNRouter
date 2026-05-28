#nullable enable
using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Serilog;

namespace VPNRouter.App.Services;

/// <summary>
/// Resolves a filesystem path that the Explorer "route through VPN"
/// context-menu verb hands us (<c>"%1"</c>) down to an <c>.exe</c>
/// process-name (basename, on-disk casing preserved).
///
/// <para>Windows-only. Uses WScript.Shell late-binding to read a
/// <c>.lnk</c>'s <c>TargetPath</c> — same COM pattern as
/// <see cref="ShortcutSelfHeal"/>, so we add no new dependency.</para>
///
/// <para>Casing matters: sing-box <c>process_name</c> matching is
/// case-sensitive (CLAUDE golden rule #7), and both the shell verb's
/// <c>%1</c> and a <c>.lnk</c> <c>TargetPath</c> carry real filesystem
/// casing, so <see cref="Path.GetFileName(string)"/> preserves it.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class ShortcutResolver
{
    /// <summary>
    /// Resolve <paramref name="path"/> to its <c>.exe</c> basename:
    /// an <c>.exe</c> → its own filename; a <c>.lnk</c> → its target's
    /// filename when the target is an <c>.exe</c>. Returns <c>null</c> when
    /// the input isn't a routable executable (incl. folders, docs, broken
    /// shortcuts).
    /// </summary>
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

            // CreateShortcut on an existing .lnk loads it for inspection.
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
