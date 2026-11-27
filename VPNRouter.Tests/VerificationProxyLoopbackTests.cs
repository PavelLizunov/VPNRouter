using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using VPNRouter.Core;
using VPNRouter.Core.Models;
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReachableVlessPortOnlyPassesTraceProbeWithWorkingOutbound(bool validUser)
    {
        using var vlessPort = new TcpListener(IPAddress.Loopback, 0);
        using var directTrap = new TcpListener(IPAddress.Loopback, 0);
        using var origin = new TcpListener(IPAddress.Loopback, 0);
        vlessPort.Start();
        directTrap.Start();
        origin.Start();
        var serverEndpoint = (IPEndPoint)vlessPort.LocalEndpoint;
        var trapEndpoint = (IPEndPoint)directTrap.LocalEndpoint;
        var originEndpoint = (IPEndPoint)origin.LocalEndpoint;
        var user = Guid.NewGuid().ToString();

        await WithOwnedProxyAsync(async (proxy, ct) =>
        {
            using (var tcp = new TcpClient())
                await tcp.ConnectAsync(serverEndpoint.Address, serverEndpoint.Port, ct);

            var trace = DeepVerifyProbe.ProbeViaProxyAsync(
                proxy, TimeSpan.FromSeconds(4), ct, $"http://{trapEndpoint}/trace");
            if (validUser)
            {
                using var connection = await origin.AcceptTcpClientAsync(ct);
                await ReadOriginRequestAsync(connection.GetStream(), "/trace", ct);
                await ReplyAsync(connection, "200 OK", ct, "ip=198.51.100.42\n");
            }

            var result = await trace;
            Assert.False(directTrap.Pending(), "The verifier attempted to connect directly to the trace URL.");
            Assert.False(origin.Pending(), "The origin received an unexpected connection.");
            if (validUser) Assert.True(result.ok, result.err);
            else Assert.False(result.ok, "TCP reachability must not pass verification for a rejected VLESS user.");
        }, proxy =>
        {
            vlessPort.Stop();
            var entry = new VlessServerEntry
            {
                Server = serverEndpoint.Address.ToString(),
                Port = serverEndpoint.Port,
                Uuid = validUser ? user : Guid.NewGuid().ToString(),
            };
            var config = JsonNode.Parse(VlessDeepVerifier.BuildSingleOutboundConfig(entry, proxy))!.AsObject();
            config["inbounds"]!.AsArray().Insert(0, new JsonObject
            {
                ["type"] = "vless",
                ["tag"] = "fixture-vless",
                ["listen"] = entry.Server,
                ["listen_port"] = entry.Port,
                ["users"] = new JsonArray(new JsonObject { ["uuid"] = user }),
            });
            // Only authenticated VLESS traffic can reach the origin behind the trap URL.
            var rules = config["route"]!["rules"]!.AsArray();
            rules.Insert(0, new JsonObject
            {
                ["inbound"] = new JsonArray("fixture-vless"),
                ["ip_cidr"] = new JsonArray("127.0.0.1/32"),
                ["port"] = new JsonArray(trapEndpoint.Port),
                ["action"] = "route",
                ["outbound"] = "dns-direct-out",
                ["override_port"] = originEndpoint.Port,
            });
            rules.Insert(1, new JsonObject { ["inbound"] = new JsonArray("fixture-vless"), ["action"] = "reject" });
            var dns = config["dns"]!["servers"]![0]!;
            dns["server"] = "127.0.0.1";
            dns["server_port"] = trapEndpoint.Port;
            return config;
        });
    }

    [Theory]
    [InlineData("204 No Content", true)]
    [InlineData("200 OK", false)]
    [InlineData("302 Found", false)]
    public Task SelectionControlRequiresExactNoContentResponse(string status, bool expected) =>
        WithOwnedProxyAsync(async (proxy, ct) =>
        {
            using var origin = new TcpListener(IPAddress.Loopback, 0);
            origin.Start();
            var probe = DeepVerifyProbe.ProbeViaProxyAsync(proxy, TimeSpan.FromSeconds(5), ct,
                $"http://{origin.LocalEndpoint}/control", expectNoContent: true);
            using var connection = await origin.AcceptTcpClientAsync(ct);
            await ReadOriginRequestAsync(connection.GetStream(), "/control", ct);
            await ReplyAsync(connection, status, ct);

            Assert.Equal(expected, (await probe).ok);
        });

    [Fact]
    public async Task SelectionStunRetriesThroughTheSingBoxUdpInbound()
    {
        using var target = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var targetPort = ((IPEndPoint)target.Client.LocalEndPoint!).Port;
        var probePort = NetPortUtil.FindFreeUdpPort();
        await WithOwnedProxyAsync(async (_, ct) =>
        {
            var probe = DeepVerifyProbe.ProbeStunAsync(probePort, ct);
            var dropped = await target.ReceiveAsync(ct);
            var retried = await target.ReceiveAsync(ct);
            Assert.Equal(dropped.Buffer, retried.Buffer);

            var response = new byte[32];
            retried.Buffer.CopyTo(response, 0);
            response[0] = 1;
            response[3] = 12;
            response[21] = 0x20;
            response[23] = 8;
            response[25] = 1;
            await target.SendAsync(response.AsMemory(), retried.RemoteEndPoint, ct);
            Assert.True(await probe);
        }, proxy =>
        {
            var config = JsonNode.Parse(VlessDeepVerifier.BuildSingleOutboundConfig(
                VlessDeepVerifierTests.CleanVlessEntry(), proxy, udpPort: probePort))!.AsObject();
            var inbound = config["inbounds"]![1]!;
            inbound["override_address"] = "127.0.0.1";
            inbound["override_port"] = targetPort;
            config["outbounds"]![0] = new JsonObject { ["type"] = "direct", ["tag"] = "proxy" };
            return config;
        });
    }

    private static async Task ReplyAsync(TcpClient client, string statusAndHeaders, CancellationToken ct, string body = "")
    {
        var response = $"HTTP/1.1 {statusAndHeaders}\r\nConnection: close\r\nContent-Length: {body.Length}\r\n\r\n{body}";
        await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes(response), ct);
    }

    private static async Task WithOwnedProxyAsync(Func<VerificationProxy, CancellationToken, Task> test,
        Func<VerificationProxy, JsonObject>? configure = null)
    {
        var runtime = Environment.GetEnvironmentVariable("VPNROUTER_TEST_VERIFIER_RUNTIME");
        if (OperatingSystem.IsLinux() && Environment.GetEnvironmentVariable("VPNROUTER_EXPECT_BUSINESS_POLICY_TESTS") == "1")
            Assert.False(string.IsNullOrEmpty(runtime), "Business CI requires the pinned sing-box fixture.");
        Assert.SkipUnless(!string.IsNullOrEmpty(runtime), "Requires the verified sing-box fixture");
        Assert.True(File.Exists(runtime));

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(20));
        var proxy = new VerificationProxy(NetPortUtil.FindFreePort());
        var config = configure?.Invoke(proxy) ?? new JsonObject
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
