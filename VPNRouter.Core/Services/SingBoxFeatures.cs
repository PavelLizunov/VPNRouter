using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

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

    /// <summary>
    /// P2 (2026-07-10): fire the one-time capability probe on a BACKGROUND thread
    /// so the first <see cref="AwgAvailable"/> / <see cref="XhttpAvailable"/> read
    /// doesn't pay the ≤5s <c>sing-box version</c> spawn on the UI thread — that
    /// path is hit synchronously on an <c>awg://</c> manual paste
    /// (<c>SmpToggleConnectAsync</c> → <c>TryApplyVless</c> → parser gate). Idempotent
    /// (the probe caches), best-effort (a spawn failure leaves the safe default),
    /// no-op when an override is set (tests). Call once at app startup.
    /// </summary>
    public static void Prewarm()
    {
        if (OverrideAwg.HasValue && OverrideXhttp.HasValue) return;
        _ = Task.Run(() => { try { _ = Probe(); } catch { /* best-effort warm */ } });
    }

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
                var path = ResolveBinaryPath();
                if (path != null && File.Exists(path))
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

    private static string ResolveBinaryPath()
    {
        // Probe the BUNDLED binary (AppContext.BaseDirectory) — that's what
        // StartupPipeline.DeploySingBoxBinary copies to the runtime path and
        // actually runs. Reading the runtime path (AppPaths.SingBoxExePath) can
        // be stale BEFORE the deploy phase, or hold a leftover from a previous
        // install, caching the wrong capability for the whole process. Fall back
        // to the runtime path when the bundle isn't resolvable (dev / tests).
        try
        {
            var bundled = Path.Combine(AppContext.BaseDirectory,
                OperatingSystem.IsWindows() ? "sing-box.exe" : "sing-box");
            if (File.Exists(bundled)) return bundled;
        }
        catch { /* AppContext.BaseDirectory unavailable -> fall back */ }
        return AppPaths.SingBoxExePath;
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
        // Drain BOTH pipes concurrently BEFORE WaitForExit. Reading stdout to
        // end first while stderr stays undrained deadlocks if the child fills
        // the stderr pipe buffer (~4KB) — exactly the anti-pattern already fixed
        // in MacProcessScanner / UpdateChecker.RunWithCapture. The async reads
        // let the 5s ceiling actually bound the call; a hang here would hold
        // _gate forever and wedge every later AwgAvailable/XhttpAvailable reader.
        var outTask = p.StandardOutput.ReadToEndAsync();
        var errTask = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(5000))
        {
            try { p.Kill(true); } catch { /* best effort */ }
            return string.Empty;
        }
        string stdout;
        try
        {
            stdout = outTask.GetAwaiter().GetResult();
            _ = errTask.GetAwaiter().GetResult(); // drained + observed, discarded
        }
        catch { return string.Empty; }
        foreach (var line in stdout.Split('\n'))
            if (line.TrimStart().StartsWith("Tags:", StringComparison.OrdinalIgnoreCase))
                return line;
        return string.Empty;
    }
}
