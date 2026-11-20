#nullable enable
using System;
using System.IO;
using VPNRouter.Core.Services;

namespace VPNRouter.Core.Platform.Linux;

internal static class LinuxDesktopPackage
{
    internal const string InstallRoot = "/opt/vpnrouter";
    internal const string SingBoxFileName = "sing-box";

    internal static string? TryGetProtectedRuntimePath(string appBaseDirectory)
        => TryGetProtectedRuntimePath(appBaseDirectory, InstallRoot, IsTrustedPackagePath);

    internal static string? TryGetProtectedRuntimePath(
        string appBaseDirectory,
        string installRoot,
        Func<string, bool, bool> isTrustedPath)
    {
        if (!OperatingSystem.IsLinux() || isTrustedPath is null)
            return null;
        if (string.IsNullOrWhiteSpace(appBaseDirectory) ||
            string.IsNullOrWhiteSpace(installRoot))
            return null;

        string appRoot;
        string packageRoot;
        try
        {
            appRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(appBaseDirectory));
            packageRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installRoot));
        }
        catch
        {
            return null;
        }

        if (!string.Equals(appRoot, packageRoot, StringComparison.Ordinal))
            return null;

        string? current = packageRoot;
        while (!string.IsNullOrEmpty(current))
        {
            if (!isTrustedPath(current, true))
                return null;
            current = Path.GetDirectoryName(current);
        }

        var runtime = Path.Combine(packageRoot, SingBoxFileName);
        return isTrustedPath(runtime, false) ? runtime : null;
    }

    internal static bool IsTrustedPackagePath(string path, bool directory)
        => SingBoxRuntimePolicy.IsTrustedPackagePath(path, directory);
}
