#nullable enable
using System;
using System.Diagnostics;
using System.IO;

namespace VPNRouter.App.Services;

internal static class FileManagerHelper
{
    private static string WindowsExplorer()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!Path.IsPathFullyQualified(windows))
            throw new IOException("Could not resolve the Windows directory.");
        return Path.Combine(windows, "explorer.exe");
    }

    public static ProcessStartInfo BuildRevealStartInfo(string filePath)
    {
        var psi = new ProcessStartInfo();
        if (OperatingSystem.IsWindows())
        {
            psi.FileName = WindowsExplorer();
            psi.WorkingDirectory = Environment.SystemDirectory;
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

    public static ProcessStartInfo BuildOpenDirectoryStartInfo(string dirPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dirPath);

        var psi = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows()
                ? WindowsExplorer()
                : (OperatingSystem.IsMacOS() ? "/usr/bin/open" : "xdg-open"),
            UseShellExecute = false,
        };
        if (OperatingSystem.IsWindows())
        {
            if (!Path.IsPathFullyQualified(dirPath))
                throw new ArgumentException("Expected an absolute directory path.", nameof(dirPath));
            psi.WorkingDirectory = Environment.SystemDirectory;
        }
        psi.ArgumentList.Add(dirPath);
        return psi;
    }
}
