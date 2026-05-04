using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Serilog;
using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

/// <summary>
/// Outcome classes for a TCP/UDP + optional TLS probe.
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
    Implausible,

    /// <summary>
    /// v2.31.6-r16 (Phase 1): quick TCP+TLS probe is not applicable to
    /// this protocol (e.g. Hysteria2 / TUIC are pure UDP+QUIC; raw TCP
    /// probe always fails as Unreachable even when the server works
    /// fine in production). The result was skipped intentionally and
    /// the user should run Deep verify (which spawns sing-box with the
    /// real protocol) for a meaningful answer. UI renders this as a
    /// neutral «—» with a tooltip explaining the skip.
    /// </summary>
    SkippedNotApplicable
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
    /// v2.31.6-r16 (Phase 3): static logger for verification diagnostics.
    /// Set once at app startup (typically in <c>MainWindowViewModel</c>
    /// ctor). Iter#7 user feedback: «есть ли у проверки логи?» — pre-r16
    /// the answer was no (this class had zero log calls in 333 lines,
    /// confirmed by grep across all log files in
    /// <c>%ProgramData%\VPNRouter\logs\</c>). r16 adds Debug-level
    /// per-probe entries (target, protocol, outcome, latency, error)
    /// so the user can <c>tail -f vpnrouter*.log</c> while running
    /// "Test all" and see exactly why each server fails.
    /// </summary>
    public static ILogger? Logger { get; set; }

    /// <summary>UDP probe timeout (Hysteria2 / TUIC don't speak TCP).</summary>
    public static TimeSpan UdpProbeTimeout { get; set; } = TimeSpan.FromSeconds(2);

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

    // ──────────────────────────────────────────────────────────────────────
    // v2.31.6-r16 (iter#7 / Phase 1): protocol-aware probe dispatcher
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Protocol-aware quick probe. Replaces the old "always TCP+TLS" path
    /// that produced 100% false negatives on Hysteria2 / TUIC servers
    /// (pure UDP+QUIC — TCP probe always returns Unreachable even when
    /// the server works fine in production). User feedback iter#7:
    /// «как проверка работает на не vless конфиги? сейчас на другом пк
    /// мне показала что два моих конфига со страницы servers не работают,
    /// хотя я без проблем смог к ним законнектиться».
    ///
    /// Dispatch by <see cref="VlessServerEntry.Protocol"/>:
    /// <list type="bullet">
    ///   <item><c>vless</c> + Reality/TLS → full TCP+TLS probe with cert validation</item>
    ///   <item><c>vless</c> + plain (legacy, no security) → TCP only</item>
    ///   <item><c>shadowsocks</c> / <c>ss</c> → TCP only (no TLS layer to validate)</item>
    ///   <item><c>hysteria2</c> / <c>hy2</c> → UDP probe (port reachability only)</item>
    ///   <item><c>tuic</c> → UDP probe (port reachability only)</item>
    ///   <item>unknown → <see cref="ServerProbeStatus.SkippedNotApplicable"/></item>
    /// </list>
    ///
    /// For UDP probes the result is "probably reachable" — true correctness
    /// requires Deep verify (which spawns sing-box with the actual protocol
    /// stack). Quick probe is the cheap reachability gate, deep probe is
    /// the expensive end-to-end correctness check.
    /// </summary>
    public static async Task<ServerProbeResult> ProbeServerAsync(
        VlessServerEntry server,
        CancellationToken ct = default)
    {
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
                // Reality / TLS variants → full TCP+TLS+cert validation.
                // Plain VLESS (no security, no transport TLS) → TCP only.
                var security = (server.Security ?? string.Empty).Trim().ToLowerInvariant();
                var hasReality = string.Equals(security, "reality", StringComparison.OrdinalIgnoreCase)
                              || (server.Reality is { Enabled: true });
                var hasTls = string.Equals(security, "tls", StringComparison.OrdinalIgnoreCase)
                          || (server.Tls is { Enabled: true });

                if (hasReality || hasTls)
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
                // SS speaks raw TCP encrypted from byte 0 — no TLS layer to handshake.
                // Plugin-wrapped SS (shadow-tls) does have TLS but probing the outer
                // TLS without the plugin sequence still yields the cover server's
                // cert which won't match SNI; so we treat all SS as TCP-only.
                result = await ProbeTcpOnlyAsync(host, port, ct);
                break;

            case "hysteria2":
            case "hy2":
            case "tuic":
                // Pure UDP+QUIC — TCP probe always fails Unreachable.
                result = await ProbeUdpAsync(host, port, ct);
                break;

            default:
                result = new ServerProbeResult(
                    ServerProbeStatus.SkippedNotApplicable,
                    0,
                    $"unknown protocol '{protocol}' — use Deep verify");
                break;
        }

        // v2.31.6-r17 (iter#7 follow-up): bumped from Debug → Information.
        // r16 MCP test confirmed Debug entries were filtered out by the
        // default LoggerConfiguration MinimumLevel = Information, so the
        // user-visible "verify logs" file showed only VlessDeepVerifier
        // results (Information) and missed every quick-probe outcome
        // (Debug). Information level for the probe end record makes the
        // log file actionable for diagnosing Test all batch results
        // without bumping the global minimum (which would also unleash
        // 48 other Debug calls across the codebase).
        Logger?.Information(
            "[TcpTlsProbe] {Name} {Host}:{Port} protocol={Protocol} status={Status} latency={LatencyMs}ms err={Error}",
            server.Name, host, port, protocol, result.Status, result.LatencyMs, result.Error ?? "-");
        return result;
    }

    /// <summary>
    /// TCP-only probe for protocols without a TLS layer (Shadowsocks,
    /// plain VLESS). Identical to <see cref="ProbeAsync"/> with
    /// <c>requireTls: false</c> — provided as a named entry point so
    /// call sites read self-documenting (<c>ProbeTcpOnlyAsync(host, port)</c>
    /// vs <c>ProbeAsync(host, port, null, requireTls: false)</c>).
    /// </summary>
    public static Task<ServerProbeResult> ProbeTcpOnlyAsync(
        string host, int port, CancellationToken ct = default)
        => ProbeAsync(host, port, sni: null, requireTls: false, ct);

    /// <summary>
    /// UDP probe for Hysteria2 / TUIC. UDP has no connection state — we
    /// just verify the port doesn't immediately surface ICMP Port
    /// Unreachable (which arrives as <see cref="SocketError.ConnectionReset"/>
    /// on a subsequent receive). For QUIC servers that ignore unsolicited
    /// garbage, we rely on the absence of ICMP back as "probably reachable".
    ///
    /// <para>This is intentionally optimistic: it's a reachability gate,
    /// not a correctness check. A blackhole IP returns ConnectionReset
    /// on most networks; an ICMP-stripping firewall + dead server would
    /// false-positive here, but Deep verify catches that downstream
    /// (real QUIC handshake fails).</para>
    /// </summary>
    public static async Task<ServerProbeResult> ProbeUdpAsync(
        string host, int port, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(host) || port <= 0 || port > 65535)
            return new ServerProbeResult(ServerProbeStatus.Unreachable, 0, "invalid host/port");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(UdpProbeTimeout);

        var sw = Stopwatch.StartNew();
        try
        {
            // Resolve hostname to IPv4 first
            IPAddress[] addrs;
            try
            {
                addrs = await Dns.GetHostAddressesAsync(host, cts.Token);
            }
            catch (Exception dnsEx)
            {
                return new ServerProbeResult(
                    ServerProbeStatus.Unreachable, 0,
                    $"dns: {Short(dnsEx.Message)}");
            }
            var ipv4 = Array.Find(addrs, a => a.AddressFamily == AddressFamily.InterNetwork);
            if (ipv4 is null)
                return new ServerProbeResult(ServerProbeStatus.Unreachable, 0, "no ipv4");

            using var udp = new UdpClient(AddressFamily.InterNetwork);
            udp.Client.SendTimeout = (int)UdpProbeTimeout.TotalMilliseconds;
            udp.Client.ReceiveTimeout = (int)UdpProbeTimeout.TotalMilliseconds;

            // Send 8 random bytes — too small for a real QUIC INITIAL but
            // enough to elicit ICMP Port Unreachable from a closed port.
            var probe = new byte[8];
            Random.Shared.NextBytes(probe);
            var endpoint = new IPEndPoint(ipv4, port);
            try
            {
                await udp.SendAsync(probe, probe.Length, endpoint);
            }
            catch (SocketException sx) when (sx.SocketErrorCode is
                SocketError.HostUnreachable or
                SocketError.NetworkUnreachable or
                SocketError.HostNotFound)
            {
                return new ServerProbeResult(
                    ServerProbeStatus.Unreachable, 0, sx.SocketErrorCode.ToString());
            }

            // Brief receive window. ICMP Unreachable surfaces as
            // ConnectionReset on the next op. QUIC servers that ignore
            // garbage will simply not reply → we reach the timeout
            // branch and mark Ok.
            var receiveTask = udp.ReceiveAsync(cts.Token).AsTask();
            var timeoutTask = Task.Delay(UdpProbeTimeout, CancellationToken.None);
            var completed = await Task.WhenAny(receiveTask, timeoutTask);
            sw.Stop();

            if (completed == receiveTask)
            {
                if (receiveTask.IsFaulted)
                {
                    var inner = receiveTask.Exception?.GetBaseException();
                    if (inner is SocketException sx2 && sx2.SocketErrorCode is SocketError.ConnectionReset)
                        return new ServerProbeResult(
                            ServerProbeStatus.Unreachable, 0, "ICMP port unreachable");
                    return new ServerProbeResult(
                        ServerProbeStatus.Unreachable, 0, inner?.GetType().Name ?? "udp recv error");
                }
                // Got a reply — rare for QUIC INITIAL with garbage payload.
                var latencyMs = (int)sw.ElapsedMilliseconds;
                if (latencyMs < ImplausibleThresholdMs)
                    return new ServerProbeResult(ServerProbeStatus.Implausible, latencyMs, "udp <5ms");
                var status = latencyMs > SlowThresholdMs ? ServerProbeStatus.Slow : ServerProbeStatus.Ok;
                return new ServerProbeResult(status, latencyMs, null);
            }

            // Timeout branch: no ICMP back → port is probably open.
            // Report ~timeout as latency (cosmetic — UDP doesn't have RTT).
            var elapsedMs = Math.Min((int)sw.ElapsedMilliseconds, (int)UdpProbeTimeout.TotalMilliseconds);
            return new ServerProbeResult(ServerProbeStatus.Ok, elapsedMs, "udp open (no reply)");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Outer timeout — same semantics: probably open.
            var elapsedMs = Math.Min((int)sw.ElapsedMilliseconds, (int)UdpProbeTimeout.TotalMilliseconds);
            return new ServerProbeResult(ServerProbeStatus.Ok, elapsedMs, "udp timeout (no reply)");
        }
        catch (SocketException sx) when (
            sx.SocketErrorCode is SocketError.ConnectionRefused
                             or SocketError.ConnectionReset
                             or SocketError.HostUnreachable
                             or SocketError.NetworkUnreachable
                             or SocketError.HostNotFound)
        {
            return new ServerProbeResult(
                ServerProbeStatus.Unreachable, 0, sx.SocketErrorCode.ToString());
        }
        catch (Exception ex)
        {
            return new ServerProbeResult(
                ServerProbeStatus.Unreachable, 0, ex.GetType().Name);
        }
    }

    /// <summary>
    /// Resolve effective SNI: Reality.ServerName → Tls.ServerName → host.
    /// Matches the sing-box outbound generation logic in ConfigGenerator.
    /// </summary>
    private static string ResolveSni(VlessServerEntry server, string fallback)
    {
        if (server.Reality is { Enabled: true } r && !string.IsNullOrWhiteSpace(r.ServerName))
            return r.ServerName;
        if (server.Tls is { Enabled: true } t && !string.IsNullOrWhiteSpace(t.ServerName))
            return t.ServerName;
        return fallback;
    }
}
