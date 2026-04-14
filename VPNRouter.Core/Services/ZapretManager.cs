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
            // === Basic strategies (TCP only, for simple DPI) ===
            "multisplit" =>
                $"--wf-tcp={targetPort},8443 --wf-l3=ipv4 " +
                $"--dpi-desync=multisplit --dpi-desync-split-seqovl=2 --dpi-desync-split-pos=2",

            "fake+multisplit" =>
                $"--wf-tcp={targetPort},8443 --wf-l3=ipv4 " +
                $"--dpi-desync=fake,multisplit --dpi-desync-ttl=2 " +
                $"--dpi-desync-split-seqovl=2 --dpi-desync-split-pos=2 " +
                $"--dpi-desync-fake-tls=0x00000000000000000000",

            // === Full strategies (TCP + UDP, Discord voice + YouTube + all services) ===
            // NO hostlist filtering — process ALL traffic on target ports (reliable)
            // Multi-profile: different desync methods for TCP vs UDP vs QUIC
            // IMPORTANT: all TCP fake strategies use --dpi-desync-autottl=2 so fakes
            // expire AFTER the DPI but BEFORE the real server (prevents ERR_SSL_PROTOCOL_ERROR)

            // General — multisplit (proven working) + Discord UDP
            "general" =>
                "--wf-tcp=80,443,2053,2083,2087,2096,8443 " +
                "--wf-udp=443,19294-19344,50000-50100 " +
                // QUIC on UDP 443
                @"--filter-udp=443 --dpi-desync=fake --dpi-desync-repeats=6 --dpi-desync-fake-quic=""files\quic_initial_www_google_com.bin"" --new " +
                // Discord voice (STUN/RTC) on UDP 19294-19344, 50000-50100
                "--filter-udp=19294-19344,50000-50100 --filter-l7=discord,stun " +
                "--dpi-desync=fake --dpi-desync-repeats=6 --new " +
                // Discord CDN on TCP alt-ports
                "--filter-tcp=2053,2083,2087,2096,8443 " +
                "--dpi-desync=fake,multisplit --dpi-desync-autottl=2 --dpi-desync-split-seqovl=2 --dpi-desync-split-pos=2 --dpi-desync-repeats=6 --new " +
                // All TCP 80,443 — same as working multisplit (no fake = safe)
                "--filter-tcp=80,443 " +
                "--dpi-desync=multisplit --dpi-desync-split-seqovl=2 --dpi-desync-split-pos=2",

            // General ALT — fake,fakedsplit + autottl + ts fooling + Discord UDP
            "general (ALT)" =>
                "--wf-tcp=80,443,2053,2083,2087,2096,8443 " +
                "--wf-udp=443,19294-19344,50000-50100 " +
                // QUIC
                @"--filter-udp=443 --dpi-desync=fake --dpi-desync-repeats=6 --dpi-desync-fake-quic=""files\quic_initial_www_google_com.bin"" --new " +
                // Discord voice
                "--filter-udp=19294-19344,50000-50100 --filter-l7=discord,stun " +
                "--dpi-desync=fake --dpi-desync-repeats=6 --new " +
                // Discord CDN
                "--filter-tcp=2053,2083,2087,2096,8443 " +
                "--dpi-desync=fake,fakedsplit --dpi-desync-autottl=2 --dpi-desync-repeats=6 --dpi-desync-fooling=ts --dpi-desync-fakedsplit-pattern=0x00 --new " +
                // All TCP 80,443 — autottl prevents fakes reaching server
                "--filter-tcp=80,443 " +
                "--dpi-desync=fake,fakedsplit --dpi-desync-autottl=2 --dpi-desync-repeats=6 --dpi-desync-fooling=ts --dpi-desync-fakedsplit-pattern=0x00",

            // General ALT2 — fake,multidisorder + autottl + md5sig + Discord UDP
            "general (ALT2)" =>
                "--wf-tcp=80,443,2053,2083,2087,2096,8443 " +
                "--wf-udp=443,19294-19344,50000-50100 " +
                // QUIC — high repeats
                @"--filter-udp=443 --dpi-desync=fake --dpi-desync-repeats=11 --dpi-desync-fake-quic=""files\quic_initial_www_google_com.bin"" --new " +
                // Discord voice — high repeats
                "--filter-udp=19294-19344,50000-50100 --filter-l7=discord,stun " +
                "--dpi-desync=fake --dpi-desync-repeats=11 --new " +
                // Discord CDN
                "--filter-tcp=2053,2083,2087,2096,8443 " +
                "--dpi-desync=fake,multidisorder --dpi-desync-autottl=2 --dpi-desync-split-pos=1,midsld --dpi-desync-repeats=11 --dpi-desync-fooling=md5sig --new " +
                // All TCP 80,443 — autottl + badseq,md5sig
                "--filter-tcp=80,443 " +
                "--dpi-desync=fake,multidisorder --dpi-desync-autottl=2 --dpi-desync-split-pos=midsld --dpi-desync-repeats=6 --dpi-desync-fooling=badseq,md5sig",

            // General ALT3 — fake+multisplit + autottl + ts + Discord UDP
            "general (ALT3)" =>
                "--wf-tcp=80,443,2053,2083,2087,2096,8443 " +
                "--wf-udp=443,19294-19344,50000-50100 " +
                // QUIC
                @"--filter-udp=443 --dpi-desync=fake --dpi-desync-repeats=11 --dpi-desync-fake-quic=""files\quic_initial_www_google_com.bin"" --new " +
                // Discord voice
                "--filter-udp=19294-19344,50000-50100 --filter-l7=discord,stun " +
                "--dpi-desync=fake --dpi-desync-repeats=6 --new " +
                // Discord CDN
                "--filter-tcp=2053,2083,2087,2096,8443 " +
                "--dpi-desync=fake,multisplit --dpi-desync-autottl=2 --dpi-desync-split-seqovl=2 --dpi-desync-split-pos=1 --dpi-desync-fooling=ts --dpi-desync-repeats=8 --new " +
                // All TCP 80,443 — autottl + ts
                "--filter-tcp=80,443 " +
                "--dpi-desync=fake,multisplit --dpi-desync-autottl=2 --dpi-desync-split-seqovl=2 --dpi-desync-split-pos=1 --dpi-desync-fooling=ts --dpi-desync-repeats=8",

            "custom" => customArgs ?? "",

            _ => throw new ArgumentException($"Unknown zapret strategy: {strategy}")
        };

        _logger.Information("[Zapret] Starting with strategy '{Strategy}'", strategy);
        _logger.Information("[Zapret] WorkingDir: {Dir}", zapretDir);
        _logger.Information("[Zapret] Args: {Args}", args);

        // Verify critical files exist for Flowseal strategies
        if (strategy.StartsWith("general"))
        {
            var filesDir = Path.Combine(zapretDir, "files");
            if (!Directory.Exists(filesDir))
                _logger.Warning("[Zapret] Files dir missing: {Dir}", filesDir);
            else
            {
                var requiredFiles = new[]
                {
                    "list-general.txt", "list-google.txt",
                    "quic_initial_www_google_com.bin",
                    "tls_clienthello_www_google_com.bin",
                    "tls_clienthello_max_ru.bin", "stun.bin"
                };
                foreach (var f in requiredFiles)
                {
                    var path = Path.Combine(filesDir, f);
                    if (!File.Exists(path))
                        _logger.Warning("[Zapret] Required file missing: {File}", path);
                }
            }
        }

        // Set error mode to suppress system error dialogs (missing DLL, etc.)
        // so they don't block the UI with modal popups.
        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = args,
            WorkingDirectory = zapretDir,
            UseShellExecute = false,
            // Cygwin apps (winws.exe) need a console to initialize properly.
            // CreateNoWindow=false creates a hidden console window.
            CreateNoWindow = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };

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
