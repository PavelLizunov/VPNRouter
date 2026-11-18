using Microsoft.Win32;
using System.Runtime.Versioning;

namespace VPNRouter.Core.Services;

// Only the service writes this value under its SCM registration. Desired settings remain in config.yaml.
internal static class ServiceSettingsReceipt
{
    private const string ValueName = "VPNRouterLastSettingsApply";

    [SupportedOSPlatform("windows")]
    internal static string[]? Read(string serviceName)
    {
        using var registration = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + serviceName);
        return registration?.GetValue(ValueName) as string[];
    }

    [SupportedOSPlatform("windows")]
    internal static void Publish(string serviceName, string revision, bool applied)
    {
        using var registration = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + serviceName, writable: true)
            ?? throw new IOException("Service registration is unavailable.");
        registration.SetValue(ValueName, new[]
        {
            Guid.NewGuid().ToString("N"), Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            revision, applied ? "applied" : "rejected"
        }, RegistryValueKind.MultiString);
    }

    internal static string? ReadRejectedCandidate(string[]? receipt, Func<string> readYaml)
    {
        if (receipt is not [_, _, var revision, "rejected"]) return null;
        var yaml = readYaml();
        return SettingsLoader.GetRevision(yaml) == revision ? yaml : null;
    }

    internal static bool? Match(string[]? receipt, string? previousId, int? managerPid, string revision)
    {
        if (receipt is not [var id, var pid, var saved, var result] || id == previousId ||
            !Guid.TryParseExact(id, "N", out _) || managerPid is not > 0 || !int.TryParse(pid, out var writer) ||
            writer != managerPid || saved != revision)
            return null;
        return result switch { "applied" => true, "rejected" => false, _ => null };
    }
}
