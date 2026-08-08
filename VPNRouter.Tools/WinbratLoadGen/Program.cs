#nullable enable

using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using VPNRouter.Tools.LoadTest.Protocol;
using VPNRouter.Tools.WinbratLoadGen;

var summary = await GameUdpMvp.RunAsync(CancellationToken.None);
Console.WriteLine(JsonSerializer.Serialize(summary));

public sealed class GameUdpCookieState(byte[] cookie)
{
    private byte[] _cookie = cookie;
    private DateTimeOffset _expiry = UdpCookieAuthenticator.GetExpiry(cookie);
    private DateTimeOffset _nextRefreshAt;

    public byte[] CurrentCookie => Volatile.Read(ref _cookie);

    public bool TryBeginRefresh(DateTimeOffset now)
    {
        lock (this)
        {
            if (now < _expiry.AddSeconds(-5) || now < _nextRefreshAt) return false;
            _nextRefreshAt = now.AddSeconds(1);
            return true;
        }
    }

    public bool Accept(byte[] cookie)
    {
        var expiry = UdpCookieAuthenticator.GetExpiry(cookie);
        lock (this)
        {
            if (expiry < _expiry) return false;
            Volatile.Write(ref _cookie, cookie);
            _expiry = expiry;
            return true;
        }
    }
}

public static class GameUdpMvp
{
    private const int PayloadBytes = 256;

    public static async Task<GameUdpSummary> RunAsync(CancellationToken cancellationToken)
    {
        using var udp = new UdpClient();
        var address = (await Dns.GetHostAddressesAsync(LoadTestContract.HostName, cancellationToken))
            .FirstOrDefault(candidate => candidate.AddressFamily == AddressFamily.InterNetwork);
        if (address is null) throw new InvalidOperationException("fixed UDP endpoint did not resolve to IPv4");
        await udp.Client.ConnectAsync(address, LoadTestContract.UdpPort, cancellationToken);

        var cookies = new GameUdpCookieState(await GetCookieAsync(udp, cancellationToken));
        var metrics = new GameUdpMetrics();
        using var receiveStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var receiver = ReceiveAsync(udp, cookies, metrics, receiveStop.Token);
        var started = DateTimeOffset.UtcNow;
        var deadline = started + GameUdpProfile.Duration;
        var nextSend = started;
        var sequence = 0L;

        try
        {
            while (DateTimeOffset.UtcNow < deadline)
            {
                var now = DateTimeOffset.UtcNow;
                var delay = nextSend - now;
                if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken);
                now = DateTimeOffset.UtcNow;
                if (cookies.TryBeginRefresh(now)) await udp.SendAsync(CreateCookieRequest(), cancellationToken);

                var request = CreateEchoRequest(cookies.CurrentCookie, sequence);
                metrics.Sent(sequence, now);
                await udp.SendAsync(request, cancellationToken);
                if (metrics.HasFailureGap(now)) throw new InvalidOperationException("sent-but-unanswered gap reached three seconds");

                sequence++;
                nextSend += GameUdpProfile.IntervalAt(now - started);
            }

            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
            return metrics.Snapshot();
        }
        finally
        {
            receiveStop.Cancel();
            try { await receiver; }
            catch (OperationCanceledException) when (receiveStop.IsCancellationRequested) { }
        }
    }

    private static async Task ReceiveAsync(UdpClient udp, GameUdpCookieState cookies, GameUdpMetrics metrics, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var response = await udp.ReceiveAsync(cancellationToken);
            var bytes = response.Buffer;
            if (bytes.Length == LoadTestContract.CookieBytes && bytes[0] == 1 && bytes[1] == (byte)UdpPacketKind.Cookie)
            {
                cookies.Accept(bytes);
                continue;
            }

            if (bytes.Length != 10 + PayloadBytes || bytes[0] != 1 || bytes[1] != (byte)UdpPacketKind.EchoResponse) continue;
            var sequence = BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(2, 8));
            metrics.Received(sequence, bytes.AsSpan(10).SequenceEqual(PayloadFor(sequence)), DateTimeOffset.UtcNow);
        }
    }

    private static async Task<byte[]> GetCookieAsync(UdpClient udp, CancellationToken cancellationToken)
    {
        await udp.SendAsync(CreateCookieRequest(), cancellationToken);
        using var receiveTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        receiveTimeout.CancelAfter(TimeSpan.FromSeconds(3));
        var response = await udp.ReceiveAsync(receiveTimeout.Token);
        if (response.Buffer.Length != LoadTestContract.CookieBytes || response.Buffer[1] != (byte)UdpPacketKind.Cookie)
            throw new InvalidOperationException("fixed UDP endpoint did not return a cookie");
        return response.Buffer;
    }

    public static byte[] PayloadFor(long sequence)
    {
        var payload = new byte[PayloadBytes];
        for (var index = 0; index < payload.Length; index++) payload[index] = unchecked((byte)(sequence + index));
        return payload;
    }

    private static byte[] CreateCookieRequest()
    {
        var request = new byte[LoadTestContract.CookieRequestBytes];
        request[0] = 1;
        request[1] = (byte)UdpPacketKind.CookieRequest;
        RandomNumberGenerator.Fill(request.AsSpan(2, 8));
        return request;
    }

    private static byte[] CreateEchoRequest(byte[] cookie, long sequence)
    {
        var packet = new byte[LoadTestContract.EchoHeaderBytes + PayloadBytes];
        packet[0] = 1;
        packet[1] = (byte)UdpPacketKind.EchoRequest;
        cookie.CopyTo(packet, 2);
        BinaryPrimitives.WriteInt64BigEndian(packet.AsSpan(36, 8), sequence);
        PayloadFor(sequence).CopyTo(packet, LoadTestContract.EchoHeaderBytes);
        return packet;
    }
}
