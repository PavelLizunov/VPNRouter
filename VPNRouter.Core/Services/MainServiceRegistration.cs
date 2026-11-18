using System.Runtime.Versioning;
using VPNRouter.Core.Localization;

namespace VPNRouter.Core.Services;

internal sealed record MainServiceRegistration(string Name, IWindowsRegistration Handle) : IDisposable
{
    public void Dispose() => Handle.Dispose();

    internal static bool Matches(ServiceConfig config, string exe)
    {
        var image = config.Image.Trim();
        bool MatchesPath(string path) => image.Equals(path, StringComparison.OrdinalIgnoreCase) ||
            image.Equals(path + " --service", StringComparison.OrdinalIgnoreCase);
        return config.Type == 0x10 && (MatchesPath($"\"{exe}\"") || (!exe.Any(char.IsWhiteSpace) && MatchesPath(exe)));
    }

    // The GUI historically created VPNRouterBusiness. The CLI and older hosts registered VPNRouter.
    internal static string? Resolve(string exe, bool business, Func<string, uint, IWindowsRegistration?> open, int? hostPid = null, bool ignoreForeign = false)
    {
        string? selected = null;
        bool selectedActive = false, conflict = false;
        foreach (string name in business ? new[] { "VPNRouterBusiness", "VPNRouter" } : new[] { "VPNRouter" })
        {
            using var registration = open(name, 0);
            if (registration == null) continue;
            if (!Matches(registration.Config, exe)) { conflict = true; continue; }
            var status = registration.ProcessStatus;
            if (hostPid.HasValue && status.Pid != hostPid) continue;
            bool active = status.State != 1;
            if (selected != null && selectedActive == active)
                throw new InvalidOperationException(Strings.MainServiceOwnerConflict);
            if (selected == null || active) { selected = name; selectedActive = active; }
        }
        if (selected == null && conflict && !ignoreForeign) throw new InvalidOperationException(Strings.MainServiceOwnerConflict);
        return selected;
    }

    internal static MainServiceRegistration? OpenVerified(string? name, string exe, uint access,
        Func<string, uint, IWindowsRegistration?> open, int? hostPid = null)
    {
        if (name == null) return null;
        var registration = open(name, access);
        if (registration == null) return null;
        try
        {
            if (!Matches(registration.Config, exe) || (hostPid.HasValue && registration.ProcessStatus.Pid != hostPid))
                throw new InvalidOperationException(Strings.MainServiceOwnerConflict);
            return new(name, registration);
        }
        catch { registration.Dispose(); throw; }
    }

    [SupportedOSPlatform("windows")]
    internal static MainServiceRegistration? CurrentHost()
    {
        var exe = Environment.ProcessPath ?? throw new IOException("Could not identify the service executable.");
        int pid = Environment.ProcessId;
        var name = Resolve(exe, ProductEdition.IsBusiness, WindowsServiceRegistration.OpenWithAccess, pid);
        return OpenVerified(name, exe, 0, WindowsServiceRegistration.OpenWithAccess, pid);
    }
}
