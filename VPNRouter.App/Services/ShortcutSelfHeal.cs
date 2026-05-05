#if PLATFORM_WINDOWS
using System;
using System.IO;
using System.Reflection;

namespace VPNRouter.App.Services;

/// <summary>
/// v2.31.9-r1: ensures the Start Menu shortcut points at the trampoline
/// stub (<c>VPNRouter.GUI.exe</c>) rather than <c>VPNRouter.App.exe</c>
/// directly. Pre-r1 <c>install.ps1</c> wrote shortcuts targeting App.exe,
/// which bypassed the trampoline's integrity check on every daily launch.
///
/// <para>This self-heal patches existing users on their first v2.31.9+
/// launch. Users who upgraded via in-app Update don't get a fresh
/// install.ps1 run (the helper.cmd post-update flow doesn't touch the
/// shortcut), so without this migration the trampoline would never fire
/// for them.</para>
///
/// <para>Idempotent: no-op when the shortcut is already correct, missing,
/// or unwritable. Catches all exceptions — never blocks app startup over
/// a cosmetic shortcut fix.</para>
/// </summary>
public static class ShortcutSelfHeal
{
    /// <summary>Per-machine Start Menu shortcut written by install.ps1.</summary>
    private static string GlobalShortcutPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            "Programs", "VPNRouter.lnk");

    /// <summary>
    /// Inspects the Start Menu shortcut and rewrites its TargetPath if it
    /// points at App.exe. Returns true when a change was made (caller may
    /// want to log it); false on already-correct, missing-file, or error.
    /// </summary>
    public static bool EnsureTrampolineTarget()
    {
        if (!OperatingSystem.IsWindows()) return false;

        var lnkPath = GlobalShortcutPath;
        if (!File.Exists(lnkPath)) return false;

        var appDir   = AppContext.BaseDirectory.TrimEnd('\\');
        var guiTarget = Path.Combine(appDir, "VPNRouter.GUI.exe");
        if (!File.Exists(guiTarget)) return false; // no stub on disk, can't migrate

        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return false;

            var shell = Activator.CreateInstance(shellType);
            if (shell == null) return false;

            // CreateShortcut returns an IWshShortcut COM object even for
            // existing files (in which case it loads + lets us modify).
            var lnk = shellType.InvokeMember("CreateShortcut",
                BindingFlags.InvokeMethod, null, shell, new object[] { lnkPath });
            if (lnk == null) return false;

            var lnkType = lnk.GetType();
            var currentTarget = lnkType.InvokeMember("TargetPath",
                BindingFlags.GetProperty, null, lnk, null) as string ?? "";

            // Already correct — no work to do. Normalise for case-insensitive
            // compare since Windows paths are case-insensitive.
            if (string.Equals(currentTarget, guiTarget, StringComparison.OrdinalIgnoreCase))
                return false;

            // Only patch shortcuts that *were* targeting App.exe in our
            // install dir; leave alien shortcuts (e.g. user's hand-edited
            // ones, third-party portable launchers) untouched.
            var expectedOldTarget = Path.Combine(appDir, "VPNRouter.App.exe");
            if (!string.Equals(currentTarget, expectedOldTarget, StringComparison.OrdinalIgnoreCase))
                return false;

            lnkType.InvokeMember("TargetPath",
                BindingFlags.SetProperty, null, lnk, new object[] { guiTarget });
            lnkType.InvokeMember("WorkingDirectory",
                BindingFlags.SetProperty, null, lnk, new object[] { appDir });
            // IconLocation stays pointed at App.exe — the Go stub doesn't
            // carry a Win32 icon resource and we want the penguin to show
            // in Start Menu / pinned-to-taskbar previews.
            lnkType.InvokeMember("IconLocation",
                BindingFlags.SetProperty, null, lnk,
                new object[] { $"{expectedOldTarget},0" });
            lnkType.InvokeMember("Save",
                BindingFlags.InvokeMethod, null, lnk, null);
            return true;
        }
        catch
        {
            // Cosmetic; don't surface to user. Logged by caller's wrap.
            return false;
        }
    }
}

#endif
