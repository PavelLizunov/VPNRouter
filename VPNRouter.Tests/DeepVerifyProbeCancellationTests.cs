#nullable enable
using System.Net;
using System.Net.Sockets;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// F1 (v2.47.0-r8): pins that <see cref="DeepVerifyProbe.ProbeViaSocksAsync"/>
/// distinguishes EXTERNAL cancellation (user Cancel / caller budget — must
/// rethrow so VlessDeepVerifier maps it to the Cancelled/Timeout phase) from its
/// own HttpClient timeout (a server-meaningful "http timeout"). Pre-F1 both
/// collapsed into "http timeout" → ProxiedHttp FAIL, so every user Cancel
/// false-branded in-flight servers ProtocolHandshakeBlockedLikely and excluded
/// them from the Auto pool for 12h.
///
/// <para>Harness: a silent loopback listener accepts the SOCKS connect and never
/// replies, guaranteeing the HTTP request is in-flight when we cancel / when the
/// client timeout fires. No real sing-box involved.</para>
/// </summary>
public class DeepVerifyProbeCancellationTests
{
    private static TcpListener StartSilentListener(out int port, out Task<TcpClient> acceptedClient)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        port = ((IPEndPoint)listener.LocalEndpoint).Port;
        acceptedClient = listener.AcceptTcpClientAsync(); // keep the accepted socket rooted
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

            // Pinned contract (F1): with NO external cancellation the client's own
            // timeout must surface as a graceful http failure (ok=false + a reason),
            // NOT rethrow as OperationCanceledException — that rethrow path belongs
            // to the sibling ExternalCancellation test. The exact reason string is
            // runtime-dependent and NOT part of the contract: the silent listener
            // stalls the SOCKS/TLS connect to the https ProbeUrl, so the 500 ms
            // HttpClient.Timeout races the connection layer. Depending on which wins,
            // the runtime reports either TaskCanceledException → "http timeout" or the
            // connect abort wrapped as HttpRequestException → "http: An error occurred
            // while establishing a connection…" (the shape that failed CI on run
            // 29481203691). Both are the same "client gave up" outcome, and both
            // callers (VlessDeepVerifier / FreeConfigDeepVerifier) map either string to
            // the identical server-meaningful ProxiedHttp failure. Accept exactly those
            // two shapes; ok=true, an empty reason, "local ip in response", or a rethrow
            // is a genuine regression and must still fail.
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
