using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Linq;
using System.Collections.Generic;
using System.Text;
using Serilog;

namespace VPNRouter.Core.Services;

public class TgProxyManager : IDisposable
{
    private readonly ILogger _logger;
    private readonly IProcessRunner _runner;
    private IProcessHandle? _handle;
    private readonly StringBuilder _capturedStderr = new();
    private readonly object _stderrGate = new();
    private readonly object _lifecycleGate = new();
    private bool _disposed;
    private string _activeSecret = string.Empty;

    internal static IProcessRunner Runner { get; set; } = new ProcessRunner();

    public bool IsRunning
    {
        get
        {
            var handle = Volatile.Read(ref _handle);
            if (handle == null) return false;
            try
            {
                return !handle.HasExited;
            }
            catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
            {
                _logger.Debug(ex, "[TgProxy] Handle HasExited query failed; assuming process still active");
                return true;
            }
        }
    }

    public int? Pid
    {
        get
        {
            var handle = Volatile.Read(ref _handle);
            if (handle == null) return null;
            try
            {
                return !handle.HasExited ? handle.Pid : null;
            }
            catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
            {
                try { return handle.Pid; }
                catch { return null; }
            }
        }
    }

    public string? LastStats { get; private set; }

    public event Action<string>? StatsUpdated;

    public TgProxyManager(ILogger? logger = null, IProcessRunner? runner = null)
    {
        _logger = logger ?? Log.Logger;
        _runner = runner ?? Runner;
    }

    public void Start(int port, string secret, bool verbose = false)
    {
        lock (_lifecycleGate)
        {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_handle != null)
        {
            if (!_handle.HasExited)
                _logger.Warning("[TgProxy] Already running (PID {Pid}), stopping first", _handle.Pid);
            Stop();
            if (_handle != null)
            {
                throw new InvalidOperationException(
                    $"Cannot start tg-ws-proxy: prior instance (PID {_handle.Pid}) could not be stopped.");
            }
        }

        if (!File.Exists(TgProxyUpdater.PythonExePath))
            throw new FileNotFoundException("Python not found. Download tg-ws-proxy first.");

        if (!Directory.Exists(TgProxyUpdater.ProxySourceDir))
            throw new FileNotFoundException("Proxy source not found. Download tg-ws-proxy first.");

        if (!IsPortAvailable(port))
        {
            var ownerHint = PortOwnerResolver.TryResolve(port);
            _logger.Warning(
                "[TgProxy] Port {Port} pre-flight probe: BUSY (owner hint: {Owner})",
                port, ownerHint ?? "<unknown>");
            throw new TgProxyPortConflictException(port, ownerHint);
        }

        var args = $"-m proxy.tg_ws_proxy --port {port} --host 127.0.0.1 --secret {secret}";
        if (verbose) args += " --verbose";

        var redactedArgs = RedactSecretInArgs(args);

        var argv = new List<string>
        {
            "-m", "proxy.tg_ws_proxy",
            "--port", port.ToString(),
            "--host", "127.0.0.1",
            "--secret", secret,
        };
        if (verbose) argv.Add("--verbose");

        var request = new ProcessRequest(
            ExecutablePath: TgProxyUpdater.PythonExePath,
            Arguments: argv,
            WorkingDirectory: TgProxyUpdater.TgProxyDir,
            CaptureStdout: true,
            CaptureStderr: true);

        _logger.Information(
            "[TgProxy] Spawn ProcessStartInfo: FileName={FileName}, Arguments={Arguments}, WorkingDirectory={WorkingDirectory}, CreateNoWindow={CreateNoWindow}, UseShellExecute={UseShellExecute}",
            request.ExecutablePath, redactedArgs, request.WorkingDirectory, true, false);

        lock (_stderrGate) _capturedStderr.Clear();
        _activeSecret = secret;

        try
        {
            _handle = _runner.Start(request);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[TgProxy] Failed to start process");
            throw new InvalidOperationException("Failed to start tg-ws-proxy", ex);
        }

        var startedHandle = _handle;
        startedHandle.Exited += (_, code) =>
        {
            _logger.Warning("[TgProxy] Process exited (exit code: {Code})", code);
        };

        startedHandle.OutputLine += OnOutputLineHandler;
        startedHandle.ErrorLine += OnErrorLineHandler;

        _logger.Information("[TgProxy] Spawned PID {Pid}", startedHandle.Pid);

        using var probeCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(2000));
        try
        {
            var exitCode = startedHandle.WaitForExitAsync(probeCts.Token)
                .GetAwaiter().GetResult();

                _logger.Error(
                    "[TgProxy] Process exited within 2s of spawn (PID {Pid}, ExitCode {ExitCode}) — likely startup failure",
                    startedHandle.Pid, exitCode);

                string stderrTail;
                lock (_stderrGate) stderrTail = _capturedStderr.ToString();

            if (!string.IsNullOrWhiteSpace(stderrTail))
            {
                _logger.Error(
                    "[TgProxy] StandardError tail (PID {Pid}): {Stderr}",
                    startedHandle.Pid, stderrTail.Trim());
            }

            startedHandle.Dispose();
            _handle = null;
            _activeSecret = string.Empty;
            throw new InvalidOperationException(
                $"tg-ws-proxy exited immediately (exit code {exitCode}). Use Update TgProxy to repair it.");
        }
        catch (OperationCanceledException)
        {
            _logger.Information(
                "[TgProxy] Process still alive after 2s probe (PID {Pid})",
                startedHandle.Pid);
        }
        }
    }

    internal static string RedactSecretInArgs(string args)
    {
        if (string.IsNullOrEmpty(args)) return args;
        return System.Text.RegularExpressions.Regex.Replace(
            args, @"--secret\s+\S+", "--secret REDACTED");
    }

    private void OnOutputLineHandler(object? sender, string line)
    {
        if (string.IsNullOrEmpty(line)) return;

        if (line.Contains("stats:"))
        {
            LastStats = line;
            StatsUpdated?.Invoke(line);
        }
    }

    private void OnErrorLineHandler(object? sender, string line)
    {
        if (string.IsNullOrEmpty(line)) return;

        const int MaxStderrBuffer = 16 * 1024;
        lock (_stderrGate)
        {
            if (_capturedStderr.Length < MaxStderrBuffer)
            {
                _capturedStderr.AppendLine(RedactSensitiveOutput(line, _activeSecret));
            }
        }

        if (line.Contains("stats:"))
        {
            LastStats = line;
            StatsUpdated?.Invoke(line);
        }
    }

    internal static string RedactSensitiveOutput(string text, string? secret)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var redacted = string.IsNullOrEmpty(secret)
            ? text
            : text.Replace(secret, "REDACTED", StringComparison.Ordinal);
        return System.Text.RegularExpressions.Regex.Replace(
            redacted, @"(?i)(secret(?:=|:\s*))[^\s&]+", "$1REDACTED");
    }

    public static bool IsPortAvailable(int port)
    {
        if (port <= 0 || port > 65535) return false;

        TcpListener? listener = null;
        try
        {
            listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        catch
        {
            return false;
        }
        finally
        {
            try { listener?.Stop(); } catch { }
        }
    }

    public static string BuildProxyLink(string host, int port, string secret)
    {
        return $"tg://proxy?server={host}&port={port}&secret=dd{secret}";
    }

    public static void OpenInTelegram(string host, int port, string secret)
    {
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(secret) ||
            host.Any(c => c is '\r' or '\n' or '"' or '\'' or ' ' || char.IsControl(c)) ||
            secret.Any(c => c is '\r' or '\n' or '"' or '\'' or ' ' || char.IsControl(c)))
        {
            Log.Warning("[TgProxy] Refusing to open tg:// link with invalid host or secret parameters");
            return;
        }

        if (port is < 1 or > 65535)
        {
            Log.Warning("[TgProxy] Refusing to open tg:// link with invalid port {Port}", port);
            return;
        }

        var url = BuildProxyLink(host, port, secret);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, "tg", StringComparison.OrdinalIgnoreCase))
        {
            Log.Warning("[TgProxy] Refusing to open invalid or non-tg URL: {Url}", CanaryPolicy.RedactUrl(url));
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[TgProxy] Failed to open tg:// link: {Url}", CanaryPolicy.RedactUrl(url));
        }
    }

    public static bool IsTelegramSchemeRegistered()
    {
        if (!OperatingSystem.IsWindows()) return true;

        try
        {
#pragma warning disable CA1416
            using var hkcrTg = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey("tg");
            if (hkcrTg != null) return true;

            using var hkcuTg = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Classes\tg");
            return hkcuTg != null;
#pragma warning restore CA1416
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[TgProxy] tg:// scheme probe failed (assume registered)");
            return true;
        }
    }

    public void Stop()
    {
        lock (_lifecycleGate)
        {
            if (_handle == null)
            {
                return;
            }

            bool alreadyExited = false;
            try
            {
                alreadyExited = _handle.HasExited;
            }
            catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
            {
                alreadyExited = false;
            }

            if (alreadyExited)
            {
                try { _handle.Dispose(); } catch { }
                _handle = null;
                _activeSecret = string.Empty;
                return;
            }

            var handle = _handle;
            _logger.Information("[TgProxy] Stopping (PID {Pid})", handle.Pid);

            try
            {
                handle.SuppressExitedEvent();
                handle.Kill(entireProcessTree: true);

                using var stopCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(3000));
                try
                {
                    handle.WaitForExitAsync(stopCts.Token).GetAwaiter().GetResult();
                }
                catch (OperationCanceledException)
                {
                    _logger.Warning("[TgProxy] WaitForExitAsync timeout (3s) — process did not exit");
                }
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "[TgProxy] Error stopping (PID {Pid})", handle.Pid);
            }

            bool confirmedExited = false;
            try
            {
                confirmedExited = handle.HasExited;
            }
            catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
            {
                confirmedExited = false;
            }

            if (confirmedExited)
            {
                try { handle.Dispose(); } catch { }
                _handle = null;
                _activeSecret = string.Empty;
                _logger.Information("[TgProxy] Stopped");
            }
            else
            {
                _logger.Error(
                    "[TgProxy] Stop failed to confirm exit for PID {Pid}; retaining handle and secret",
                    handle.Pid);
            }
        }
    }

    public static bool IsAnyRunning(int port = 1443)
    {
        try
        {
            var listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
            return listeners.Any(l => l.Port == port);
        }
        catch { return false; }
    }

    public void Dispose()
    {
        lock (_lifecycleGate)
        {
            if (_disposed) return;
            Stop();
            if (_handle == null)
            {
                _disposed = true;
            }
        }
    }
}
