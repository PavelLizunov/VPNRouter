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

    internal static bool EmbeddedCore { get; set; } = OperatingSystem.IsAndroid();

    internal static bool? OverrideAwg { get; set; }

    internal static bool? OverrideXhttp { get; set; }

    public static bool AwgAvailable => OverrideAwg ?? Probe().awg;

    public static bool XhttpAvailable => OverrideXhttp ?? Probe().xhttp;

    public static void Prewarm()
    {
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
            EmbeddedCore = OperatingSystem.IsAndroid();
            OverrideAwg = null;
            OverrideXhttp = null;
        }
    }

    private static (bool awg, bool xhttp) Probe()
    {
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
                    var tags = ReadTagsLine(path);
                    _awg = tags.Contains("with_awg", StringComparison.Ordinal);
                    _xhttp = tags.Contains("with_xhttp", StringComparison.Ordinal);
                }
            }
            catch
            {
                // Any probe failure keeps the safe default: fork protocols stay rejected.
                _awg = false;
                _xhttp = false;
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
        foreach (var line in stdout.Split('\n'))
            if (line.TrimStart().StartsWith("Tags:", StringComparison.OrdinalIgnoreCase))
                return line;
        return string.Empty;
    }
}
