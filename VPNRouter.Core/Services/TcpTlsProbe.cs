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

    // A UDP port that stayed silent proves nothing about the server (QUIC based protocols never answer a blind datagram).
    public bool IsVerified => IsReachable && !string.Equals(Error, TcpTlsProbe.UdpNoReplyNote, StringComparison.Ordinal);

    public static ServerProbeResult Unknown { get; } = new(ServerProbeStatus.Unknown, 0, null);
}

public static class TcpTlsProbe
{
    public const int SlowThresholdMs = 800;
    public const int ImplausibleThresholdMs = 5;
    public const string UdpNoReplyNote = "udp open (no reply)";

    public static TimeSpan TcpConnectTimeout { get; set; } = TimeSpan.FromSeconds(3);
    public static TimeSpan TlsHandshakeTimeout { get; set; } = TimeSpan.FromSeconds(3);

    public static ILogger? Logger { get; set; }

    // While the VPN's TUN adapter is up, a plain connect to any server is answered by the tunnel itself in under a millisecond, so the
    // number says nothing about the server. A probe is therefore bound to the physical interface (IP_UNICAST_IF, Windows) when the
    // tunnel is up, and the "under 5 ms means interception" rule applies only to an unbound probe made while the tunnel is up. Without a
    // tunnel a fast answer is real (a server in the same room), not a fault.
    public static Func<bool> IsTunnelActive { get; set; } = DefaultIsTunnelActive;

    public static Func<int?> OutboundInterfaceIndex { get; set; } = DefaultOutboundInterfaceIndex;

    private static DateTime _tunnelCheckedAt = DateTime.MinValue;
    private static bool _tunnelCached;
    private static DateTime _ifaceCheckedAt = DateTime.MinValue;
    private static int? _ifaceCached;

    private static bool DefaultIsTunnelActive()
    {
        var now = DateTime.UtcNow;
        if ((now - _tunnelCheckedAt).TotalSeconds < 2) return _tunnelCached;
        bool active;
        try
        {
            active = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces().Any(n =>
                n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up &&
                n.Name.Equals("VPNRouter-TUN", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            active = false;
        }
        _tunnelCached = active;
        _tunnelCheckedAt = now;
        return active;
    }

    private static int? DefaultOutboundInterfaceIndex()
    {
        var now = DateTime.UtcNow;
        if ((now - _ifaceCheckedAt).TotalSeconds < 5) return _ifaceCached;
        int? index = null;
        try { index = NetworkInterfaceDetector.GetInternetInterfaceIndex("VPNRouter-TUN", Logger); }
        catch { index = null; }
        _ifaceCached = index;
        _ifaceCheckedAt = now;
        return index;
    }

    // A probe socket; bound to the physical interface when the tunnel is up and Windows lets us (IP_UNICAST_IF = 31, index in network order).
    internal static Socket CreateProbeSocket(SocketType type, ProtocolType protocol, out bool bound)
    {
        var socket = new Socket(AddressFamily.InterNetwork, type, protocol);
        bound = ApplyOutboundBinding(socket);
        return socket;
    }

    internal static bool ApplyOutboundBinding(Socket socket)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            if (!IsTunnelActive()) return false;
            var index = OutboundInterfaceIndex();
            if (index is not > 0) return false;
            socket.SetSocketOption(SocketOptionLevel.IP, (SocketOptionName)31, IPAddress.HostToNetworkOrder(index.Value));
            return true;
        }
        catch
        {
            return false;
        }
    }

    // The tunnel answers an unbound connect itself; this decides whether a very fast answer is that interception.
    internal static bool LooksIntercepted(int latencyMs, bool bound)
        => latencyMs < ImplausibleThresholdMs && !bound && IsTunnelActive();

    internal const string Ipv6OnlyNote = "IPv6-only address - not probed, use Deep verify";

    private static bool IsIpv6OnlyError(string? err) =>
        string.Equals(err, "ipv6 not supported", StringComparison.Ordinal) || string.Equals(err, "no ipv4", StringComparison.Ordinal);

    // Name resolution is not part of the server's round trip: resolve first, time only the connect.
    private static async Task<(IPAddress? ip, string? err)> ResolveIpv4Async(string host, CancellationToken ct)
    {
        if (IPAddress.TryParse(host, out var literal))
            return literal.AddressFamily == AddressFamily.InterNetwork ? (literal, null) : (null, "ipv6 not supported");
        try
        {
            var addrs = await Dns.GetHostAddressesAsync(host, ct);
            var v4 = Array.Find(addrs, a => a.AddressFamily == AddressFamily.InterNetwork);
            return v4 is null ? (null, "no ipv4") : (v4, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return (null, "timeout");
        }
        catch (SocketException sx)
        {
            return (null, sx.SocketErrorCode.ToString());
        }
        catch (Exception ex)
        {
            return (null, ex.GetType().Name);
        }
    }

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
        var anyUnbound = false;

        for (var attempt = 0; attempt < 2; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var (ok, latency, err, bound) = await ProbeTcpCoreAsync(host, port, effectiveTcpTimeout, ct);
            if (ok)
            {
                latencies.Add(latency);
                if (!bound) anyUnbound = true;
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
            // The probe speaks IPv4 only: an IPv6-only server is "not judged", not "dead".
            if (IsIpv6OnlyError(lastTcpErr))
                return new ServerProbeResult(ServerProbeStatus.SkippedNotApplicable, 0, Ipv6OnlyNote);
            return new ServerProbeResult(tcpError, 0, lastTcpErr ?? "tcp failed");
        }

        var bestLatency = latencies.Min();

        if (LooksIntercepted(bestLatency, bound: !anyUnbound))
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
        var (ok, latency, err, _) = await ProbeTcpCoreAsync(host, port, tcpTimeout, ct);
        return (ok, latency, err);
    }

    internal static async Task<(bool ok, int latencyMs, string? err, bool bound)> ProbeTcpCoreAsync(
        string host, int port, TimeSpan tcpTimeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(tcpTimeout);

        var bound = false;
        try
        {
            var (ip, resolveErr) = await ResolveIpv4Async(host, cts.Token);
            if (ip is null)
                return (false, 0, resolveErr ?? "dns", false);

            using var socket = CreateProbeSocket(SocketType.Stream, ProtocolType.Tcp, out bound);
            socket.NoDelay = true;
            socket.LingerState = new LingerOption(enable: true, seconds: 0);

            var sw = Stopwatch.StartNew();
            await socket.ConnectAsync(new IPEndPoint(ip, port), cts.Token);
            sw.Stop();
            return (true, (int)sw.ElapsedMilliseconds, null, bound);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (false, 0, "timeout", bound);
        }
        catch (SocketException sx) when (
            sx.SocketErrorCode is SocketError.ConnectionRefused
                             or SocketError.ConnectionReset
                             or SocketError.HostUnreachable
                             or SocketError.NetworkUnreachable
                             or SocketError.HostNotFound)
        {
            return (false, 0, sx.SocketErrorCode.ToString(), bound);
        }
        catch (Exception ex)
        {
            return (false, 0, ex.GetType().Name, bound);
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

        Socket? tcp = null;
        SslStream? ssl = null;
        try
        {
            var (ip, resolveErr) = await ResolveIpv4Async(host, cts.Token);
            if (ip is null)
                return (false, resolveErr ?? "dns");

            tcp = CreateProbeSocket(SocketType.Stream, ProtocolType.Tcp, out _);
            tcp.NoDelay = true;
            tcp.LingerState = new LingerOption(enable: true, seconds: 0);
            await tcp.ConnectAsync(new IPEndPoint(ip, port), cts.Token);

            string? certError = null;

            ssl = new SslStream(new NetworkStream(tcp, ownsSocket: false), leaveInnerStreamOpen: false,
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
                return addrs.Length > 0
                    ? new ServerProbeResult(ServerProbeStatus.SkippedNotApplicable, 0, Ipv6OnlyNote)
                    : new ServerProbeResult(ServerProbeStatus.Unreachable, 0, "no address");

            ct.ThrowIfCancellationRequested();

            sw.Restart();
            using var udp = new UdpClient(AddressFamily.InterNetwork);
            var udpBound = ApplyOutboundBinding(udp.Client);
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
            if (LooksIntercepted(latencyMs, udpBound))
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
            return new ServerProbeResult(ServerProbeStatus.Ok, elapsedMs, UdpNoReplyNote);
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
