#nullable enable
using System;
using System.Diagnostics;
using System.IO;

namespace VPNRouter.App.Services;

internal static class FileManagerHelper
{
    public static ProcessStartInfo BuildRevealStartInfo(string filePath)
    {
        var psi = new ProcessStartInfo();
        if (OperatingSystem.IsWindows())
        {
            psi.FileName = "explorer.exe";
            psi.UseShellExecute = false;
            psi.ArgumentList.Add($"/select,{filePath}");
        }
        else
        {
            var dir = Path.GetDirectoryName(filePath);
            psi.FileName = OperatingSystem.IsMacOS() ? "/usr/bin/open" : "xdg-open";
            psi.UseShellExecute = false;
            if (!string.IsNullOrEmpty(dir))
            {
                psi.ArgumentList.Add(dir);
            }
        }
        return psi;
    }
}
