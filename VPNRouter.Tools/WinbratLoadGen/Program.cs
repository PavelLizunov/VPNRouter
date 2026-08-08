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

public static class GameUdpMvp
{
    private static readonly TimeSpan Duration = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan BurstStart = TimeSpan.FromMinutes(2.5);
    private static readonly TimeSpan BurstDuration = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan NormalInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan BurstInterval = TimeSpan.FromMilliseconds(20);
    private const int PayloadBytes = 256;

    public static async Task<GameUdpSummary> RunAsync(CancellationToken cancellationToken)
    {
        using var udp = new UdpClient();
        var address = (await Dns.GetHostAddressesAsync(LoadTestContract.HostName, cancellationToken))
            .FirstOrDefault(candidate => candidate.AddressFamily == AddressFamily.InterNetwork);
        if (address is null) throw new InvalidOperationException("fixed UDP endpoint did not resolve to IPv4");
        await udp.Client.ConnectAsync(address, LoadTestContract.UdpPort, cancellationToken);
        var cookie = await GetCookieAsync(udp, cancellationToken);
        var metrics = new GameUdpMetrics();
        var started = DateTimeOffset.UtcNow;
        var sequence = 0L;

        while (DateTimeOffset.UtcNow - started < Duration)
        {
            var now = DateTimeOffset.UtcNow;
            var request = CreateEchoRequest(cookie, sequence);
            metrics.Sent(sequence, now);
            await udp.SendAsync(request, cancellationToken);

            try
            {
                using var receiveTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                receiveTimeout.CancelAfter(TimeSpan.FromMilliseconds(40));
                var response = await udp.ReceiveAsync(receiveTimeout.Token);
                Observe(metrics, request, response.Buffer, DateTimeOffset.UtcNow);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }

            if (metrics.HasFailureGap(DateTimeOffset.UtcNow)) throw new InvalidOperationException("sent-but-unanswered gap reached three seconds");
            sequence++;
            var elapsed = DateTimeOffset.UtcNow - started;
            var interval = elapsed >= BurstStart && elapsed < BurstStart + BurstDuration ? BurstInterval : NormalInterval;
            await Task.Delay(interval, cancellationToken);
        }

        return metrics.Snapshot();
    }

    private static async Task<byte[]> GetCookieAsync(UdpClient udp, CancellationToken cancellationToken)
    {
        var request = new byte[LoadTestContract.CookieRequestBytes];
        request[0] = 1;
        request[1] = (byte)UdpPacketKind.CookieRequest;
        RandomNumberGenerator.Fill(request.AsSpan(2, 8));
        await udp.SendAsync(request, cancellationToken);
        using var receiveTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        receiveTimeout.CancelAfter(TimeSpan.FromSeconds(3));
        var response = await udp.ReceiveAsync(receiveTimeout.Token);
        if (response.Buffer.Length != LoadTestContract.CookieBytes || response.Buffer[1] != (byte)UdpPacketKind.Cookie)
            throw new InvalidOperationException("fixed UDP endpoint did not return a cookie");
        return response.Buffer;
    }

    private static byte[] CreateEchoRequest(byte[] cookie, long sequence)
    {
        var packet = new byte[LoadTestContract.EchoHeaderBytes + PayloadBytes];
        packet[0] = 1;
        packet[1] = (byte)UdpPacketKind.EchoRequest;
        cookie.CopyTo(packet, 2);
        BinaryPrimitives.WriteInt64BigEndian(packet.AsSpan(36, 8), sequence);
        RandomNumberGenerator.Fill(packet.AsSpan(LoadTestContract.EchoHeaderBytes));
        return packet;
    }

    private static void Observe(GameUdpMetrics metrics, byte[] request, byte[] response, DateTimeOffset now)
    {
        if (response.Length < 10 || response[0] != 1 || response[1] != (byte)UdpPacketKind.EchoResponse)
        {
            metrics.Received(-1, false, now);
            return;
        }

        var sequence = BinaryPrimitives.ReadInt64BigEndian(response.AsSpan(2, 8));
        var payloadMatches = response.AsSpan(10).SequenceEqual(request.AsSpan(44));
        metrics.Received(sequence, payloadMatches, now);
    }
}
