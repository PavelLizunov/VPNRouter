#if PLATFORM_WINDOWS
using System;
using System.IO;
using System.Reflection;

namespace VPNRouter.App.Services;

public static class ShortcutSelfHeal
{
    private static string GlobalShortcutPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            "Programs", "VPNRouter.lnk");

    public static bool EnsureTrampolineTarget()
    {
        if (!OperatingSystem.IsWindows()) return false;

        var lnkPath = GlobalShortcutPath;
        if (!File.Exists(lnkPath)) return false;

        var appDir   = AppContext.BaseDirectory.TrimEnd('\\');
        var guiTarget = Path.Combine(appDir, "VPNRouter.GUI.exe");
        if (!File.Exists(guiTarget)) return false;

        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return false;

            var shell = Activator.CreateInstance(shellType);
            if (shell == null) return false;

            var lnk = shellType.InvokeMember("CreateShortcut",
                BindingFlags.InvokeMethod, null, shell, new object[] { lnkPath });
            if (lnk == null) return false;

            var lnkType = lnk.GetType();
            var currentTarget = lnkType.InvokeMember("TargetPath",
                BindingFlags.GetProperty, null, lnk, null) as string ?? "";

            if (string.Equals(currentTarget, guiTarget, StringComparison.OrdinalIgnoreCase))
                return false;

            var expectedOldTarget = Path.Combine(appDir, "VPNRouter.App.exe");
            if (!string.Equals(currentTarget, expectedOldTarget, StringComparison.OrdinalIgnoreCase))
                return false;

            lnkType.InvokeMember("TargetPath",
                BindingFlags.SetProperty, null, lnk, new object[] { guiTarget });
            lnkType.InvokeMember("WorkingDirectory",
                BindingFlags.SetProperty, null, lnk, new object[] { appDir });
            lnkType.InvokeMember("IconLocation",
                BindingFlags.SetProperty, null, lnk,
                new object[] { $"{expectedOldTarget},0" });
            lnkType.InvokeMember("Save",
                BindingFlags.InvokeMethod, null, lnk, null);
            return true;
        }
        catch
        {
            return false;
        }
    }
}

#endif
