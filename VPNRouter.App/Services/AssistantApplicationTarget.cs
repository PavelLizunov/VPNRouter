using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Win32;
using VPNRouter.Core.Services;

namespace VPNRouter.App.Services;

internal static class AssistantApplicationTarget
{
    internal static string? Resolve(string input)
    {
        var windows = OperatingSystem.IsWindows();
        if (input.IndexOfAny(['/', '\\', ':']) >= 0
            || RoutingAppListEditor.NormalizeManualProcessName(input, windows) is not { Length: <= 128 } name
            || !name.All(c => char.IsLetterOrDigit(c) || c is ' ' or '.' or '_' or '-'))
            return null;

        var processes = Process.GetProcessesByName(windows ? Path.GetFileNameWithoutExtension(name) : name);
        try
        {
            foreach (var process in processes)
            {
                try
                {
                    var actual = Path.GetFileName(process.MainModule?.FileName);
                    if (string.Equals(actual, name, windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                        return actual;
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // The process can exit or become inaccessible while it is being inspected.
                }
            }
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }

        return OperatingSystem.IsWindows() ? FindWindowsRegistration(name) : null;
    }

    [SupportedOSPlatform("windows")]
    private static string? FindWindowsRegistration(string name)
    {
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var key = baseKey.OpenSubKey($@"Software\Microsoft\Windows\CurrentVersion\App Paths\{name}");
            if (key?.GetValue(null) is string path && Path.IsPathFullyQualified(path) && File.Exists(path)
                && string.Equals(Path.GetFileName(path), name, StringComparison.OrdinalIgnoreCase))
                return Path.GetFileName(path);
        }

        return null;
    }
}
