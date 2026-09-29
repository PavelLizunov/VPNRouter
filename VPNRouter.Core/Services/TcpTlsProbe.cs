using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Serilog;
using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

public enum ServerProbeStatus
{
    Unknown,

    Ok,

    Slow,

    Unreachable,

    Timeout,

    TlsFailed,

    Implausible,

    SkippedNotApplicable
}

public sealed record ServerProbeResult(
    ServerProbeStatus Status,
    int LatencyMs,
    string? Error)
{
    public bool IsReachable => Status is ServerProbeStatus.Ok or ServerProbeStatus.Slow;

    public static ServerProbeResult Unknown { get; } = new(ServerProbeStatus.Unknown, 0, null);
}

public static class TcpTlsProbe
{
    public const int SlowThresholdMs = 800;
    public const int ImplausibleThresholdMs = 5;

    public static TimeSpan TcpConnectTimeout { get; set; } = TimeSpan.FromSeconds(3);
    public static TimeSpan TlsHandshakeTimeout { get; set; } = TimeSpan.FromSeconds(3);

    public static ILogger? Logger { get; set; }

    public static TimeSpan UdpProbeTimeout { get; set; } = TimeSpan.FromSeconds(2);

    public static async Task<ServerProbeResult> ProbeAsync(
        string host,
        int port,
        string? sni,
        bool requireTls = true,
        CancellationToken ct = default,
        TimeSpan? tcpTimeout = null,
        TimeSpan? tlsTimeout = null)
    {
        var effectiveTcpTimeout = tcpTimeout ?? TcpConnectTimeout;
        var effectiveTlsTimeout = tlsTimeout ?? TlsHandshakeTimeout;
        if (string.IsNullOrWhiteSpace(host) || port <= 0 || port > 65535)
            return new ServerProbeResult(ServerProbeStatus.Unreachable, 0, "invalid host/port");

        var latencies = new List<int>(capacity: 2);
        ServerProbeStatus tcpError = ServerProbeStatus.Timeout;
        string? lastTcpErr = null;

        for (var attempt = 0; attempt < 2; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var (ok, latency, err) = await ProbeTcpAsync(host, port, effectiveTcpTimeout, ct);
            if (ok)
            {
                latencies.Add(latency);
            }
            else
            {
                lastTcpErr = err;
                tcpError = string.Equals(err, "timeout", StringComparison.OrdinalIgnoreCase)
                    ? ServerProbeStatus.Timeout
                    : ServerProbeStatus.Unreachable;

                if (tcpError == ServerProbeStatus.Unreachable) break;
            }
        }

        if (latencies.Count == 0)
        {
            return new ServerProbeResult(tcpError, 0, lastTcpErr ?? "tcp failed");
        }

        var bestLatency = latencies.Min();

        if (bestLatency < ImplausibleThresholdMs)
        {
            return new ServerProbeResult(
                ServerProbeStatus.Implausible,
                bestLatency,
                "latency < 5 ms (local intercept?)");
        }

        if (requireTls)
        {
            var effectiveSni = !string.IsNullOrWhiteSpace(sni) ? sni : host;
            var (tlsOk, tlsErr) = await ProbeTlsAsync(host, port, effectiveSni, effectiveTlsTimeout, ct);

            if (!tlsOk)
            {
                return new ServerProbeResult(
                    ServerProbeStatus.TlsFailed,
                    bestLatency,
                    tlsErr ?? "tls failed");
            }
        }

        var status = bestLatency > SlowThresholdMs ? ServerProbeStatus.Slow : ServerProbeStatus.Ok;
        return new ServerProbeResult(status, bestLatency, null);
    }

    public static Task<(bool ok, int latencyMs, string? err)> ProbeTcpAsync(
        string host, int port, CancellationToken ct)
        => ProbeTcpAsync(host, port, TcpConnectTimeout, ct);

    public static async Task<(bool ok, int latencyMs, string? err)> ProbeTcpAsync(
        string host, int port, TimeSpan tcpTimeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(tcpTimeout);

        var sw = Stopwatch.StartNew();
        try
        {
            using var client = new TcpClient(AddressFamily.InterNetwork)
            {
                NoDelay = true,
                LingerState = new LingerOption(enable: true, seconds: 0)
            };
            await client.ConnectAsync(host, port, cts.Token);
            sw.Stop();
            return (true, (int)sw.ElapsedMilliseconds, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (false, 0, "timeout");
        }
        catch (SocketException sx) when (
            sx.SocketErrorCode is SocketError.ConnectionRefused
                             or SocketError.ConnectionReset
                             or SocketError.HostUnreachable
                             or SocketError.NetworkUnreachable
                             or SocketError.HostNotFound)
        {
            return (false, 0, sx.SocketErrorCode.ToString());
        }
        catch (Exception ex)
        {
            return (false, 0, ex.GetType().Name);
        }
    }

    public static Task<(bool ok, string? err)> ProbeTlsAsync(
        string host, int port, string sni, CancellationToken ct)
        => ProbeTlsAsync(host, port, sni, TlsHandshakeTimeout, ct);

    public static async Task<(bool ok, string? err)> ProbeTlsAsync(
        string host, int port, string sni, TimeSpan tlsTimeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(tlsTimeout);

        TcpClient? tcp = null;
        SslStream? ssl = null;
        try
        {
            tcp = new TcpClient(AddressFamily.InterNetwork)
            {
                NoDelay = true,
                LingerState = new LingerOption(enable: true, seconds: 0)
            };
            await tcp.ConnectAsync(host, port, cts.Token);

            string? certError = null;

            ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false,
                userCertificateValidationCallback: (sender, cert, chain, errors) =>
                {
                    if (cert is null) { certError = "no cert"; return false; }

                    if (errors != SslPolicyErrors.None)
                    {
                        certError = errors.ToString();
                        return false;
                    }

                    var cert2 = cert as X509Certificate2 ?? new X509Certificate2(cert);
                    if (!CertNameMatches(cert2, sni))
                    {
                        certError = $"cert name != {sni}";
                        return false;
                    }

                    return true;
                });

            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = sni,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            }, cts.Token);

            return (true, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (false, "tls timeout");
        }
        catch (AuthenticationException aex)
        {
            return (false, Short(aex.Message));
        }
        catch (IOException iox)
        {
            return (false, $"io: {Short(iox.Message)}");
        }
        catch (Exception ex)
        {
            return (false, ex.GetType().Name);
        }
        finally
        {
            ssl?.Dispose();
            tcp?.Dispose();
        }
    }

    private static bool CertNameMatches(X509Certificate2 cert, string domain)
    {
        if (string.IsNullOrEmpty(domain)) return false;

        var domainLower = domain.ToLowerInvariant();
        var names = new List<string>();

        var cn = cert.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
        if (!string.IsNullOrEmpty(cn)) names.Add(cn);

        var sanExt = cert.Extensions["2.5.29.17"];
        if (sanExt != null)
        {
            var sanText = sanExt.Format(multiLine: true);
            foreach (var line in sanText.Split('\n', '\r'))
            {
                var trimmed = line.Trim();
                var idx = trimmed.IndexOf('=');
                if (idx < 0) idx = trimmed.IndexOf(':');
                if (idx >= 0 && trimmed.StartsWith("DNS", StringComparison.OrdinalIgnoreCase))
                    names.Add(trimmed[(idx + 1)..].Trim());
            }
        }

        foreach (var n in names)
        {
            var nLower = n.ToLowerInvariant();
            if (nLower == domainLower) return true;
            if (nLower.StartsWith("*.") && domainLower.EndsWith(nLower[1..]))
                return true;
        }
        return false;
    }

    private static string Short(string s) => s.Length > 60 ? s[..60] : s;

    public static async Task<ServerProbeResult> ProbeServerAsync(
        VlessServerEntry server,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (server is null)
            return new ServerProbeResult(ServerProbeStatus.Unreachable, 0, "server is null");

        var protocol = (server.Protocol ?? "vless").Trim().ToLowerInvariant();
        var host = server.Server ?? string.Empty;
        var port = server.Port;

        Logger?.Debug(
            "TcpTlsProbe.ProbeServerAsync start: name={Name} host={Host} port={Port} protocol={Protocol}",
            server.Name, host, port, protocol);

        ServerProbeResult result;
        switch (protocol)
        {
            case "vless":
            {
                var security = (server.Security ?? string.Empty).Trim().ToLowerInvariant();
                var hasReality = string.Equals(security, "reality", StringComparison.OrdinalIgnoreCase)
                              || (server.Reality is { Enabled: true });
                var hasTls = string.Equals(security, "tls", StringComparison.OrdinalIgnoreCase)
                          || (server.Tls is { Enabled: true });

                if (hasReality)
                {
                    result = await ProbeTcpOnlyAsync(host, port, ct);
                }
                else if (hasTls)
                {
                    var sni = ResolveSni(server, host);
                    result = await ProbeAsync(host, port, sni, requireTls: true, ct);
                }
                else
                {
                    result = await ProbeAsync(host, port, sni: null, requireTls: false, ct);
                }
                break;
            }

            case "shadowsocks":
            case "ss":
                result = await ProbeTcpOnlyAsync(host, port, ct);
                break;

            case "hysteria2":
            case "hy2":
            case "tuic":
                result = await ProbeUdpAsync(host, port, ct);
                break;

            default:
                result = new ServerProbeResult(
                    ServerProbeStatus.SkippedNotApplicable,
                    0,
                    $"unknown protocol '{protocol}' — use Deep verify");
                break;
        }

        Logger?.Information(
            "[TcpTlsProbe] {Name} {Host}:{Port} protocol={Protocol} status={Status} latency={LatencyMs}ms err={Error}",
            server.Name, host, port, protocol, result.Status, result.LatencyMs, result.Error ?? "-");
        return result;
    }

    public static Task<ServerProbeResult> ProbeTcpOnlyAsync(
        string host, int port, CancellationToken ct = default)
        => ProbeAsync(host, port, sni: null, requireTls: false, ct);

    public static async Task<ServerProbeResult> ProbeUdpAsync(
        string host, int port, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(host) || port <= 0 || port > 65535)
            return new ServerProbeResult(ServerProbeStatus.Unreachable, 0, "invalid host/port");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(UdpProbeTimeout);

        var sw = Stopwatch.StartNew();
        try
        {
            IPAddress[] addrs;
            try
            {
                addrs = await Dns.GetHostAddressesAsync(host, cts.Token);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception dnsEx)
            {
                ct.ThrowIfCancellationRequested();
                return new ServerProbeResult(
                    ServerProbeStatus.Unreachable, 0,
                    $"dns: {Short(dnsEx.Message)}");
            }
            var ipv4 = Array.Find(addrs, a => a.AddressFamily == AddressFamily.InterNetwork);
            if (ipv4 is null)
                return new ServerProbeResult(ServerProbeStatus.Unreachable, 0, "no ipv4");

            ct.ThrowIfCancellationRequested();

            using var udp = new UdpClient(AddressFamily.InterNetwork);
            udp.Client.SendTimeout = (int)UdpProbeTimeout.TotalMilliseconds;
            udp.Client.ReceiveTimeout = (int)UdpProbeTimeout.TotalMilliseconds;

            var probe = new byte[8];
            Random.Shared.NextBytes(probe);
            var endpoint = new IPEndPoint(ipv4, port);
            try
            {
                await udp.SendAsync(probe, endpoint, cts.Token);
            }
            catch (SocketException sx) when (sx.SocketErrorCode is
                SocketError.HostUnreachable or
                SocketError.NetworkUnreachable or
                SocketError.HostNotFound)
            {
                return new ServerProbeResult(
                    ServerProbeStatus.Unreachable, 0, sx.SocketErrorCode.ToString());
            }

            await udp.ReceiveAsync(cts.Token);
            sw.Stop();
            ct.ThrowIfCancellationRequested();

            var latencyMs = (int)sw.ElapsedMilliseconds;
            if (latencyMs < ImplausibleThresholdMs)
                return new ServerProbeResult(ServerProbeStatus.Implausible, latencyMs, "udp <5ms");
            var status = latencyMs > SlowThresholdMs ? ServerProbeStatus.Slow : ServerProbeStatus.Ok;
            return new ServerProbeResult(status, latencyMs, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            var elapsedMs = Math.Min((int)sw.ElapsedMilliseconds, (int)UdpProbeTimeout.TotalMilliseconds);
            return new ServerProbeResult(ServerProbeStatus.Ok, elapsedMs, "udp open (no reply)");
        }
        catch (SocketException sx) when (
            sx.SocketErrorCode is SocketError.ConnectionRefused
                             or SocketError.ConnectionReset
                             or SocketError.HostUnreachable
                             or SocketError.NetworkUnreachable
                             or SocketError.HostNotFound)
        {
            var err = sx.SocketErrorCode == SocketError.ConnectionReset
                ? "ICMP port unreachable"
                : sx.SocketErrorCode.ToString();
            return new ServerProbeResult(
                ServerProbeStatus.Unreachable, 0, err);
        }
        catch (Exception ex)
        {
            ct.ThrowIfCancellationRequested();
            return new ServerProbeResult(
                ServerProbeStatus.Unreachable, 0, ex.GetType().Name);
        }
    }

    private static string ResolveSni(VlessServerEntry server, string fallback)
    {
        if (server.Reality is { Enabled: true } r && !string.IsNullOrWhiteSpace(r.ServerName))
            return r.ServerName;
        if (server.Tls is { Enabled: true } t && !string.IsNullOrWhiteSpace(t.ServerName))
            return t.ServerName;
        return fallback;
    }
}
