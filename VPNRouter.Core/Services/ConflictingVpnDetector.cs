using System.Diagnostics;
using Serilog;

namespace VPNRouter.Core.Services;

public static class ConflictingVpnDetector
{
    public static readonly IReadOnlyList<string> KnownVpnProcessNames = new[]
    {
        "xraycore",
        "openvpn",
        "openvpnconnect",
        "hiddify",
        "qv2ray",
        "nekoray",
        "nekobox",
    };

    public static readonly IReadOnlyList<string> CoexistingVpnProcessNames = new[]
    {
        "wireguard",
        "amneziavpn",
    };

    public sealed record ConflictingProcessInfo(string ProcessName, int Pid, string FullPath);

    public static List<ConflictingProcessInfo> DetectConflictingVpnProcesses(ILogger? logger = null)
        => DetectByNames(KnownVpnProcessNames, "ConflictingVpnDetector", logger);

    public static List<ConflictingProcessInfo> DetectCoexistingVpnProcesses(ILogger? logger = null)
        => DetectByNames(CoexistingVpnProcessNames, "CoexistingVpnDetector", logger);

    private static List<ConflictingProcessInfo> DetectByNames(
        IReadOnlyList<string> names, string logTag, ILogger? logger)
    {
        var matches = new List<ConflictingProcessInfo>();

        if (!OperatingSystem.IsWindows())
            return matches;

        foreach (var name in names)
        {
            Process[]? procs = null;
            try
            {
                procs = Process.GetProcessesByName(name);
                foreach (var p in procs)
                {
                    string fullPath = "";
                    try { fullPath = p.MainModule?.FileName ?? ""; }
                    catch { }

                    matches.Add(new ConflictingProcessInfo(
                        ProcessName: name,
                        Pid: p.Id,
                        FullPath: fullPath));

                    logger?.Information(
                        "[{LogTag}] Found: {ProcessName} (PID {Pid}, path {FullPath})",
                        logTag, name, p.Id, string.IsNullOrEmpty(fullPath) ? "<protected>" : fullPath);
                }
            }
            catch (Exception ex)
            {
                logger?.Debug(ex,
                    "[{LogTag}] Probe failed for {Name} — skipping", logTag, name);
            }
            finally
            {
                if (procs != null)
                    foreach (var p in procs) p.Dispose();
            }
        }

        return matches;
    }
}

public class ConflictingVpnException : Exception
{
    public IReadOnlyList<ConflictingVpnDetector.ConflictingProcessInfo> Conflicts { get; }

    public ConflictingVpnException(
        IReadOnlyList<ConflictingVpnDetector.ConflictingProcessInfo> conflicts,
        string message)
        : base(message)
    {
        Conflicts = conflicts;
    }
}
