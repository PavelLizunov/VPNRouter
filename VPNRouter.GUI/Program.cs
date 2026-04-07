// Backwards-compat launcher stub.
// Old auto-updater (v2.3.x) validates the update package by checking VPNRouter.GUI.exe.
// Old shortcuts also point to VPNRouter.GUI.exe.
// This stub forwards to VPNRouter.App.exe (the real Avalonia app) and exits.
using System;
using System.Diagnostics;
using System.IO;

namespace VPNRouter.GUI;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            var dir = AppContext.BaseDirectory;
            var realExe = Path.Combine(dir, "VPNRouter.App.exe");
            if (!File.Exists(realExe))
                return 1;

            var psi = new ProcessStartInfo
            {
                FileName = realExe,
                UseShellExecute = true,
                WorkingDirectory = dir
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            Process.Start(psi);
            return 0;
        }
        catch
        {
            return 2;
        }
    }
}
