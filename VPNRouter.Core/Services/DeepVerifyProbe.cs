#nullable enable
using System;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using VPNRouter.Core.Services.Diagnostics;

namespace VPNRouter.Core.Services;

internal static class DeepVerifyProbe
{
    public const int MaxDiagnosticBufferChars = 2048;

    private static int _probesInFlight;

    public static bool AnyProbeInFlight => Volatile.Read(ref _probesInFlight) > 0;

    internal static int ProbesInFlightForTests => Volatile.Read(ref _probesInFlight);

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
            catch { }
            await Task.Delay(100, ct);
        }
        return false;
    }

    public static IPAddress? KnownHostPublicIp { get; set; }

    public static async Task<(bool ok, int latencyMs, string? err)> ProbeViaSocksAsync(
        int socksPort, TimeSpan httpTimeout, CancellationToken ct, IPAddress? hostPublicIp = null)
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

            var (valid, evalErr) = EvaluateProbeResponse(body, hostPublicIp);
            if (!valid)
                return (false, 0, evalErr);

            sw.Stop();
            return (true, (int)sw.ElapsedMilliseconds, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
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
            "https://speed.cloudflare.com/__down?bytes=5242880",
            "https://proof.ovh.net/files/10Mb.dat",
            "https://ash-speed.hetzner.com/100MB.bin",
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
            catch { }
        }
        return (false, 0, "all bandwidth URLs failed");
    }

    internal static (bool ok, string? err) EvaluateProbeResponse(string body, IPAddress? hostPublicIp = null)
    {
        if (!body.Contains("ip=", StringComparison.Ordinal))
            return (false, "bad response");

        var ipLine = body.Split('\n').FirstOrDefault(l => l.StartsWith("ip=", StringComparison.Ordinal));
        if (ipLine != null)
        {
            var ipStr = ipLine[3..].Trim();
            if (!IPAddress.TryParse(ipStr, out var ip))
                return (false, "bad response");

            if (IsPrivateOrLoopback(ip))
                return (false, "local ip in response");

            var hostIp = hostPublicIp ?? KnownHostPublicIp;
            if (hostIp != null && ip.Equals(hostIp))
                return (false, "proxy reflects host public ip");
        }
        return (true, null);
    }

    public static bool IsPrivateOrLoopback(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return true;
        var bytes = ip.GetAddressBytes();
        if (bytes.Length != 4) return false;
        if (bytes[0] == 10) return true;
        if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;
        if (bytes[0] == 192 && bytes[1] == 168) return true;
        if (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127) return true;
        return false;
    }

    public static void AppendSanitizedLine(StringBuilder destination, string? line, int maxChars)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (line is null || maxChars <= 0) return;

        var sanitized = DiagnosticsRedactor.RedactLogText(line);
        lock (destination)
        {
            var remaining = maxChars - destination.Length;
            if (remaining <= 0) return;

            var take = Math.Min(sanitized.Length, remaining);
            destination.Append(sanitized.AsSpan(0, take));
            if (take < remaining) destination.Append('\n');
        }
    }

    public static string ReadSanitizedSnippet(StringBuilder source, int max)
    {
        ArgumentNullException.ThrowIfNull(source);
        lock (source)
        {
            return TrimSnippet(source.ToString(), max);
        }
    }

    public static string TrimSnippet(string s, int max)
    {
        s = s.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return s.Length > max ? s[..max] + "…" : s;
    }

    private static string Short(string s) => s.Length > 60 ? s[..60] : s;
}
