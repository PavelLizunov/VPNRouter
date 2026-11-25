using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace VPNRouter.Core.Services;

public static class SingBoxFeatures
{
    private static readonly object _gate = new();
    internal sealed record DesktopFeatures(bool Awg, bool Xhttp, string? Version)
    {
        internal static readonly DesktopFeatures Unknown = new(false, false, null);
    }

    private static volatile DesktopFeatures? _desktop;
    private static bool _embeddedDnstt;

    internal static bool EmbeddedCore { get; set; } = OperatingSystem.IsAndroid();

    public static bool AwgAvailable
    {
        get
        {
            var policy = SingBoxRuntimePolicy.Current;
            if (policy != null) return policy.AwgAvailable;
            return OverrideAwg ?? Probe().Awg;
        }
    }

    public static bool XhttpAvailable
    {
        get
        {
            var policy = SingBoxRuntimePolicy.Current;
            if (policy != null) return policy.XhttpAvailable;
            return OverrideXhttp ?? Probe().Xhttp;
        }
    }

    public static bool DnsttAvailable
    {
        get
        {
            if (OverrideDnstt is bool value) return value;
            if (EmbeddedCore) return _embeddedDnstt;
            if (SingBoxRuntimePolicy.Current is { } policy) return policy.DnsttAvailable;
            return Probe().Version is { } version && HasDnsttVersion(version);
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
            _desktop = null;
            _embeddedDnstt = false;
            OverrideDnstt = null;
            EmbeddedCore = OperatingSystem.IsAndroid();
            OverrideAwg = null;
            OverrideXhttp = null;
        }
    }

    // Subscription negotiation refreshes the same feature snapshot consumed by the parser.
    internal static string? ReadDesktopVersion() => Probe(refresh: true).Version;

    private static DesktopFeatures Probe(bool refresh = false)
    {
        var policy = SingBoxRuntimePolicy.Current;
        if (policy != null) return new(policy.AwgAvailable, policy.XhttpAvailable, null);
        if (EmbeddedCore) return new(true, true, null);
        if (!refresh && _desktop is { } current) return current;
        lock (_gate)
        {
            if (!refresh && _desktop is { } cached) return cached;
            DesktopFeatures result;
            try
            {
                result = ReadDesktopFeatures(AppPaths.DefaultSingBoxDeployment, ReadVersionOutput);
            }
            catch
            {
                result = DesktopFeatures.Unknown;
            }
            _desktop = result;
            return result;
        }
    }

    internal static DesktopFeatures ReadDesktopFeatures(
        SingBoxDeployment deployment, Func<string, string> readVersionOutput)
    {
        var path = deployment.Source;
        if (!File.Exists(path)) return DesktopFeatures.Unknown;
        var lines = readVersionOutput(path).Split('\n');
        var tags = Array.Find(lines, line => line.TrimStart().StartsWith("Tags:", StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
        return new(tags.Contains("with_awg", StringComparison.Ordinal),
            tags.Contains("with_xhttp", StringComparison.Ordinal), ParseVersion(lines));
    }

    private static string? ParseVersion(string[] lines)
    {
        const string prefix = "sing-box version ";
        var line = Array.Find(lines, text => text.StartsWith(prefix, StringComparison.Ordinal));
        if (line == null) return null;
        var version = line[prefix.Length..].Trim();
        return version.Length is > 0 and <= 96 &&
            Regex.IsMatch(version, @"\A[0-9]+\.[0-9]+\.[0-9]+(?:-[a-zA-Z0-9-]+(?:\.[a-zA-Z0-9-]+)*)?(?:\+[a-zA-Z0-9-]+(?:\.[a-zA-Z0-9-]+)*)?\z",
                RegexOptions.CultureInvariant | RegexOptions.NonBacktracking) ? version : null;
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
