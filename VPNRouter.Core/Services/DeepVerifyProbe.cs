#nullable enable
using System;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace VPNRouter.Core.Services;

internal static class DeepVerifyProbe
{
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

    public static IPAddress? KnownHostPublicIp { get; set; }

    public static async Task<(bool ok, int latencyMs, string? err)> ProbeViaProxyAsync(
        VerificationProxy proxy, TimeSpan httpTimeout, CancellationToken ct,
        string probeUrl = DeepVerifyConstants.ProbeUrl, bool expectNoContent = false)
    {
        var handler = proxy.CreateHttpHandler(TimeSpan.FromSeconds(5));
        handler.AllowAutoRedirect = false;
        using var http = new HttpClient(handler) { Timeout = httpTimeout, MaxResponseContentBufferSize = 8192 };

        var sw = Stopwatch.StartNew();
        try
        {
            using var resp = await http.GetAsync(probeUrl, ct);
            if (!resp.IsSuccessStatusCode)
                return (false, 0, $"http {(int)resp.StatusCode}");

            var body = await resp.Content.ReadAsStringAsync(ct);

            if (expectNoContent)
                return resp.StatusCode == HttpStatusCode.NoContent && body.Length == 0
                    ? (true, (int)sw.ElapsedMilliseconds, null)
                    : (false, 0, "unexpected HTTPS control response");

            var (valid, evalErr) = EvaluateProbeResponse(body);
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
        catch (HttpRequestException)
        {
            return (false, 0, "HTTP verification failed");
        }
        catch (Exception ex)
        {
            return (false, 0, ex.GetType().Name);
        }
    }

    internal static async Task<bool> ProbeStunAsync(int localPort, CancellationToken ct)
    {
        using var client = new UdpClient(AddressFamily.InterNetwork);
        client.Connect(IPAddress.Loopback, localPort);

        byte[] request = [0, 1, 0, 0, 0x21, 0x12, 0xa4, 0x42, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        RandomNumberGenerator.Fill(request.AsSpan(8));
        for (int attempt = 0; attempt < 3; attempt++)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(1));
            try
            {
                await client.SendAsync(request.AsMemory(), deadline.Token).ConfigureAwait(false);
                var response = await client.ReceiveAsync(deadline.Token).ConfigureAwait(false);
                if (IsMatchingStunResponse(response.Buffer, request)) return true;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            catch (SocketException) { await Task.Delay(250, ct).ConfigureAwait(false); }
        }
        return false;
    }

    internal static bool IsMatchingStunResponse(ReadOnlySpan<byte> response, ReadOnlySpan<byte> request)
    {
        const int headerLength = 20;
        if (response.Length <= headerLength || request.Length != headerLength ||
            response[0] != 1 || response[1] != 1 || !response[4..20].SequenceEqual(request[4..20]))
            return false;

        int attributeLength = (response[2] << 8) | response[3];
        if (attributeLength % 4 != 0 || attributeLength != response.Length - headerLength) return false;

        bool hasMappedAddress = false;
        for (int offset = headerLength; offset < response.Length;)
        {
            int type = (response[offset] << 8) | response[offset + 1];
            int length = (response[offset + 2] << 8) | response[offset + 3];
            int data = offset + 4;
            int next = data + ((length + 3) & ~3);
            if (next > response.Length) return false;
            if (type == 0x0020 && length >= 2)
                hasMappedAddress |= (response[data + 1] == 1 && length == 8) ||
                    (response[data + 1] == 2 && length == 20);
            offset = next;
        }
        return hasMappedAddress;
    }

    public static async Task<(bool ok, double mbps, string? err)> MeasureBandwidthViaProxyAsync(
        VerificationProxy proxy, CancellationToken ct)
    {
        var handler = proxy.CreateHttpHandler(TimeSpan.FromSeconds(5));
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
        var ipLine = body.Split('\n').FirstOrDefault(line => line.StartsWith("ip=", StringComparison.Ordinal));
        if (ipLine is null || !IPAddress.TryParse(ipLine[3..].Trim(), out var ip))
            return (false, "bad response");

        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IsPrivateOrLoopback(ip))
            return (false, "local ip in response");

        var hostIp = hostPublicIp ?? KnownHostPublicIp;
        if (hostIp?.IsIPv4MappedToIPv6 == true) hostIp = hostIp.MapToIPv4();
        if (hostIp != null && ip.Equals(hostIp))
            return (false, "proxy reflects host public ip");

        return (true, null);
    }

    public static bool IsPrivateOrLoopback(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)) return true;
        var bytes = ip.GetAddressBytes();
        if (bytes.Length != 4)
            return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || (bytes[0] & 0xfe) == 0xfc;
        if (bytes[0] == 10 || (bytes[0] == 169 && bytes[1] == 254)) return true;
        if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;
        if (bytes[0] == 192 && bytes[1] == 168) return true;
        if (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127) return true;
        return false;
    }
}
