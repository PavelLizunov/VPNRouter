#nullable enable
using System;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace VPNRouter.Core.Services;

/// <summary>
/// The pure sing-box probe plumbing shared by <see cref="VlessDeepVerifier"/> and
/// <see cref="FreeConfigs.FreeConfigDeepVerifier"/> (#4 cleanup 2026-07-10). Both
/// verifiers spawn a temporary sing-box with a local SOCKS inbound and then run
/// the SAME mechanical checks over it — wait for the port to bind, HTTP-GET the
/// Cloudflare trace endpoint through the proxy, optionally measure bandwidth.
/// Those methods were byte-identical copies; they live here now.
///
/// <para>This is the "consolidation possible in a future refactor" that
/// <see cref="VlessDeepVerifier"/>'s class doc invited. It deliberately does NOT
/// touch each verifier's <i>result-mutation</i> (VLESS returns a structured
/// <c>DeepVerifyResult</c>; FreeConfigs mutates a <c>FreeConfigEntry</c> status
/// enum) — that difference is the "by design" duplication the author kept, and
/// it stays in each class. (AndroidFreeConfigDeepVerifier keeps its own copy —
/// separate Android .NET toolchain, see <see cref="DeepVerifyConstants"/>.)</para>
/// </summary>
internal static class DeepVerifyProbe
{
    // ── r9 P2: probe-in-flight signal for RuntimeStatusDetector ─────────────
    // Deep verify spawns REAL sing-box processes from our own bin dir, so the
    // ownership-filtered process detector counts them as "VPN running" and the
    // 2s status poll flipped the UI to a false "Connected via service" for the
    // duration of a batch (live-caught on brat 2026-07-10). While any probe is
    // in flight, the detector demands the second signal (the TUN ownership
    // semaphore a REAL tunnel holds) before reporting running.

    private static int _probesInFlight;

    /// <summary>True while any deep-verify sing-box probe spawn is alive in THIS process.</summary>
    public static bool AnyProbeInFlight => Volatile.Read(ref _probesInFlight) > 0;

    /// <summary>Raw counter for tests (delta-based assertions stay parallel-safe).</summary>
    internal static int ProbesInFlightForTests => Volatile.Read(ref _probesInFlight);

    /// <summary>Marks a probe as in flight until disposed. Dispose is idempotent.</summary>
    public static IDisposable BeginProbeScope()
    {
        Interlocked.Increment(ref _probesInFlight);
        return new ProbeScope();
    }

    private sealed class ProbeScope : IDisposable
    {
        private int _done;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
                Interlocked.Decrement(ref _probesInFlight);
        }
    }

    /// <summary>Poll a loopback TCP port until it accepts a connection or the wait elapses.</summary>
    public static async Task<bool> WaitForPortBoundAsync(int port, TimeSpan maxWait, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + maxWait;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var c = new TcpClient();
                var connectTask = c.ConnectAsync(IPAddress.Loopback, port);
                var completed = await Task.WhenAny(connectTask, Task.Delay(200, ct));
                if (completed == connectTask && c.Connected) return true;
            }
            catch { /* keep polling */ }
            await Task.Delay(100, ct);
        }
        return false;
    }

    /// <summary>Make an HTTP GET through a local SOCKS5 proxy. Returns (ok, latency_ms, err).
    /// Fails if the trace response carries a private/loopback ip= (the proxy leaked local).</summary>
    public static async Task<(bool ok, int latencyMs, string? err)> ProbeViaSocksAsync(
        int socksPort, TimeSpan httpTimeout, CancellationToken ct)
    {
        var handler = new SocketsHttpHandler
        {
            Proxy = new WebProxy($"socks5://127.0.0.1:{socksPort}"),
            UseProxy = true,
            ConnectTimeout = TimeSpan.FromSeconds(5),
        };
        using var http = new HttpClient(handler) { Timeout = httpTimeout };

        var sw = Stopwatch.StartNew();
        try
        {
            using var resp = await http.GetAsync(DeepVerifyConstants.ProbeUrl, ct);
            if (!resp.IsSuccessStatusCode)
                return (false, 0, $"http {(int)resp.StatusCode}");

            var body = await resp.Content.ReadAsStringAsync(ct);

            // Trace endpoint format: multiline "key=value". Look for "ip=" line with a non-local IP.
            if (!body.Contains("ip=", StringComparison.Ordinal))
                return (false, 0, "bad response");

            var ipLine = body.Split('\n').FirstOrDefault(l => l.StartsWith("ip=", StringComparison.Ordinal));
            if (ipLine != null)
            {
                var ipStr = ipLine[3..].Trim();
                if (IPAddress.TryParse(ipStr, out var ip))
                {
                    if (IsPrivateOrLoopback(ip))
                        return (false, 0, "local ip in response");
                }
            }

            sw.Stop();
            return (true, (int)sw.ElapsedMilliseconds, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // F1 (r8): EXTERNAL cancellation (user Cancel / caller budget) must
            // surface as cancellation, NOT be swallowed as "http timeout" — the
            // callers map "http timeout" to a server-meaningful ProxiedHttp FAIL,
            // which false-branded in-flight servers ProtocolHandshakeBlockedLikely
            // (and excluded them from the Auto pool for 12h) on every user Cancel.
            // Mirrors MeasureBandwidthViaSocksAsync's existing rethrow.
            throw;
        }
        catch (TaskCanceledException)
        {
            return (false, 0, "http timeout");
        }
        catch (HttpRequestException hx)
        {
            return (false, 0, $"http: {Short(hx.Message)}");
        }
        catch (Exception ex)
        {
            return (false, 0, ex.GetType().Name);
        }
    }

    /// <summary>Download ~5 MB through the SOCKS proxy, trying a few large-file mirrors,
    /// and report throughput in Mbps. Returns (false, ...) if none deliver enough bytes.</summary>
    public static async Task<(bool ok, double mbps, string? err)> MeasureBandwidthViaSocksAsync(
        int socksPort, CancellationToken ct)
    {
        var handler = new SocketsHttpHandler
        {
            Proxy = new WebProxy($"socks5://127.0.0.1:{socksPort}"),
            UseProxy = true,
            ConnectTimeout = TimeSpan.FromSeconds(5),
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };

        var urls = new[]
        {
            "https://speed.cloudflare.com/__down?bytes=5242880",  // Cloudflare, global
            "https://proof.ovh.net/files/10Mb.dat",               // OVH, EU
            "https://ash-speed.hetzner.com/100MB.bin",            // Hetzner, US
        };

        foreach (var url in urls)
        {
            try
            {
                var sw = Stopwatch.StartNew();
                using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!resp.IsSuccessStatusCode) continue;

                using var stream = await resp.Content.ReadAsStreamAsync(ct);
                var buffer = new byte[8192];
                long total = 0;
                const long target = 5_242_880L;
                while (total < target)
                {
                    var n = await stream.ReadAsync(buffer, ct);
                    if (n == 0) break;
                    total += n;
                }
                sw.Stop();

                if (total < 1_000_000) continue;
                if (sw.ElapsedMilliseconds < 100) continue;

                var mbps = (total * 8.0 / 1_000_000.0) / (sw.ElapsedMilliseconds / 1000.0);
                return (true, mbps, null);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { /* try next URL */ }
        }
        return (false, 0, "all bandwidth URLs failed");
    }

    /// <summary>True for loopback / RFC1918 / CGNAT IPv4 — a trace ip= in these ranges
    /// means the SOCKS proxy returned local, not the tunnel exit.</summary>
    public static bool IsPrivateOrLoopback(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return true;
        var bytes = ip.GetAddressBytes();
        if (bytes.Length != 4) return false;
        // 10.0.0.0/8
        if (bytes[0] == 10) return true;
        // 172.16.0.0/12
        if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;
        // 192.168.0.0/16
        if (bytes[0] == 192 && bytes[1] == 168) return true;
        // 100.64.0.0/10 — CGNAT
        if (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127) return true;
        return false;
    }

    /// <summary>Flatten newlines and cap a (usually stderr) snippet to <paramref name="max"/> chars.</summary>
    public static string TrimSnippet(string s, int max)
    {
        s = s.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return s.Length > max ? s[..max] + "…" : s;
    }

    private static string Short(string s) => s.Length > 60 ? s[..60] : s;
}
