using System;
using System.Diagnostics;
using System.IO;
using Serilog;

namespace VPNRouter.Core.Services;

/// <summary>
/// Manages zapret (winws.exe) process lifecycle for DPI bypass.
/// Fragments TLS ClientHello to bypass DPI that blocks VPN connections.
/// Windows-only — uses WinDivert driver.
/// </summary>
public class ZapretManager : IDisposable
{
    private readonly ILogger _logger;
    private Process? _process;
    private bool _disposed;

    public bool IsRunning => _process != null && !_process.HasExited;
    public int? Pid => IsRunning ? _process?.Id : null;

    public event Action<string>? OutputReceived;

    public ZapretManager(ILogger? logger = null)
    {
        _logger = logger ?? Log.Logger;
    }

    /// <summary>
    /// Start zapret with the given strategy.
    /// </summary>
    /// <param name="strategy">Predefined strategy name: "multisplit", "fake+multisplit", or "custom"</param>
    /// <param name="customArgs">Custom arguments when strategy="custom"</param>
    /// <param name="targetPort">TCP port to filter (default 443)</param>
    public void Start(string strategy = "multisplit", string? customArgs = null, int targetPort = 443)
    {
        if (IsRunning)
        {
            _logger.Warning("[Zapret] Already running (PID {Pid}), stopping first", Pid);
            Stop();
        }

        var zapretDir = GetZapretDir();
        var exePath = Path.Combine(zapretDir, "winws.exe");

        if (!File.Exists(exePath))
        {
            _logger.Error("[Zapret] winws.exe not found at {Path}", exePath);
            throw new FileNotFoundException($"winws.exe not found at: {exePath}");
        }

        var args = strategy switch
        {
            // Basic strategies
            "multisplit" =>
                $"--wf-tcp={targetPort},8443 --wf-l3=ipv4 " +
                $"--dpi-desync=multisplit --dpi-desync-split-seqovl=2 --dpi-desync-split-pos=2",

            "fake+multisplit" =>
                $"--wf-tcp={targetPort},8443 --wf-l3=ipv4 " +
                $"--dpi-desync=fake,multisplit --dpi-desync-ttl=2 " +
                $"--dpi-desync-split-seqovl=2 --dpi-desync-split-pos=2 " +
                $"--dpi-desync-fake-tls=0x00000000000000000000",

            "fake+disorder" =>
                $"--wf-tcp={targetPort},8443 --wf-l3=ipv4 " +
                $"--dpi-desync=fake,disorder2 --dpi-desync-ttl=2 " +
                $"--dpi-desync-split-pos=1 " +
                $"--dpi-desync-fake-tls=0x00000000000000000000",

            // Flowseal-based strategies — use relative paths (WorkingDirectory=zapretDir)
            "discord+youtube" =>
                "--wf-tcp=80,443 " +
                @"--wf-raw-part=@""windivert.filter\windivert_part.discord_media.txt"" " +
                @"--wf-raw-part=@""windivert.filter\windivert_part.stun.txt"" " +
                @"--wf-raw-part=@""windivert.filter\windivert_part.quic_initial_ietf.txt"" " +
                "--filter-tcp=80 --dpi-desync=fake,fakedsplit --dpi-desync-autottl=2 --dpi-desync-fooling=md5sig --new " +
                "--filter-tcp=443 --dpi-desync=fake,multidisorder --dpi-desync-split-pos=midsld --dpi-desync-repeats=6 --dpi-desync-fooling=badseq,md5sig --new " +
                @"--filter-l7=quic --dpi-desync=fake --dpi-desync-repeats=11 --dpi-desync-fake-quic=""files\quic_initial_www_google_com.bin"" --new " +
                "--filter-l7=stun,discord --dpi-desync=fake --dpi-desync-repeats=2",

            "discord+youtube (aggressive)" =>
                "--wf-tcp=80,443 " +
                @"--wf-raw-part=@""windivert.filter\windivert_part.discord_media.txt"" " +
                @"--wf-raw-part=@""windivert.filter\windivert_part.stun.txt"" " +
                @"--wf-raw-part=@""windivert.filter\windivert_part.quic_initial_ietf.txt"" " +
                "--filter-tcp=80 --dpi-desync=fake,fakedsplit --dpi-desync-autottl=2 --dpi-desync-fooling=md5sig --new " +
                @"--filter-tcp=443 --hostlist=""files\list-youtube.txt"" --dpi-desync=fake,multidisorder --dpi-desync-split-pos=1,midsld --dpi-desync-repeats=11 --dpi-desync-fooling=md5sig --dpi-desync-fake-tls-mod=rnd,dupsid,sni=www.google.com --new " +
                "--filter-tcp=443 --dpi-desync=fake,multidisorder --dpi-desync-split-pos=midsld --dpi-desync-repeats=6 --dpi-desync-fooling=badseq,md5sig --new " +
                @"--filter-l7=quic --hostlist=""files\list-youtube.txt"" --dpi-desync=fake --dpi-desync-repeats=11 --dpi-desync-fake-quic=""files\quic_initial_www_google_com.bin"" --new " +
                "--filter-l7=quic --dpi-desync=fake --dpi-desync-repeats=11 --new " +
                "--filter-l7=stun,discord --dpi-desync=fake --dpi-desync-repeats=2",

            "all services" =>
                "--wf-tcp=80,443 " +
                @"--wf-raw-part=@""windivert.filter\windivert_part.discord_media.txt"" " +
                @"--wf-raw-part=@""windivert.filter\windivert_part.stun.txt"" " +
                @"--wf-raw-part=@""windivert.filter\windivert_part.quic_initial_ietf.txt"" " +
                @"--wf-raw-part=@""windivert.filter\windivert_part.wireguard.txt"" " +
                "--filter-tcp=80 --dpi-desync=fake,fakedsplit --dpi-desync-autottl=2 --dpi-desync-fooling=md5sig --new " +
                @"--filter-tcp=443 --hostlist=""files\list-youtube.txt"" --dpi-desync=fake,multidisorder --dpi-desync-split-pos=1,midsld --dpi-desync-repeats=11 --dpi-desync-fooling=md5sig --dpi-desync-fake-tls-mod=rnd,dupsid,sni=www.google.com --new " +
                "--filter-tcp=443 --dpi-desync=fake,multidisorder --dpi-desync-split-pos=midsld --dpi-desync-repeats=6 --dpi-desync-fooling=badseq,md5sig --new " +
                @"--filter-l7=quic --hostlist=""files\list-youtube.txt"" --dpi-desync=fake --dpi-desync-repeats=11 --dpi-desync-fake-quic=""files\quic_initial_www_google_com.bin"" --new " +
                "--filter-l7=quic --dpi-desync=fake --dpi-desync-repeats=11 --new " +
                "--filter-l7=stun,discord --dpi-desync=fake --dpi-desync-repeats=2",

            "custom" => customArgs ?? "",

            _ => throw new ArgumentException($"Unknown zapret strategy: {strategy}")
        };

        _logger.Information("[Zapret] Starting with strategy '{Strategy}'", strategy);
        _logger.Information("[Zapret] WorkingDir: {Dir}", zapretDir);
        _logger.Information("[Zapret] Args: {Args}", args);

        // Verify critical files exist for Flowseal strategies
        if (strategy.Contains("discord") || strategy.Contains("all"))
        {
            var filterDir = Path.Combine(zapretDir, "windivert.filter");
            var filesDir = Path.Combine(zapretDir, "files");
            if (!Directory.Exists(filterDir))
                _logger.Warning("[Zapret] Filter dir missing: {Dir}", filterDir);
            if (!Directory.Exists(filesDir))
                _logger.Warning("[Zapret] Files dir missing: {Dir}", filesDir);
        }

        // Set error mode to suppress system error dialogs (missing DLL, etc.)
        // so they don't block the UI with modal popups.
        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = args,
            WorkingDirectory = zapretDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        // Suppress WER (Windows Error Reporting) dialog for child process
        psi.Environment["__COMPAT_LAYER"] = "RUNASINVOKER";

        _process = Process.Start(psi);
        if (_process == null)
        {
            _logger.Error("[Zapret] Failed to start process");
            throw new InvalidOperationException("Failed to start winws.exe");
        }

        _process.EnableRaisingEvents = true;
        _process.Exited += (_, _) =>
        {
            _logger.Warning("[Zapret] Process exited (exit code: {Code})", _process?.ExitCode);
        };

        // Capture output for UI log
        _process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                _logger.Debug("[Zapret] {Line}", e.Data);
                OutputReceived?.Invoke(e.Data);
            }
        };
        _process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                _logger.Debug("[Zapret] {Line}", e.Data);
                OutputReceived?.Invoke(e.Data);
            }
        };
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        _logger.Information("[Zapret] Started (PID {Pid})", _process.Id);
    }

    public void Stop()
    {
        if (_process == null || _process.HasExited)
        {
            _process = null;
            return;
        }

        _logger.Information("[Zapret] Stopping (PID {Pid})", _process.Id);

        try
        {
            _process.Kill(entireProcessTree: true);
            _process.WaitForExit(3000);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[Zapret] Error stopping");
        }
        finally
        {
            _process?.Dispose();
            _process = null;
            _logger.Information("[Zapret] Stopped");
        }
    }

    private static string GetZapretDir()
    {
        // Look for zapret binaries in app directory
        var appDir = AppContext.BaseDirectory;
        var zapretDir = Path.Combine(appDir, "zapret");
        if (Directory.Exists(zapretDir) && File.Exists(Path.Combine(zapretDir, "winws.exe")))
            return zapretDir;

        // Fallback: ProgramData
        var pdDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "VPNRouter", "zapret");
        if (Directory.Exists(pdDir))
            return pdDir;

        return zapretDir; // Will fail with FileNotFound
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
