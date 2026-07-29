namespace VPNRouter.Core.Services;

internal static class LinuxRuntimeEnvironment
{
    internal const string StandardPkexecPath = "/usr/bin/pkexec";
    internal const string NixOsPkexecPath = "/run/wrappers/bin/pkexec";

    internal static string? GetTunPrivilegeBlocker()
    {
        if (!OperatingSystem.IsLinux()) return null;

        try
        {
            if (HasNoNewPrivileges(File.ReadAllText("/proc/self/status")))
                return "NoNewPrivs";
        }
        catch
        {
            // Unknown runtime: preserve the existing native-Linux launch path.
        }

        try
        {
            if (IsNonInitialUserNamespace(File.ReadAllText("/proc/self/uid_map")))
                return "a user namespace";
        }
        catch
        {
            // Unknown runtime: the exact sing-box error remains the backstop.
        }

        return null;
    }

    internal static bool HasNoNewPrivileges(string? status)
    {
        if (string.IsNullOrWhiteSpace(status)) return false;

        foreach (var line in status.Split('\n'))
        {
            if (!line.StartsWith("NoNewPrivs:", StringComparison.Ordinal)) continue;
            return line.AsSpan("NoNewPrivs:".Length).Trim().Equals("1", StringComparison.Ordinal);
        }

        return false;
    }

    internal static bool IsNonInitialUserNamespace(string? uidMap)
    {
        if (string.IsNullOrWhiteSpace(uidMap)) return false;

        var ranges = uidMap.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (ranges.Length == 0) return false;

        foreach (var range in ranges)
        {
            var fields = range.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length != 3 ||
                !ulong.TryParse(fields[0], out var inside) ||
                !ulong.TryParse(fields[1], out var outside) ||
                !ulong.TryParse(fields[2], out var length))
            {
                return false;
            }

            if (ranges.Length != 1 || inside != 0 || outside != 0 || length != uint.MaxValue)
                return true;
        }

        return false;
    }

    internal static string? ResolvePkexec() => ResolvePkexec(File.Exists);

    internal static string? ResolvePkexec(Func<string, bool> exists) =>
        exists(StandardPkexecPath) ? StandardPkexecPath :
        exists(NixOsPkexecPath) ? NixOsPkexecPath :
        null;
}
