#nullable enable
using System;
using System.Diagnostics;
using System.IO;

namespace VPNRouter.Core.Services;

/// <summary>
/// Distinguishes a VPNRouter-managed sing-box process from a third-party / dev
/// one by its on-disk image path. VPNRouter launches the bundled sing-box from
/// under <see cref="AppPaths.SingBoxExePath"/>'s directory (both the desktop App
/// and the Windows Service use that bin dir); a user's own / a CTF / a dev
/// sing-box runs from elsewhere.
///
/// <para>S1 (v2.45.0): without this, <see cref="RuntimeStatusDetector.IsVpnRunning"/>
/// reported "connected" for ANY process named <c>sing-box</c>, and the user
/// takeover orphan-sweep (<see cref="OrphanCleanup"/> with <c>respectTunLock:false</c>)
/// <c>Kill(entireProcessTree)</c>'d it — so a third-party tunnel showed as ours
/// and could be killed by a Stop/Connect/Update. Ownership is decided by image
/// path so detection + killing only ever touch our own sing-box.</para>
///
/// <para>Unverifiable ownership (image path can't be read) resolves to NOT owned —
/// the safe default for both detection (show Idle, not a false "connected") and
/// killing (never kill what we can't confirm is ours).</para>
/// </summary>
internal static class ProcessOwnership
{
    /// <summary>The directory VPNRouter launches its bundled sing-box from.</summary>
    private static string BinDir => Path.GetDirectoryName(AppPaths.SingBoxExePath) ?? string.Empty;

    /// <summary>Pure: true when <paramref name="path"/> resolves to a file under
    /// <paramref name="dir"/>. Full-path normalised; case-insensitive on Windows.
    /// Null/empty either side -> false. Never throws.</summary>
    internal static bool IsUnderDirectory(string? path, string? dir)
    {
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(dir)) return false;
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        try
        {
            var fullPath = Path.GetFullPath(path);
            var fullDir = Path.GetFullPath(dir);
            if (!fullDir.EndsWith(Path.DirectorySeparatorChar))
                fullDir += Path.DirectorySeparatorChar;
            return fullPath.StartsWith(fullDir, cmp);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Image path of a live process. Uses
    /// <see cref="ProcessImagePath.TryGetByPid"/> on Windows (works cross-session /
    /// from SYSTEM, unlike <c>MainModule</c>), <c>MainModule.FileName</c> elsewhere.
    /// Null on any failure.</summary>
    internal static string? ImagePathOf(Process p)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                return ProcessImagePath.TryGetByPid(p.Id);
            return p.MainModule?.FileName;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>True when <paramref name="p"/> is a VPNRouter-managed sing-box
    /// (its image lives under the VPNRouter bin dir).</summary>
    public static bool IsOwnedSingBox(Process p) => IsUnderDirectory(ImagePathOf(p), BinDir);

    /// <summary>True when at least one alive <c>sing-box</c> process is
    /// VPNRouter-owned. Handle-safe: the <c>Process[]</c> snapshot is disposed in
    /// a <c>finally</c>. Never throws.</summary>
    public static bool AnySingBoxOwned()
    {
        Process[]? procs = null;
        try
        {
            procs = Process.GetProcessesByName("sing-box");
            foreach (var p in procs)
                if (IsOwnedSingBox(p))
                    return true;
            return false;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (procs != null)
                foreach (var p in procs)
                {
                    try { p.Dispose(); } catch { /* GC finalises if Dispose throws */ }
                }
        }
    }
}
