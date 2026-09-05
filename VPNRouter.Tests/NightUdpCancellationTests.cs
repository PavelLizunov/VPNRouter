using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

public sealed class NightUdpCancellationTests
{
    private static (UdpClient listener, int port) CreateLoopbackListener()
    {
        var listener = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)listener.Client.LocalEndPoint!).Port;
        return (listener, port);
    }

    [Fact]
    public async Task CancelAfterReceivesProbeDatagram_ThrowsOperationCanceledException()
    {
        using var testCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var tuple = CreateLoopbackListener();
        using var listener = tuple.listener;
        var port = tuple.port;
        using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(testCts.Token);

        var probeTask = TcpTlsProbe.ProbeUdpAsync("127.0.0.1", port, probeCts.Token);
        try
        {
            var received = await listener.ReceiveAsync(testCts.Token);
            probeCts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probeTask);

            Assert.NotNull(received.Buffer);
            Assert.Equal(8, received.Buffer.Length);
        }
        finally
        {
            probeCts.Cancel();
            try
            {
                await probeTask;
            }
            catch
            {
                // observe probeTask
            }
        }
    }

    [Fact]
    public async Task PreCanceled_DoesNotSend_ThrowsOperationCanceledException()
    {
        using var testCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var tuple = CreateLoopbackListener();
        using var listener = tuple.listener;
        var port = tuple.port;
        using var preCanceledCts = new CancellationTokenSource();
        preCanceledCts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await TcpTlsProbe.ProbeUdpAsync("127.0.0.1", port, preCanceledCts.Token);
        });

        await Task.Delay(50, testCts.Token);
        Assert.Equal(0, listener.Available);
    }

    [Fact]
    public async Task SilentListener_PermitsInternalTimeout_ReturnsOkWithRemark()
    {
        using var testCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var tuple = CreateLoopbackListener();
        using var listener = tuple.listener;
        var port = tuple.port;

        var probeTask = TcpTlsProbe.ProbeUdpAsync("127.0.0.1", port, testCts.Token);
        try
        {
            var result = await probeTask;

            Assert.Equal(ServerProbeStatus.Ok, result.Status);
            Assert.NotNull(result.Error);
            Assert.Contains("no reply", result.Error, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            testCts.Cancel();
            try
            {
                await probeTask;
            }
            catch
            {
                // observe probeTask
            }
        }
    }

    [Fact]
    public async Task ActualReply_ObservesResult()
    {
        using var testCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var tuple = CreateLoopbackListener();
        using var listener = tuple.listener;
        var port = tuple.port;

        var probeTask = TcpTlsProbe.ProbeUdpAsync("127.0.0.1", port, testCts.Token);
        try
        {
            var received = await listener.ReceiveAsync(testCts.Token);
            Assert.NotNull(received.Buffer);
            Assert.Equal(8, received.Buffer.Length);

            var response = new byte[] { 0x01, 0x02, 0x03, 0x04 };
            var sentBytes = await listener.SendAsync(response, received.RemoteEndPoint, testCts.Token);
            Assert.Equal(response.Length, sentBytes);

            var result = await probeTask;

            Assert.True(
                result.Status is ServerProbeStatus.Ok or ServerProbeStatus.Slow or ServerProbeStatus.Implausible,
                $"Expected Ok, Slow, or Implausible for live reply, got {result.Status}");
            Assert.True(result.LatencyMs >= 0, $"Expected non-negative latency, got {result.LatencyMs}");

            if (result.Status is ServerProbeStatus.Ok or ServerProbeStatus.Slow)
            {
                Assert.Null(result.Error);
            }
            else if (result.Status == ServerProbeStatus.Implausible)
            {
                Assert.Equal("udp <5ms", result.Error);
            }
        }
        finally
        {
            testCts.Cancel();
            try
            {
                await probeTask;
            }
            catch
            {
                // observe probeTask
            }
        }
    }

    [Theory]
    [InlineData("", 443)]
    [InlineData("   ", 443)]
    [InlineData("127.0.0.1", 0)]
    [InlineData("127.0.0.1", -1)]
    [InlineData("127.0.0.1", 65536)]
    public async Task InvalidInput_ReturnsUnreachable_NonCancel(string host, int port)
    {
        using var testCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var result = await TcpTlsProbe.ProbeUdpAsync(host, port, testCts.Token);

        Assert.Equal(ServerProbeStatus.Unreachable, result.Status);
        Assert.Equal("invalid host/port", result.Error);
    }

    [Theory]
    [InlineData("hysteria2")]
    [InlineData("tuic")]
    public async Task ProbeServerAsync_UdpProtocols_PropagatesOuterCancellation(string protocol)
    {
        using var testCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var tuple = CreateLoopbackListener();
        using var listener = tuple.listener;
        var port = tuple.port;
        using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(testCts.Token);

        var server = new VlessServerEntry
        {
            Name = $"test-{protocol}",
            Protocol = protocol,
            Server = "127.0.0.1",
            Port = port
        };

        var probeTask = TcpTlsProbe.ProbeServerAsync(server, probeCts.Token);
        try
        {
            var received = await listener.ReceiveAsync(testCts.Token);
            probeCts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probeTask);

            Assert.NotNull(received.Buffer);
            Assert.Equal(8, received.Buffer.Length);
        }
        finally
        {
            probeCts.Cancel();
            try
            {
                await probeTask;
            }
            catch
            {
                // observe probeTask
            }
        }
    }

    [Fact]
    public async Task ProbeServerAsync_PreCanceled_ThrowsOperationCanceledException()
    {
        using var preCanceledCts = new CancellationTokenSource();
        preCanceledCts.Cancel();

        var server = new VlessServerEntry
        {
            Name = "test-hy2",
            Protocol = "hysteria2",
            Server = "127.0.0.1",
            Port = 12345
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await TcpTlsProbe.ProbeServerAsync(server, preCanceledCts.Token);
        });
    }

    [Fact]
    public void SourceContract_PinsAwaitedReceive_AndExternalOceGuard()
    {
        var source = LoadSource("VPNRouter.Core", "Services", "TcpTlsProbe.cs");
        var cleanSource = StripComments(source);

        var probeUdpDef = cleanSource.IndexOf("public static async Task<ServerProbeResult> ProbeUdpAsync(", StringComparison.Ordinal);
        Assert.True(probeUdpDef >= 0, "ProbeUdpAsync method definition not found");

        var openBrace = cleanSource.IndexOf('{', probeUdpDef);
        Assert.True(openBrace >= 0, "ProbeUdpAsync opening brace not found");

        var depth = 0;
        var methodEnd = -1;
        for (var i = openBrace; i < cleanSource.Length; i++)
        {
            if (cleanSource[i] == '{') depth++;
            else if (cleanSource[i] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    methodEnd = i + 1;
                    break;
                }
            }
        }
        Assert.True(methodEnd > openBrace, "ProbeUdpAsync closing brace not found");

        var methodBody = cleanSource[probeUdpDef..methodEnd];

        // 1. Entry cancellation check before validation
        var entryCancelIdx = methodBody.IndexOf("ct.ThrowIfCancellationRequested();", StringComparison.Ordinal);
        var validationIdx = methodBody.IndexOf("if (string.IsNullOrWhiteSpace(host)", StringComparison.Ordinal);
        Assert.True(entryCancelIdx >= 0 && entryCancelIdx < validationIdx,
            "ct.ThrowIfCancellationRequested() must be called at entry before host/port validation");

        // 2. Elimination of redundant Task.Delay / Task.WhenAny
        Assert.DoesNotContain("Task.WhenAny", methodBody);
        Assert.DoesNotContain("Task.Delay", methodBody);

        // 3. Direct await of udp.ReceiveAsync(cts.Token)
        Assert.Contains("await udp.ReceiveAsync(cts.Token);", methodBody);

        // 4. Cancellation-aware SendAsync overload with linked token
        Assert.Contains("await udp.SendAsync(probe, endpoint, cts.Token);", methodBody);

        // 5. Post-receive cancellation check before latency
        var receiveIdx = methodBody.IndexOf("await udp.ReceiveAsync(cts.Token);", StringComparison.Ordinal);
        var postReceiveCancelIdx = methodBody.IndexOf("ct.ThrowIfCancellationRequested();", receiveIdx, StringComparison.Ordinal);
        var latencyCalcIdx = methodBody.IndexOf("var latencyMs =", receiveIdx, StringComparison.Ordinal);
        Assert.True(postReceiveCancelIdx >= 0 && postReceiveCancelIdx < latencyCalcIdx,
            "ct.ThrowIfCancellationRequested() must be called after receive before calculating latency");

        // 6. External OCE explicitly caught and rethrown before broadcatch
        var externalOceCatchIdx = methodBody.IndexOf("catch (OperationCanceledException) when (ct.IsCancellationRequested)", StringComparison.Ordinal);
        var broadCatchIdx = methodBody.IndexOf("catch (Exception ex)", StringComparison.Ordinal);
        Assert.True(externalOceCatchIdx >= 0 && externalOceCatchIdx < broadCatchIdx,
            "External OCE catch must rethrow before broadcatch (catch (Exception ex))");

        // 7. DNS catch rethrows external cancellation
        var dnsTryIdx = methodBody.IndexOf("Dns.GetHostAddressesAsync", StringComparison.Ordinal);
        var dnsCatchIdx = methodBody.IndexOf("catch (OperationCanceledException) when (ct.IsCancellationRequested)", dnsTryIdx, StringComparison.Ordinal);
        Assert.True(dnsCatchIdx >= 0 && dnsCatchIdx < receiveIdx,
            "DNS block must catch and rethrow external cancellation");
    }

    private static string StripComments(string source)
    {
        var noBlock = Regex.Replace(source, @"/\*.*?\*/", "", RegexOptions.Singleline);
        var noLine = Regex.Replace(noBlock, @"//.*", "");
        return noLine;
    }

    private static string LoadSource(params string[] relativeParts)
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        for (var i = 0; i < 8 && directory != null; i++, directory = directory.Parent)
        {
            var candidate = Path.Combine(
                new[] { directory.FullName }.Concat(relativeParts).ToArray());
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
        }
        throw new FileNotFoundException(
            $"Could not locate repository source: {Path.Combine(relativeParts)}");
    }
}
