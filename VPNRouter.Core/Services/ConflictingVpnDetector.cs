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

    /// <summary>
    /// Linux: another TUN interface already carries a full-tunnel route (mihomo, sing-box,
    /// OpenVPN and similar). Split routes such as Tailscale's are not reported.
    /// </summary>
    public static List<string> DetectForeignFullTunnelTun(string ownInterface, ILogger? logger = null)
    {
        var found = new List<string>();
        if (!OperatingSystem.IsLinux())
            return found;
        try
        {
            using var proc = Process.Start(new ProcessStartInfo("ip", "-o route show table all")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            if (proc == null)
                return found;
            var output = proc.StandardOutput.ReadToEndAsync();
            if (!proc.WaitForExit(3000) || !output.Wait(1000))
            {
                try { proc.Kill(); } catch { }
                return found;
            }
            foreach (var dev in ParseFullTunnelDevices(output.Result))
            {
                if (dev != ownInterface && File.Exists($"/sys/class/net/{dev}/tun_flags") && !found.Contains(dev))
                    found.Add(dev);
            }
        }
        catch (Exception ex)
        {
            logger?.Debug(ex, "[ConflictingVpnDetector] Route probe failed — skipping");
        }
        return found;
    }

    internal static IEnumerable<string> ParseFullTunnelDevices(string routes)
    {
        foreach (var line in routes.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var tokens = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0)
                continue;
            var dst = tokens[0] is "unicast" && tokens.Length > 1 ? tokens[1] : tokens[0];
            if (dst is not ("default" or "0.0.0.0/0" or "0.0.0.0/1" or "128.0.0.0/1"))
                continue;
            for (var i = 0; i + 1 < tokens.Length; i++)
                if (tokens[i] == "dev")
                    yield return tokens[i + 1];
        }
    }

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
