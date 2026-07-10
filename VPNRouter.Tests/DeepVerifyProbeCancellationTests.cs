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
    private static TcpListener StartSilentListener(out int port)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = listener.AcceptTcpClientAsync(); // hold the socket open, say nothing
        return listener;
    }

    [Fact]
    public async Task ExternalCancellation_Rethrows_NotHttpTimeout()
    {
        var listener = StartSilentListener(out var port);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                DeepVerifyProbe.ProbeViaSocksAsync(port, TimeSpan.FromSeconds(10), cts.Token));
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task ClientTimeout_WithoutExternalCancel_ReportsHttpTimeout()
    {
        var listener = StartSilentListener(out var port);
        try
        {
            var (ok, _, err) = await DeepVerifyProbe.ProbeViaSocksAsync(
                port, TimeSpan.FromMilliseconds(500), CancellationToken.None);
            Assert.False(ok);
            Assert.Equal("http timeout", err);
        }
        finally { listener.Stop(); }
    }
}
