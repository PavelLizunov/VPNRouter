using System.Net;
using System.Net.Sockets;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

public sealed class TcpTlsProbeRealityDispatchTests
{
    private static (TcpListener listener, int port) StartBareTcpListener()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    var client = await listener.AcceptTcpClientAsync();
                    client.Close();
                }
                catch { break; }
            }
        });
        return (listener, port);
    }

    private static bool IsTcpReachable(ServerProbeStatus s) =>
        s == ServerProbeStatus.Ok
        || s == ServerProbeStatus.Slow
        || s == ServerProbeStatus.Implausible;

    [Fact]
    public async Task RealityProtocol_DispatchesToTcpOnly_NotFullTls_BratRegression()
    {
        var (listener, port) = StartBareTcpListener();
        try
        {
            var server = new VlessServerEntry
            {
                Name = "test-reality",
                Protocol = "vless",
                Security = "reality",
                Server = "127.0.0.1",
                Port = port,
                Uuid = "test-uuid",
                Reality = new VlessRealityConfig
                {
                    Enabled = true,
                    ServerName = "www.microsoft.com",
                    Fingerprint = "chrome",
                    PublicKey = "test-pbk",
                    ShortId = "abcd1234"
                }
            };

            var result = await TcpTlsProbe.ProbeServerAsync(server, CancellationToken.None);

            Assert.NotEqual(ServerProbeStatus.TlsFailed, result.Status);
            Assert.True(
                IsTcpReachable(result.Status),
                $"Reality probe must take TCP-only path. Status was {result.Status}: {result.Error}");
        }
        finally
        {
            listener.Stop();
        }
    }
}
