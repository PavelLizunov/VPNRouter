using System;
using System.Runtime.InteropServices;

namespace VPNRouter.App.Services;

// Whether the desktop UI may animate: off when Windows "Show animations in Windows" is off, or when the
// VPNROUTER_REDUCED_MOTION environment variable is set to 1.
internal static class UiMotion
{
    private const uint SpiGetClientAreaAnimation = 0x1042;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint param, ref bool value, uint winIni);

    public static bool Allowed()
    {
        if (Environment.GetEnvironmentVariable("VPNROUTER_REDUCED_MOTION") == "1") return false;
        if (!OperatingSystem.IsWindows()) return true;
        try
        {
            var enabled = true;
            return !SystemParametersInfo(SpiGetClientAreaAnimation, 0, ref enabled, 0) || enabled;
        }
        catch
        {
            return true;
        }
    }
}
