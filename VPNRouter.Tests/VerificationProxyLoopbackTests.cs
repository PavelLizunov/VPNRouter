using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using VPNRouter.Core;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

public sealed class VerificationProxyLoopbackTests
{
    [Fact]
    public Task OwnedProxyDeliversHttpsWithIndependentDestinationTrust() => WithOwnedProxyAsync(async (proxy, ct) =>
    {
        var now = DateTimeOffset.UtcNow;
        using var rootKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var rootRequest = new CertificateRequest("CN=Trace fixture root", rootKey, HashAlgorithmName.SHA256);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        using var root = rootRequest.CreateSelfSigned(now.AddMinutes(-5), now.AddHours(1));
        using var originKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=localhost", originKey, HashAlgorithmName.SHA256);
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        using var signed = request.Create(root, now.AddMinutes(-1), now.AddMinutes(15), RandomNumberGenerator.GetBytes(16));
        using var originCert = signed.CopyWithPrivateKey(originKey);

        using var origin = new TcpListener(IPAddress.Loopback, 0);
        origin.Start();
        var endpoint = (IPEndPoint)origin.LocalEndpoint;
        using var originCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var handler = proxy.CreateHttpHandler(TimeSpan.FromSeconds(4));
        var connect = handler.ConnectCallback!;
        var originTunnels = 0;
        handler.ConnectCallback = (context, token) =>
        {
            Interlocked.Increment(ref originTunnels);
            return connect(context, token);
        };
        var trust = new X509ChainPolicy { TrustMode = X509ChainTrustMode.CustomRootTrust, RevocationMode = X509RevocationMode.NoCheck };
        trust.CustomTrustStore.Add(root);
        handler.SslOptions.CertificateChainPolicy = trust;
        using var http = new HttpClient(handler);
        var serve = ServeTraceAsync(origin, originCert, originCts.Token);
        try
        {
            var response = await http.GetStringAsync($"https://127.0.0.1:{endpoint.Port}/trace", originCts.Token);
            Assert.Equal("ip=198.51.100.42\n", response);
            await serve;
            Assert.Equal(1, originTunnels);
        }
        finally
        {
            await originCts.CancelAsync();
            try { await serve; } catch (OperationCanceledException) { }
        }
    });

    [Fact]
    public Task ParallelHttpCanariesKeepSeparateTunnelsAndRespectOriginStatus() => WithOwnedProxyAsync(async (proxy, ct) =>
    {
        using var heldOrigin = new TcpListener(IPAddress.Loopback, 0);
        using var otherOrigin = new TcpListener(IPAddress.Loopback, 0);
        heldOrigin.Start();
        otherOrigin.Start();
        var heldAddress = $"http://{heldOrigin.LocalEndpoint}";
        var otherAddress = $"http://{otherOrigin.LocalEndpoint}";
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var held = VlessDeepVerifier.ProbeCanaryAsync(proxy, heldAddress + "/held", stop.Token);
        var sibling = VlessDeepVerifier.ProbeCanaryAsync(proxy, otherAddress + "/moved", ct);
        using var first = await heldOrigin.AcceptTcpClientAsync(ct);
        using var second = await otherOrigin.AcceptTcpClientAsync(ct);
        await ReadOriginRequestAsync(first.GetStream(), "/held", ct);
        await ReadOriginRequestAsync(second.GetStream(), "/moved", ct);
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => held);

        // Follow an origin redirect across ports on the surviving request.
        await ReplyAsync(second, $"302 Found\r\nLocation: {heldAddress}/ready", ct);
        using var redirected = await heldOrigin.AcceptTcpClientAsync(ct);
        await ReadOriginRequestAsync(redirected.GetStream(), "/ready", ct);
        await ReplyAsync(redirected, "204 No Content", ct);
        Assert.True(await sibling);

        var rejected = VlessDeepVerifier.ProbeCanaryAsync(proxy, otherAddress + "/status", ct);
        using var status = await otherOrigin.AcceptTcpClientAsync(ct);
        await ReadOriginRequestAsync(status.GetStream(), "/status", ct);
        await ReplyAsync(status, "407 Proxy Authentication Required", ct);
        Assert.False(await rejected);
    });

    private static async Task ReplyAsync(TcpClient client, string statusAndHeaders, CancellationToken ct)
    {
        var response = $"HTTP/1.1 {statusAndHeaders}\r\nConnection: close\r\nContent-Length: 0\r\n\r\n";
        await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes(response), ct);
    }

    private static async Task WithOwnedProxyAsync(Func<VerificationProxy, CancellationToken, Task> test)
    {
        var runtime = Environment.GetEnvironmentVariable("VPNROUTER_TEST_VERIFIER_RUNTIME");
        Assert.SkipUnless(!string.IsNullOrEmpty(runtime), "Requires the verified sing-box fixture");
        Assert.True(File.Exists(runtime));

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(20));
        var proxy = new VerificationProxy(NetPortUtil.FindFreePort());
        var config = new JsonObject
        {
            ["log"] = new JsonObject { ["level"] = "info" },
            ["inbounds"] = new JsonArray(proxy.CreateInbound()),
            ["outbounds"] = new JsonArray(new JsonObject { ["type"] = "direct", ["tag"] = "fixture" }),
        };
        var path = Path.Combine(Path.GetTempPath(), $"vpnrouter-verify-fixture-{Guid.NewGuid():N}.json");
        try
        {
            await using (var writer = new StreamWriter(AppPaths.CreatePrivateFile(path, FileMode.CreateNew)))
                await writer.WriteAsync(config.ToJsonString().AsMemory(), cts.Token);

            using var child = new ProcessRunner().Start(new ProcessRequest(runtime!, ["run", "-c", path]), process =>
            {
                process.OutputLine += (_, line) => proxy.ObserveChildOutput(line);
                process.ErrorLine += (_, line) => proxy.ObserveChildOutput(line);
            });
            Assert.True(await proxy.WaitUntilBoundAsync(TimeSpan.FromSeconds(5), cts.Token));

            await test(proxy, cts.Token);
        }
        finally { File.Delete(path); }
    }

    private static async Task ServeTraceAsync(TcpListener origin, X509Certificate2 cert, CancellationToken ct)
    {
        using var connection = await origin.AcceptTcpClientAsync(ct);
        using var stream = new SslStream(connection.GetStream());
        await stream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = cert }, ct);
        await ReadOriginRequestAsync(stream, "/trace", ct);
        const string body = "ip=198.51.100.42\n";
        var reply = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\n\r\n{body}");
        await stream.WriteAsync(reply, ct);
        await stream.ShutdownAsync().WaitAsync(ct);
    }

    private static async Task ReadOriginRequestAsync(Stream stream, string path, CancellationToken ct)
    {
        using var reader = new StreamReader(stream, leaveOpen: true);
        Assert.Equal($"GET {path} HTTP/1.1", await reader.ReadLineAsync(ct));
        while (await reader.ReadLineAsync(ct) is { Length: > 0 } header)
            Assert.False(header.StartsWith("Proxy-Authorization:", StringComparison.OrdinalIgnoreCase));
    }
}
