using System.Diagnostics;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace VPNRouter.Core.Services;

/// <summary>
/// Outcome classes for a TCP + optional TLS probe.
/// Used by generic server-testing UI (Servers tab, Subscriptions tab).
/// Free Configs tab has its own richer <c>FreeConfigStatus</c> that maps
/// from this enum — see <c>FreeConfigTester</c>.
/// </summary>
public enum ServerProbeStatus
{
    /// <summary>Has not been tested yet.</summary>
    Unknown,

    /// <summary>TCP + TLS both passed, latency within normal range.</summary>
    Ok,

    /// <summary>Reachable but latency over <see cref="TcpTlsProbe.SlowThresholdMs"/>.</summary>
    Slow,

    /// <summary>TCP refused / host unreachable / DNS failure.</summary>
    Unreachable,

    /// <summary>TCP connect timed out.</summary>
    Timeout,

    /// <summary>TCP succeeded but TLS handshake failed (wrong SNI, dead endpoint, cert mismatch).</summary>
    TlsFailed,

    /// <summary>Latency &lt; 5 ms — likely intercepted by a local TUN adapter (active VPN).</summary>
    Implausible
}

/// <summary>
/// Immutable result of a single TCP+TLS probe against a server.
/// </summary>
public sealed record ServerProbeResult(
    ServerProbeStatus Status,
    int LatencyMs,
    string? Error)
{
    /// <summary>True if the server passed full TCP+TLS and latency is within acceptable range.</summary>
    public bool IsReachable => Status is ServerProbeStatus.Ok or ServerProbeStatus.Slow;

    public static ServerProbeResult Unknown { get; } = new(ServerProbeStatus.Unknown, 0, null);
}

/// <summary>
/// Generic TCP + optional TLS probe for any (host, port, sni) target.
/// Extracted from <c>FreeConfigTester</c> in v2.15.2 so the same logic can
/// be used from the Servers and Subscriptions tabs without depending on
/// <c>FreeConfigEntry</c>.
///
/// Defaults match <c>FreeConfigTester</c>: 3s TCP timeout, 3s TLS timeout,
/// 800 ms slow threshold, 5 ms implausibility floor.
/// </summary>
public static class TcpTlsProbe
{
    public const int SlowThresholdMs = 800;
    public const int ImplausibleThresholdMs = 5;

    public static TimeSpan TcpConnectTimeout { get; set; } = TimeSpan.FromSeconds(3);
    public static TimeSpan TlsHandshakeTimeout { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Full probe: TCP with 2 attempts (best RTT), optional TLS with cert-chain
    /// validation and SNI name matching. Returns a single result.
    ///
    /// <para>v2.31.6-r15 (iter#6 dedup): added optional per-call
    /// <paramref name="tcpTimeout"/> / <paramref name="tlsTimeout"/>
    /// overrides so <see cref="VPNRouter.Core.Services.FreeConfigs.FreeConfigTester"/>
    /// can use shorter 1.5 s timeouts for free-config bulk testing without
    /// mutating the static <see cref="TcpConnectTimeout"/> for the
    /// concurrent Servers/Subscribe Test all flows. Pre-r15 the only way
    /// to override was the static property, which created cross-test
    /// interference.</para>
    /// </summary>
    /// <param name="host">Hostname or IP.</param>
    /// <param name="port">TCP port.</param>
    /// <param name="sni">
    /// SNI to validate TLS cert against. If null/empty and <paramref name="requireTls"/>
    /// is true, <paramref name="host"/> is used as SNI.
    /// </param>
    /// <param name="requireTls">
    /// true (default) to require a successful TLS handshake with valid chain + name match.
    /// false to stop after TCP.
    /// </param>
    /// <param name="tcpTimeout">
    /// Per-call TCP connect timeout. Defaults to the static
    /// <see cref="TcpConnectTimeout"/> when null.
    /// </param>
    /// <param name="tlsTimeout">
    /// Per-call TLS handshake timeout. Defaults to the static
    /// <see cref="TlsHandshakeTimeout"/> when null.
    /// </param>
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

        // ── Stage 1: TCP (2 attempts) ──
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

                // Definitive errors — no point retrying
                if (tcpError == ServerProbeStatus.Unreachable) break;
            }
        }

        if (latencies.Count == 0)
        {
            return new ServerProbeResult(tcpError, 0, lastTcpErr ?? "tcp failed");
        }

        var bestLatency = latencies.Min();

        // ── Plausibility gate: sub-5 ms TCP means local intercept. ──
        if (bestLatency < ImplausibleThresholdMs)
        {
            return new ServerProbeResult(
                ServerProbeStatus.Implausible,
                bestLatency,
                "latency < 5 ms (local intercept?)");
        }

        // ── Stage 2: TLS handshake ──
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

    /// <summary>
    /// Raw TCP probe: single connection attempt with timeout.
    /// Returns (success, latency in ms, error description).
    /// </summary>
    public static Task<(bool ok, int latencyMs, string? err)> ProbeTcpAsync(
        string host, int port, CancellationToken ct)
        => ProbeTcpAsync(host, port, TcpConnectTimeout, ct);

    /// <summary>
    /// v2.31.6-r15: per-call timeout overload for callers that need a
    /// different TCP timeout than the static default (e.g.
    /// FreeConfigTester uses 1.5 s for bulk free-config testing).
    /// </summary>
    public static async Task<(bool ok, int latencyMs, string? err)> ProbeTcpAsync(
        string host, int port, TimeSpan tcpTimeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(tcpTimeout);

        var sw = Stopwatch.StartNew();
        try
        {
            using var client = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
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

    /// <summary>
    /// Raw TLS probe: full handshake with chain validation and SNI name match.
    /// Requires TCP reachability — caller must probe TCP first.
    /// </summary>
    public static Task<(bool ok, string? err)> ProbeTlsAsync(
        string host, int port, string sni, CancellationToken ct)
        => ProbeTlsAsync(host, port, sni, TlsHandshakeTimeout, ct);

    /// <summary>v2.31.6-r15: per-call timeout overload.</summary>
    public static async Task<(bool ok, string? err)> ProbeTlsAsync(
        string host, int port, string sni, TimeSpan tlsTimeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(tlsTimeout);

        TcpClient? tcp = null;
        SslStream? ssl = null;
        try
        {
            tcp = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
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

    /// <summary>Check if the cert's CN or any SAN entry matches the given domain (wildcard supported).</summary>
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
}
