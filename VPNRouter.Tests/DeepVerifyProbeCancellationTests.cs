#nullable enable
using System.Net;
using System.Net.Sockets;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

public class DeepVerifyProbeCancellationTests
{
    private static TcpListener StartSilentListener(out int port, out Task<TcpClient> acceptedClient)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        port = ((IPEndPoint)listener.LocalEndpoint).Port;
        acceptedClient = listener.AcceptTcpClientAsync();
        return listener;
    }

    [Fact]
    public async Task ExternalCancellation_Rethrows_NotHttpTimeout()
    {
        var listener = StartSilentListener(out var port, out var acceptedClient);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                DeepVerifyProbe.ProbeViaSocksAsync(port, TimeSpan.FromSeconds(10), cts.Token));
        }
        finally
        {
            listener.Stop();
            try { (await acceptedClient).Dispose(); }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { }
        }
    }

    [Fact]
    public async Task ClientTimeout_WithoutExternalCancel_ReportsHttpTimeout()
    {
        var listener = StartSilentListener(out var port, out var acceptedClient);
        try
        {
            var (ok, _, err) = await DeepVerifyProbe.ProbeViaSocksAsync(
                port, TimeSpan.FromMilliseconds(500), CancellationToken.None);

            Assert.False(ok);
            Assert.False(string.IsNullOrWhiteSpace(err));
            Assert.True(
                err == "http timeout" || err!.StartsWith("http: ", StringComparison.Ordinal),
                $"expected a client-timeout http failure (\"http timeout\" or \"http: …\"), got: \"{err}\"");
        }
        finally
        {
            listener.Stop();
            try { (await acceptedClient).Dispose(); }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { }
        }
    }
}
