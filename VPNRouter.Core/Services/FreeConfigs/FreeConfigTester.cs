using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace VPNRouter.Core.Services.FreeConfigs;

/// <summary>
/// Tests free configs in two stages:
///   1. TCP connect + RTT measurement
///   2. TLS handshake to the SNI (validates the server actually responds as a TLS endpoint
///      presenting a valid cert for the expected SNI — real Reality servers proxy to real
///      SNIs like google.com/microsoft.com, so a valid TLS handshake with chain validation
///      strongly suggests the config is alive. Dead endpoints, honeypots, and local TUN
///      responders fail this stage.)
/// </summary>
public sealed class FreeConfigTester
{
    private static readonly TimeSpan TcpConnectTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan TlsHandshakeTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Above this latency (ms) mark as "Slow" even if reachable.</summary>
    private const int SlowThresholdMs = 800;

    /// <summary>
    /// Below this latency, the TCP response can't have come from a real remote server
    /// (internet RTT to any non-local host is ≥ 5 ms; sub-5ms means the connection was
    /// intercepted locally — usually by the user's active VPN TUN adapter).
    /// </summary>
    private const int ImplausibleThresholdMs = 5;

    public int MaxConcurrency { get; set; } = 30;

    /// <summary>
    /// If true (default), require a valid TLS handshake (cert chain + SAN matching SNI)
    /// in addition to TCP for status=Ok. Configs that TCP-connect but fail TLS are marked TlsFailed.
    /// </summary>
    public bool RequireTlsHandshake { get; set; } = true;

    public async Task TestAllAsync(
        IReadOnlyCollection<FreeConfigEntry> configs,
        IProgress<(int done, int total)>? progress = null,
        CancellationToken ct = default)
    {
        var sem = new SemaphoreSlim(MaxConcurrency);
        var total = configs.Count;
        var done = 0;

        var tasks = configs.Select(async cfg =>
        {
            await sem.WaitAsync(ct);
            try
            {
                await TestOneAsync(cfg, ct);
            }
            finally
            {
                sem.Release();
                var n = Interlocked.Increment(ref done);
                progress?.Report((n, total));
            }
        });

        await Task.WhenAll(tasks);
    }

    /// <summary>
    /// Test a single config:
    ///   1) TCP connect (2 attempts, take best RTT)
    ///   2) TLS handshake with cert validation (if RequireTlsHandshake)
    /// </summary>
    public async Task TestOneAsync(FreeConfigEntry cfg, CancellationToken ct = default)
    {
        cfg.LastTestedAt = DateTime.UtcNow;
        cfg.LastError = null;

        // ── Stage 1: TCP ──
        var latencies = new List<int>(capacity: 2);
        FreeConfigStatus tcpError = FreeConfigStatus.Timeout;

        for (var attempt = 0; attempt < 2; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var (status, latency, _) = await TcpPingAsync(cfg.Host, cfg.Port, ct);
            if (status == FreeConfigStatus.Ok)
                latencies.Add(latency);
            else
                tcpError = status;
        }

        if (latencies.Count == 0)
        {
            cfg.LatencyMs = 0;
            cfg.Status = tcpError;
            cfg.LastError = tcpError == FreeConfigStatus.Timeout ? "tcp timeout" : "tcp unreachable";
            return;
        }

        var bestLatency = latencies.Min();

        // ── Plausibility gate: sub-5ms TCP means local intercept (active VPN / proxy). ──
        if (bestLatency < ImplausibleThresholdMs)
        {
            cfg.LatencyMs = bestLatency;
            cfg.Status = FreeConfigStatus.Implausible;
            cfg.LastError = "latency < 5 ms (local intercept?)";
            return;
        }

        // ── Stage 2: TLS handshake ──
        if (RequireTlsHandshake)
        {
            var sni = !string.IsNullOrWhiteSpace(cfg.Sni) ? cfg.Sni : cfg.Host;
            var (tlsOk, tlsErr) = await TlsHandshakeAsync(cfg.Host, cfg.Port, sni, ct);

            if (!tlsOk)
            {
                cfg.Status = FreeConfigStatus.TlsFailed;
                cfg.LatencyMs = bestLatency;
                cfg.LastError = tlsErr ?? "tls failed";
                return;
            }
        }

        cfg.LatencyMs = bestLatency;
        cfg.Status = bestLatency > SlowThresholdMs ? FreeConfigStatus.Slow : FreeConfigStatus.Ok;
    }

    /// <summary>Single TCP connect attempt with timeout.</summary>
    private static async Task<(FreeConfigStatus status, int latencyMs, string? err)> TcpPingAsync(
        string host, int port, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TcpConnectTimeout);

        var sw = Stopwatch.StartNew();
        try
        {
            using var client = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
            await client.ConnectAsync(host, port, cts.Token);
            sw.Stop();
            return (FreeConfigStatus.Ok, (int)sw.ElapsedMilliseconds, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (FreeConfigStatus.Timeout, 0, "timeout");
        }
        catch (SocketException sx) when (
            sx.SocketErrorCode is SocketError.ConnectionRefused
                             or SocketError.ConnectionReset
                             or SocketError.HostUnreachable
                             or SocketError.NetworkUnreachable
                             or SocketError.HostNotFound)
        {
            return (FreeConfigStatus.Unreachable, 0, sx.SocketErrorCode.ToString());
        }
        catch (Exception ex)
        {
            return (FreeConfigStatus.Timeout, 0, ex.GetType().Name);
        }
    }

    /// <summary>
    /// Attempt a full TLS handshake to the given SNI.
    /// Validates: cert chain OK AND cert SAN/CN matches the SNI domain.
    /// A real Reality server forwards TLS to a real SNI (e.g. google.com) so a correct
    /// chain-valid cert for that domain will be presented — genuine check.
    /// </summary>
    private static async Task<(bool ok, string? err)> TlsHandshakeAsync(
        string host, int port, string sni, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TlsHandshakeTimeout);

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
                    // Must have at least a cert.
                    if (cert is null) { certError = "no cert"; return false; }

                    // Chain must validate OR have only a name-mismatch (we verify name separately).
                    // But for Reality we want a REAL public cert — so we enforce full chain validity.
                    if (errors != SslPolicyErrors.None)
                    {
                        certError = errors.ToString();
                        return false;
                    }

                    // Verify cert name matches the SNI (Reality servers forward to real domains).
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

    /// <summary>Check if the certificate's CN or any SAN entry matches the given domain (wildcard supported).</summary>
    private static bool CertNameMatches(X509Certificate2 cert, string domain)
    {
        if (string.IsNullOrEmpty(domain)) return false;

        var domainLower = domain.ToLowerInvariant();

        // Collect candidate names: CN + all SAN DNS entries.
        var names = new List<string>();

        // CN from Subject
        var cn = cert.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
        if (!string.IsNullOrEmpty(cn)) names.Add(cn);

        // SAN (Subject Alternative Names)
        var sanExt = cert.Extensions["2.5.29.17"]; // OID for SAN
        if (sanExt != null)
        {
            var sanText = sanExt.Format(multiLine: true);
            foreach (var line in sanText.Split('\n', '\r'))
            {
                var trimmed = line.Trim();
                // Format: "DNS Name=example.com" or "DNS:example.com"
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
