using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

public static class SingBoxFeatures
{
    private static readonly object _gate = new();
    private static bool _probed;
    private static bool _awg;
    private static bool _xhttp;
    private static bool _dnstt;
    private static bool _embeddedDnstt;

    internal static bool EmbeddedCore { get; set; } = OperatingSystem.IsAndroid();

    public static bool AwgAvailable
    {
        get
        {
            var policy = SingBoxRuntimePolicy.Current;
            if (policy != null) return policy.AwgAvailable;
            return OverrideAwg ?? Probe().awg;
        }
    }

    public static bool XhttpAvailable
    {
        get
        {
            var policy = SingBoxRuntimePolicy.Current;
            if (policy != null) return policy.XhttpAvailable;
            return OverrideXhttp ?? Probe().xhttp;
        }
    }

    public static bool DnsttAvailable
    {
        get
        {
            if (OverrideDnstt is bool value) return value;
            if (EmbeddedCore) return _embeddedDnstt;
            if (SingBoxRuntimePolicy.Current is { } policy) return policy.DnsttAvailable;
            _ = Probe();
            return _dnstt;
        }
    }

    public static void SetEmbeddedCoreVersion(string version) => _embeddedDnstt = HasDnsttVersion(version);

    internal static bool HasDnsttVersion(string version)
    {
        var parts = version.Trim().Split("-vpnctl.", StringSplitOptions.None);
        return parts.Length == 2 && Version.TryParse(parts[0], out var core)
            && int.TryParse(parts[1], out int patch) && patch >= 1
            && (core > new Version(1, 14, 4) || (core == new Version(1, 14, 4) && patch >= 2));
    }

    internal static string? ReadDesktopCoreVersion(SingBoxSettings? settings)
    {
        if (EmbeddedCore || OperatingSystem.IsAndroid() || OperatingSystem.IsIOS()) return null;
        try
        {
            string? path;
            if (SingBoxRuntimePolicy.Current is { } policy)
            {
                if (!policy.IsAvailable) return null;
                path = policy.SelectedExecutablePath;
            }
            else if (OperatingSystem.IsMacOS())
            {
                // Connect provisions the privileged copy from this bundle before starting.
                path = AppPaths.BundledSingBoxExePath;
            }
            else if (OperatingSystem.IsWindows() && settings == null)
            {
                return null;
            }
            else
            {
                path = AppPaths.SingBoxDeploymentSource(AppPaths.BundledSingBoxExePath,
                    AppPaths.DeployedSingBoxExePath(settings));
            }
            return path != null && File.Exists(path)
                ? ParseManagedCoreVersion(ReadVersionOutput(Path.GetFullPath(path)))
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    internal static string? ParseManagedCoreVersion(string output)
    {
        const string prefix = "sing-box version ";
        var line = Array.Find(output.Split('\n'), value => value.StartsWith(prefix, StringComparison.Ordinal));
        if (line == null) return null;
        var version = line[prefix.Length..].Trim();
        var parts = version.Split("-vpnctl.", StringSplitOptions.None);
        return parts.Length == 2 && Version.TryParse(parts[0], out var core) && core.Build >= 0
            && core.Revision < 0 && core.ToString(3) == parts[0]
            && uint.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var patch)
            && patch > 0 && patch.ToString(CultureInfo.InvariantCulture) == parts[1]
            ? version : null;
    }

    internal static bool? OverrideDnstt { get; set; }

    internal static bool? OverrideAwg { get; set; }

    internal static bool? OverrideXhttp { get; set; }

    public static void Prewarm()
    {
        if (SingBoxRuntimePolicy.Current != null) return;
        if (OverrideAwg.HasValue && OverrideXhttp.HasValue) return;
        _ = Task.Run(() => { try { _ = Probe(); } catch { } });
    }

    internal static void ResetForTests()
    {
        lock (_gate)
        {
            _probed = false;
            _awg = false;
            _xhttp = false;
            _dnstt = false;
            _embeddedDnstt = false;
            OverrideDnstt = null;
            EmbeddedCore = OperatingSystem.IsAndroid();
            OverrideAwg = null;
            OverrideXhttp = null;
        }
    }

    private static (bool awg, bool xhttp) Probe()
    {
        var policy = SingBoxRuntimePolicy.Current;
        if (policy != null) return (policy.AwgAvailable, policy.XhttpAvailable);
        // Android runs the core inside the app (libbox, built with with_awg and with_xhttp, checked in CI); no executable to ask.
        if (EmbeddedCore) return (true, true);
        if (_probed) return (_awg, _xhttp);
        lock (_gate)
        {
            if (_probed) return (_awg, _xhttp);
            try
            {
                var path = ResolveBinaryPath();
                if (path != null && File.Exists(path))
                {
                    var output = ReadVersionOutput(path);
                    const string prefix = "sing-box version ";
                    var lines = output.Split('\n');
                    var tags = Array.Find(lines, line => line.TrimStart().StartsWith("Tags:", StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
                    var version = Array.Find(lines, line => line.StartsWith(prefix, StringComparison.Ordinal));
                    _dnstt = version != null && HasDnsttVersion(version[prefix.Length..]);
                    _awg = tags.Contains("with_awg", StringComparison.Ordinal);
                    _xhttp = tags.Contains("with_xhttp", StringComparison.Ordinal);
                }
            }
            catch
            {
                // Any probe failure keeps the safe default: fork protocols stay rejected.
                _awg = false;
                _xhttp = false;
                _dnstt = false;
            }
            _probed = true;
            return (_awg, _xhttp);
        }
    }

    private static string ResolveBinaryPath()
    {
        try
        {
            var bundled = Path.Combine(AppContext.BaseDirectory,
                OperatingSystem.IsWindows() ? "sing-box.exe" : "sing-box");
            if (File.Exists(bundled)) return bundled;
        }
        catch { }
        return AppPaths.SingBoxExePath;
    }

    private static string ReadVersionOutput(string path)
    {
        var psi = new ProcessStartInfo
        {
            FileName = path,
            Arguments = "version",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi);
        if (p == null) return string.Empty;
        // Drain stdout and stderr concurrently before WaitForExit, or a full stderr pipe deadlocks the child.
        var outTask = p.StandardOutput.ReadToEndAsync();
        var errTask = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(5000))
        {
            try { p.Kill(true); } catch { }
            return string.Empty;
        }
        string stdout;
        try
        {
            stdout = outTask.GetAwaiter().GetResult();
            _ = errTask.GetAwaiter().GetResult();
        }
        catch { return string.Empty; }
        return p.ExitCode == 0 ? stdout : string.Empty;
    }
}
