using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

public class SlipstreamException : Exception
{
    public SlipstreamException(string message) : base(message) { }
    public SlipstreamException(string message, Exception inner) : base(message, inner) { }
}

public sealed class SlipstreamPortConflictException : SlipstreamException
{
    public int Port { get; }
    public string? OwnerProcessHint { get; }
    public SlipstreamPortConflictException(int port, string? ownerHint)
        : base($"Local port {port} is already in use" +
               (ownerHint != null ? $" by {ownerHint}" : "") +
               " — slipstream-client can't bind it. Stop the other process or pick another port.")
    {
        Port = port;
        OwnerProcessHint = ownerHint;
    }
}

public class SlipstreamManager : IDisposable
{
    public const int DefaultLocalPort = 7001;

    private readonly ILogger _logger;
    private readonly IProcessRunner _runner;
    private IProcessHandle? _handle;
    private bool _disposed;

    internal static IProcessRunner Runner { get; set; } = new ProcessRunner();

    internal int StartupProbeMs { get; set; } = 2000;

    public bool IsRunning => _handle != null && !_handle.HasExited;
    public int? Pid => IsRunning ? _handle?.Pid : null;
    public int LocalPort { get; private set; }

    public SlipstreamManager(ILogger? logger = null, IProcessRunner? runner = null)
    {
        _logger = logger ?? Log.Logger;
        _runner = runner ?? Runner;
    }

    public void Start(VlessServerEntry entry, int localPort = DefaultLocalPort)
    {
        if (entry == null) throw new ArgumentNullException(nameof(entry));
        if (!string.Equals(entry.Protocol, "dns-tunnel", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(
                "SlipstreamManager.Start requires a dns-tunnel server",
                nameof(entry));

        if (IsRunning)
        {
            _logger.Warning("[Slipstream] Already running (PID {Pid}), stopping first", Pid);
            Stop();
        }

        if (string.IsNullOrWhiteSpace(entry.DnsDomain))
            throw new SlipstreamException("dns-tunnel server has no domain");
        List<string> resolvers;
        if (entry.DnsUseSystemResolver)
        {
            var osResolvers = ReadOsResolvers();
            resolvers = SelectResolvers(entry, osResolvers);
            _logger.Information(
                "[Slipstream] System-resolver mode: {OsCount} OS resolver(s) discovered; " +
                "using {Used} ({Source})",
                osResolvers.Count, resolvers.Count, osResolvers.Count > 0 ? "OS" : "link fallback");
        }
        else
        {
            resolvers = SelectResolvers(entry, Array.Empty<string>());
        }
        if (resolvers.Count == 0)
            throw new SlipstreamException(entry.DnsUseSystemResolver
                ? "dns-tunnel server requests the system resolver but none could be discovered and no fallback resolvers are configured"
                : "dns-tunnel server has no resolvers");
        if (string.IsNullOrWhiteSpace(entry.DnsLeafCertPem))
            throw new SlipstreamException("dns-tunnel server has no leaf certificate (PEM)");

        EnsureBinaryProvisioned(
            AppPaths.SlipstreamExePath, AppPaths.SlipstreamBundledExePath,
            AppPaths.SlipstreamBinDir, _logger);
        if (!File.Exists(AppPaths.SlipstreamExePath))
            throw new SlipstreamException(
                $"slipstream-client not found at {AppPaths.SlipstreamExePath}. " +
                "It ships bundled with the Windows installer; for a dev build, build it " +
                "from Mygod/slipstream-rust and place it there. (DNS-tunnel is Windows/Linux only.)");

        if (!string.IsNullOrWhiteSpace(entry.DnsLeafFingerprint))
        {
            var actual = ComputeLeafSha256Hex(entry.DnsLeafCertPem);
            var expected = NormalizeHex(entry.DnsLeafFingerprint);
            if (actual == null)
            {
                _logger.Warning(
                    "[Slipstream] Could not compute leaf fingerprint from PEM — skipping cross-check " +
                    "(slipstream-client --cert remains the authority)");
            }
            else if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                throw new SlipstreamException(
                    "dns-tunnel leaf fingerprint mismatch — " +
                    "refusing to connect (tampered or corrupt profile)");
            }
        }

        if (!IsPortAvailable(localPort))
        {
            var ownerHint = PortOwnerResolver.TryResolve(localPort);
            _logger.Warning("[Slipstream] Port {Port} pre-flight: BUSY (owner: {Owner})",
                localPort, ownerHint ?? "<unknown>");
            throw new SlipstreamPortConflictException(localPort, ownerHint);
        }

        try
        {
            Directory.CreateDirectory(AppPaths.SlipstreamDir);
            File.WriteAllText(AppPaths.SlipstreamActiveCertPath, entry.DnsLeafCertPem);
        }
        catch (Exception ex)
        {
            throw new SlipstreamException("Failed to write the active leaf cert to disk", ex);
        }

        var argv = new List<string>
        {
            "--cert", AppPaths.SlipstreamActiveCertPath,
            "-d", entry.DnsDomain.Trim(),
            "-l", localPort.ToString(),
            "--tcp-listen-host", "127.0.0.1",
        };
        foreach (var r in resolvers) { argv.Add("-r"); argv.Add(r); }

        var authoritative = (entry.DnsAuthoritative ?? new List<string>())
            .Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim()).ToList();
        foreach (var a in authoritative) { argv.Add("--authoritative"); argv.Add(a); }

        var cc = (entry.CongestionControl ?? "").Trim().ToLowerInvariant();
        if (cc == "bbr" || cc == "dcubic") { argv.Add("-c"); argv.Add(cc); }
        argv.Add("-t"); argv.Add("2000");

        argv.Add("--path-stats");

        var request = new ProcessRequest(
            ExecutablePath: AppPaths.SlipstreamExePath,
            Arguments: argv,
            WorkingDirectory: AppPaths.SlipstreamBinDir,
            EnvironmentOverrides: new Dictionary<string, string>
            {
                ["RUST_LOG"] = "info",
                ["RUST_BACKTRACE"] = "1",
            },
            CaptureStdout: true,
            CaptureStderr: true);

        RotateTransportLog();

        try
        {
            _handle = _runner.Start(request);
        }
        catch (Exception ex)
        {
            _logger.Error("[Slipstream] Failed to start process ({Type})", ex.GetType().Name);
            throw new SlipstreamException("Failed to start slipstream-client");
        }

        LocalPort = localPort;
        var startedHandle = _handle;
        startedHandle.Exited += (_, code) =>
        {
            _logger.Warning("[Slipstream] Process exited (exit code: {Code})", code);
            AppendTransportLog($"--- process exited (code {code}) ---");
        };
        AppendTransportLog($"Started PID {startedHandle.Pid}; resolvers={resolvers.Count}, authoritative={authoritative.Count}");
        startedHandle.OutputLine += OnTransportLine;
        startedHandle.ErrorLine += OnTransportLine;
        _logger.Information("[Slipstream] Spawned PID {Pid} on loopback port {Port}; resolvers={N}, authoritative={M}",
            startedHandle.Pid, localPort, resolvers.Count, authoritative.Count);

        using var probeCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(StartupProbeMs));
        int? earlyExitCode = null;
        try
        {
            earlyExitCode = startedHandle.WaitForExitAsync(probeCts.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            _logger.Information("[Slipstream] Alive after {Ms}ms probe (PID {Pid})",
                StartupProbeMs, startedHandle.Pid);
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "[Slipstream] Post-spawn probe raised");
        }

        if (earlyExitCode.HasValue)
        {
            _logger.Error("[Slipstream] Exited within {Ms}ms of spawn (code {Code})", StartupProbeMs, earlyExitCode);
            Stop();
            throw new SlipstreamException($"slipstream-client exited immediately (code {earlyExitCode}).");
        }
    }

    private void OnTransportLine(object? sender, string line)
    {
        if (string.IsNullOrEmpty(line)) return;
        if (line.Contains("ERROR", StringComparison.OrdinalIgnoreCase))
        {
            _logger.Error("[Slipstream] Transport reported an error");
            AppendTransportLog("Transport error");
        }
        else if (line.Contains("WARN", StringComparison.OrdinalIgnoreCase))
        {
            _logger.Warning("[Slipstream] Transport reported a warning");
            AppendTransportLog("Transport warning");
        }
    }

    private const long TransportLogMaxBytes = 8 * 1024 * 1024;
    private static readonly object _transportLogGate = new();

    private static void RotateTransportLog()
    {
        try
        {
            lock (_transportLogGate)
            {
                var path = AppPaths.SlipstreamLogPath;
                if (!File.Exists(path)) return;
                var prev = path + ".prev";
                try { if (File.Exists(prev)) File.Delete(prev); } catch { }
                File.Move(path, prev);
            }
        }
        catch { }
    }

    private static void AppendTransportLog(string line)
    {
        if (string.IsNullOrEmpty(line)) return;
        try
        {
            lock (_transportLogGate)
            {
                var path = AppPaths.SlipstreamLogPath;
                var fi = new FileInfo(path);
                if (fi.Exists && fi.Length > TransportLogMaxBytes) return;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.AppendAllText(path,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {line}{Environment.NewLine}");
            }
        }
        catch { }
    }

    public void Stop()
    {
        var handle = _handle;
        if (handle == null) { CleanActiveCert(); return; }

        if (handle.HasExited)
        {
            try { handle.Dispose(); } catch { }
            _handle = null;
            CleanActiveCert();
            return;
        }

        _logger.Information("[Slipstream] Stopping (PID {Pid})", handle.Pid);
        try
        {
            handle.SuppressExitedEvent();
            handle.Kill(entireProcessTree: true);
            using var stopCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(3000));
            try { handle.WaitForExitAsync(stopCts.Token).GetAwaiter().GetResult(); }
            catch (OperationCanceledException)
            {
                _logger.Debug("[Slipstream] WaitForExitAsync timeout (3s) — proceeding to dispose");
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[Slipstream] Error stopping");
        }
        finally
        {
            try { handle.Dispose(); } catch { }
            _handle = null;
            CleanActiveCert();
            LocalPort = 0;
            _logger.Information("[Slipstream] Stopped");
        }
    }

    private void CleanActiveCert()
    {
        try
        {
            if (File.Exists(AppPaths.SlipstreamActiveCertPath))
                File.Delete(AppPaths.SlipstreamActiveCertPath);
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "[Slipstream] active cert cleanup failed");
        }
    }

    internal static bool EnsureBinaryProvisioned(
        string targetExePath, string? bundledExePath, string targetBinDir, ILogger? logger)
    {
        var haveBundle = !string.IsNullOrEmpty(bundledExePath) && File.Exists(bundledExePath);
        var haveTarget = File.Exists(targetExePath);
        if (!haveBundle) return haveTarget;

        var needCopy = !haveTarget;
        if (!needCopy)
        {
            try
            {
                needCopy = new FileInfo(bundledExePath!).Length != new FileInfo(targetExePath).Length;
            }
            catch (Exception ex)
            {
                logger?.Warning(ex, "[Slipstream] Could not compare runtime vs bundled binary — re-promoting");
                needCopy = true;
            }
        }
        if (!needCopy) return true;

        try
        {
            Directory.CreateDirectory(targetBinDir);
            File.Copy(bundledExePath!, targetExePath, overwrite: true);
            logger?.Information("[Slipstream] Promoted bundled binary {Src} -> {Dst} ({Len} bytes, app {Ver})",
                bundledExePath, targetExePath, new FileInfo(targetExePath).Length, AppVersion.Version);
        }
        catch (Exception ex)
        {
            logger?.Warning(ex, "[Slipstream] Could not promote bundled binary to {Dst}", targetExePath);
        }
        return File.Exists(targetExePath);
    }

    internal static List<string> SelectResolvers(VlessServerEntry entry, IReadOnlyList<string> systemResolvers)
    {
        var literals = (entry.DnsResolvers ?? new List<string>())
            .Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r.Trim()).ToList();
        if (!entry.DnsUseSystemResolver)
            return literals;
        var sys = (systemResolvers ?? Array.Empty<string>())
            .Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return sys.Count > 0 ? sys : literals;
    }

    internal static List<string> ReadOsResolvers()
    {
        var result = new List<string>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                IPInterfaceProperties props;
                try { props = ni.GetIPProperties(); } catch { continue; }
                foreach (var dns in props.DnsAddresses)
                {
                    if (dns.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (IPAddress.IsLoopback(dns)) continue;
                    var ip = dns.ToString();
                    if (ip.StartsWith("169.254", StringComparison.Ordinal)) continue;
                    var ep = ip + ":53";
                    if (!result.Contains(ep)) result.Add(ep);
                }
            }
        }
        catch { }
        return result;
    }

    internal static string? ComputeLeafSha256Hex(string pem)
    {
        try
        {
            const string begin = "-----BEGIN CERTIFICATE-----";
            const string end = "-----END CERTIFICATE-----";
            var bi = pem.IndexOf(begin, StringComparison.Ordinal);
            var ei = pem.IndexOf(end, StringComparison.Ordinal);
            if (bi < 0 || ei < 0 || ei <= bi) return null;
            var body = pem.Substring(bi + begin.Length, ei - bi - begin.Length);
            body = new string(body.Where(c => !char.IsWhiteSpace(c)).ToArray());
            if (body.Length == 0) return null;
            var der = Convert.FromBase64String(body);
            return Convert.ToHexStringLower(SHA256.HashData(der));
        }
        catch
        {
            return null;
        }
    }

    internal static string NormalizeHex(string s)
        => new string((s ?? string.Empty).Where(Uri.IsHexDigit).ToArray()).ToLowerInvariant();

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
        catch
        {
            return false;
        }
        finally
        {
            try { listener?.Stop(); } catch { }
        }
    }

    internal static bool IsPortListening(int port)
    {
        try
        {
            using var client = new TcpClient();
            return client.ConnectAsync(IPAddress.Loopback, port).Wait(300) && client.Connected;
        }
        catch
        {
            return false;
        }
    }

    public bool WaitForPortListening(int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (_handle == null || _handle.HasExited) return false;
            if (IsPortListening(LocalPort)) return true;
            Thread.Sleep(100);
        }
        return false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
