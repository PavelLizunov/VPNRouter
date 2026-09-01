namespace VPNRouter.Core.Services;

/// <summary>Pure Windows Service Control Manager command-line contracts.</summary>
public static class WindowsServiceCommand
{
    private const string ServiceSwitch = "--service";
    private const string ServiceExecutableName = "VPNRouter.Service.exe";

    public static string FormatImagePath(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        var fullPath = Path.GetFullPath(executablePath);
        if (fullPath.Contains('"'))
            throw new ArgumentException("Executable path cannot contain a quote.", nameof(executablePath));

        return $"\"{fullPath}\" {ServiceSwitch}";
    }

    public static string[] BuildCreateArguments(
        string serviceName,
        string executablePath,
        string displayName,
        string? dependencies = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        var imagePath = FormatImagePath(executablePath);

        if (dependencies is null)
        {
            return
            [
                "create", serviceName,
                "binPath=", imagePath,
                "start=", "auto",
                "obj=", "LocalSystem",
                "DisplayName=", displayName
            ];
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(dependencies);
        return
        [
            "create", serviceName,
            "binPath=", imagePath,
            "start=", "auto",
            "obj=", "LocalSystem",
            "depend=", dependencies,
            "DisplayName=", displayName
        ];
    }

    public static string[] BuildFailureRecoveryArguments(string serviceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        return
        [
            "failure", serviceName,
            "reset=", "86400",
            "actions=", "restart/60000/restart/60000/restart/60000"
        ];
    }

    public static bool IsRecognizedVpnRouterImagePath(
        string? configuredImagePath,
        out string executablePath)
    {
        executablePath = string.Empty;
        if (string.IsNullOrWhiteSpace(configuredImagePath)) return false;

        var value = configuredImagePath.Trim();
        string candidate;
        if (value.StartsWith('"'))
        {
            var closingQuote = value.IndexOf('"', 1);
            if (closingQuote < 0) return false;

            var quotedPart = value[1..closingQuote];
            var remainder = value[(closingQuote + 1)..].Trim();
            if (string.Equals(remainder, ServiceSwitch, StringComparison.OrdinalIgnoreCase))
            {
                candidate = quotedPart;
            }
            else if (remainder.Length == 0 && TryRemoveServiceSwitch(quotedPart, out var legacy))
            {
                candidate = legacy;
            }
            else
            {
                return false;
            }
        }
        else if (!TryRemoveServiceSwitch(value, out candidate))
        {
            return false;
        }

        if (candidate.Contains('"') || !Path.IsPathFullyQualified(candidate))
            return false;

        try
        {
            var fullPath = Path.GetFullPath(candidate);
            if (!string.Equals(
                    Path.GetFileName(fullPath),
                    ServiceExecutableName,
                    StringComparison.OrdinalIgnoreCase))
                return false;

            executablePath = fullPath;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryRemoveServiceSwitch(string value, out string executablePath)
    {
        var suffix = $" {ServiceSwitch}";
        if (!value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            executablePath = string.Empty;
            return false;
        }

        executablePath = value[..^suffix.Length].TrimEnd();
        return executablePath.Length > 0;
    }

    public static bool IsCurrentImagePath(string? configuredImagePath, string executablePath) =>
        string.Equals(
            configuredImagePath?.Trim(),
            FormatImagePath(executablePath),
            StringComparison.OrdinalIgnoreCase);

    public static string GetSystemScPath()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("sc.exe is available only on Windows.");

        var systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
        if (string.IsNullOrWhiteSpace(systemDirectory))
            throw new InvalidOperationException("Cannot resolve the Windows system directory.");
        return Path.Combine(systemDirectory, "sc.exe");
    }
}
