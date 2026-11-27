using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace VPNRouter.Core.Services;

public static class SingBoxFeatures
{
    private static readonly object _gate = new();
    private static bool _probed;
    private static bool _awg;
    private static bool _xhttp;
    private static bool _dnstt;
    private static bool _embeddedDnstt;
    private static bool _turnrelay;
    private static bool _embeddedTurnRelay;
    private static bool _naive;
    private static bool _trustTunnel;
    private static bool _mieru;
    private static bool _embeddedNativeTransports;

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

    public static void SetEmbeddedCoreVersion(string version)
    {
        _embeddedDnstt = HasDnsttVersion(version);
        _embeddedTurnRelay = HasTurnRelayVersion(version);
        _embeddedNativeTransports = HasNativeTransportVersion(version);
    }

    internal static bool HasNativeTransportVersion(string version)
    {
        var parts = version.Trim().Split("-vpnctl.", StringSplitOptions.None);
        return parts.Length == 2 && Version.TryParse(parts[0], out var core) &&
            core >= new Version(1, 14, 5) && int.TryParse(parts[1], out var patch) && patch >= 1;
    }

    internal static bool NaiveAvailable
    {
        get
        {
            if (OverrideNaive is bool value) return value;
            if (EmbeddedCore || SingBoxRuntimePolicy.Current != null ||
                !(OperatingSystem.IsWindows() || OperatingSystem.IsLinux())) return false;
            _ = Probe();
            return _naive;
        }
    }

    public static bool TrustTunnelAvailable
    {
        get
        {
            if (OverrideTrustTunnel is bool value) return value;
            if (EmbeddedCore) return _embeddedNativeTransports;
            if (SingBoxRuntimePolicy.Current is { } policy) return policy.TrustTunnelAvailable;
            _ = Probe();
            return _trustTunnel;
        }
    }

    public static bool MieruAvailable
    {
        get
        {
            if (OverrideMieru is bool value) return value;
            if (EmbeddedCore) return _embeddedNativeTransports;
            if (SingBoxRuntimePolicy.Current is { } policy) return policy.MieruAvailable;
            _ = Probe();
            return _mieru;
        }
    }

    internal static bool HasDnsttVersion(string version)
    {
        var parts = version.Trim().Split("-vpnctl.", StringSplitOptions.None);
        return parts.Length == 2 && Version.TryParse(parts[0], out var core)
            && int.TryParse(parts[1], out int patch) && patch >= 1
            && (core > new Version(1, 14, 4) || (core == new Version(1, 14, 4) && patch >= 2));
    }

    public static bool TurnRelayAvailable
    {
        get
        {
            if (OverrideTurnRelay is bool value) return value;
            if (EmbeddedCore) return _embeddedTurnRelay;
            if (SingBoxRuntimePolicy.Current is { } policy) return policy.TurnRelayAvailable;
            _ = Probe();
            return _turnrelay;
        }
    }

    internal static bool HasTurnRelayVersion(string version)
    {
        version = version.Trim();

        const string localPrefix = "-vpnctl-turnrelay.";
        var localIndex = version.IndexOf(localPrefix, StringComparison.Ordinal);
        var minimumCore = new Version(1, 14, 4);
        if (localIndex > 0)
            return Version.TryParse(version[..localIndex], out var localCore) && localCore >= minimumCore &&
                int.TryParse(version[(localIndex + localPrefix.Length)..], out var localPatch) && localPatch >= 5;

        var parts = version.Split("-vpnctl.", StringSplitOptions.None);
        return parts.Length == 2 && Version.TryParse(parts[0], out var core) &&
            int.TryParse(parts[1], out var patch) && patch >= 1 &&
            (core > minimumCore || (core == minimumCore && patch >= 4));
    }

    internal static bool? OverrideDnstt { get; set; }

    internal static bool? OverrideTurnRelay { get; set; }
    internal static bool? OverrideNaive { get; set; }
    internal static bool? OverrideTrustTunnel { get; set; }
    internal static bool? OverrideMieru { get; set; }

    internal static bool? OverrideAwg { get; set; }

    internal static bool? OverrideXhttp { get; set; }

    public static void Prewarm()
    {
        if (SingBoxRuntimePolicy.Current != null) return;
        if (OverrideAwg.HasValue && OverrideXhttp.HasValue && OverrideDnstt.HasValue && OverrideTurnRelay.HasValue) return;
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
            _turnrelay = false;
            _embeddedTurnRelay = false;
            _trustTunnel = false;
            _mieru = false;
            _embeddedNativeTransports = false;
            OverrideDnstt = null;
            OverrideTurnRelay = null;
            _naive = false;
            OverrideNaive = null;
            OverrideTrustTunnel = null;
            OverrideMieru = null;
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
                    _turnrelay = tags.Contains("with_turnrelay", StringComparison.Ordinal);
                    var featureTags = tags.Length == 0 ? Array.Empty<string>() :
                        tags.TrimStart()[5..].Split(',', StringSplitOptions.TrimEntries);
                    _naive = Array.IndexOf(featureTags, "with_naive_outbound") >= 0;
                    _trustTunnel = Array.IndexOf(featureTags, "with_trusttunnel") >= 0;
                    _mieru = Array.IndexOf(featureTags, "with_mieru") >= 0;
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
                _turnrelay = false;
                _naive = false;
                _trustTunnel = false;
                _mieru = false;
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
