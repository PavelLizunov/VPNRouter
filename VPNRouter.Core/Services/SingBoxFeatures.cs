using System;
using System.Diagnostics;
using System.IO;

namespace VPNRouter.Core.Services;

/// <summary>
/// Runtime capability probe for the bundled sing-box binary.
/// <para>
/// Official builds bundle upstream SagerNet sing-box (NO AmneziaWG / XHTTP). The
/// sing-box-lx fork (opt-in via <c>build.ps1 -SingBoxPath</c>) compiles
/// <c>with_awg</c> + <c>with_xhttp</c>. Fork-only protocols — <c>awg://</c> /
/// <c>amneziawg://</c> and a VLESS <c>type=xhttp</c> transport — MUST be gated on
/// these flags at intake (<see cref="ServerUriParser"/> / <see cref="VlessUriParser"/>):
/// otherwise an untrusted or stale subscription line reaches an official binary,
/// which FATALs at config load (it rejects the <c>endpoints</c> wireguard block /
/// the <c>xhttp</c> transport) and bricks the user's tunnel.
/// </para>
/// <para>
/// Default = <c>false</c> (official-build-safe). The truth comes from the binary's
/// own <c>version</c> "Tags:" line, which Go derives from the REAL build tags — it
/// is NOT forgeable via <c>-ldflags -X</c> (the version string is). The probe runs
/// once, lazily, and is cached. Tests override via <see cref="OverrideAwg"/> /
/// <see cref="OverrideXhttp"/> (mirrors <see cref="ServerUriParser.NaiveRuntimeAvailable"/>).
/// </para>
/// </summary>
public static class SingBoxFeatures
{
    private static readonly object _gate = new();
    private static bool _probed;
    private static bool _awg;
    private static bool _xhttp;

    /// <summary>Test/override hook for AmneziaWG availability. Non-null short-circuits the probe.</summary>
    internal static bool? OverrideAwg { get; set; }

    /// <summary>Test/override hook for XHTTP availability. Non-null short-circuits the probe.</summary>
    internal static bool? OverrideXhttp { get; set; }

    /// <summary>True when the bundled sing-box was built <c>with_awg</c> (the lx fork).</summary>
    public static bool AwgAvailable => OverrideAwg ?? Probe().awg;

    /// <summary>True when the bundled sing-box was built <c>with_xhttp</c> (the lx fork).</summary>
    public static bool XhttpAvailable => OverrideXhttp ?? Probe().xhttp;

    /// <summary>Drop the cached probe + any overrides (tests only).</summary>
    internal static void ResetForTests()
    {
        lock (_gate)
        {
            _probed = false;
            _awg = false;
            _xhttp = false;
            OverrideAwg = null;
            OverrideXhttp = null;
        }
    }

    private static (bool awg, bool xhttp) Probe()
    {
        if (_probed) return (_awg, _xhttp);
        lock (_gate)
        {
            if (_probed) return (_awg, _xhttp);
            try
            {
                var path = AppPaths.SingBoxExePath;
                if (File.Exists(path))
                {
                    var tags = ReadTagsLine(path);
                    // The Tags line is a comma-separated list, e.g.
                    // "Tags: with_gvisor,with_quic,...,with_xhttp,with_awg".
                    _awg = tags.Contains("with_awg", StringComparison.Ordinal);
                    _xhttp = tags.Contains("with_xhttp", StringComparison.Ordinal);
                }
            }
            catch
            {
                // Any probe failure (missing binary, spawn denied, hang) leaves the
                // safe default: fork protocols stay rejected. Never throws.
                _awg = false;
                _xhttp = false;
            }
            _probed = true;
            return (_awg, _xhttp);
        }
    }

    private static string ReadTagsLine(string path)
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
        var stdout = p.StandardOutput.ReadToEnd();
        if (!p.WaitForExit(5000))
        {
            try { p.Kill(true); } catch { /* best effort */ }
            return string.Empty;
        }
        foreach (var line in stdout.Split('\n'))
            if (line.TrimStart().StartsWith("Tags:", StringComparison.OrdinalIgnoreCase))
                return line;
        return string.Empty;
    }
}
