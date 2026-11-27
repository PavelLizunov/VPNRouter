using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;

namespace VPNRouter.Tests;

[Collection(SubscriptionFetcherCollection.Name)]
public sealed class NativeTransportSubscriptionTests : IDisposable
{
    private const string Url = "https://feed.example.test/sub";
    private static readonly string Pin = new('a', 64);
    private static string TrustLink => $"trusttunnel://pilot:fixture%3Asecret@[2001:db8::2]:8444?sni=edge.example.test&quic=1&udp=0&pk_sha256={Pin}#Trust";
    private const string MieruLink = "mieru://pilot:fixture%40secret@edge.example.test:8445?transport=udp&multiplexing=middle&handshake=no_wait#Mieru";
    private readonly IHttpClient _http = SubscriptionFetcher.Http;
    private readonly bool? _trust = SingBoxFeatures.OverrideTrustTunnel;
    private readonly bool? _mieru = SingBoxFeatures.OverrideMieru;
    private readonly bool? _dnstt = SingBoxFeatures.OverrideDnstt;
    private readonly bool? _relay = SingBoxFeatures.OverrideTurnRelay;

    public NativeTransportSubscriptionTests()
    {
        SingBoxFeatures.OverrideTrustTunnel = true;
        SingBoxFeatures.OverrideMieru = true;
        SingBoxFeatures.OverrideDnstt = false;
        SingBoxFeatures.OverrideTurnRelay = false;
    }

    public void Dispose()
    {
        SubscriptionFetcher.Http = _http;
        SingBoxFeatures.OverrideTrustTunnel = _trust;
        SingBoxFeatures.OverrideMieru = _mieru;
        SingBoxFeatures.OverrideDnstt = _dnstt;
        SingBoxFeatures.OverrideTurnRelay = _relay;
    }

    [Theory]
    [InlineData("1.14.5-vpnctl.1", true)]
    [InlineData("1.14.4-vpnctl.9", false)]
    [InlineData("1.14.5", false)]
    public void EmbeddedCapabilityRequiresTheForkRelease(string version, bool supported) =>
        Assert.Equal(supported, SingBoxFeatures.HasNativeTransportVersion(version));

    [Theory]
    [InlineData(false, false, "detour-v1, vmess-v1", 0)]
    [InlineData(true, false, "detour-v1, vmess-v1, trusttunnel-v1", 1)]
    [InlineData(false, true, "detour-v1, vmess-v1, mieru-v1", 1)]
    [InlineData(true, true, "detour-v1, vmess-v1, trusttunnel-v1, mieru-v1", 2)]
    public async Task FetchAdvertisesOnlySupportedTransports(bool trust, bool mieru, string header, int count)
    {
        SingBoxFeatures.OverrideTrustTunnel = trust;
        SingBoxFeatures.OverrideMieru = mieru;
        var body = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{TrustLink}\n{MieruLink}"));
        var fake = new FakeHttpClient().Setup(Url, JsonSerializer.Serialize(new { config = body }));
        SubscriptionFetcher.Http = fake;

        var entries = await SubscriptionFetcher.FetchAsync(Url, ct: TestContext.Current.CancellationToken);

        Assert.Equal(count, entries.Count);
        Assert.Equal(header, Assert.Single(fake.SentRequests).Headers!["X-VPNRouter-Capabilities"]);
    }

    [Theory]
    [InlineData("https", "detour-v1, vmess-v1, trusttunnel-v1, mieru-v1")]
    [InlineData("http", "detour-v1, vmess-v1")]
    public async Task CleartextResponseDoesNotImportNativeProfiles(string scheme, string expectedHeader)
    {
        var url = $"{scheme}://feed.example.test/sub";
        var response = new HttpResponse(200, new Dictionary<string, string>(),
            Encoding.UTF8.GetBytes($"{TrustLink}\n{MieruLink}"), TimeSpan.Zero)
        {
            FinalUri = new Uri("http://feed.example.test/sub"),
        };
        var fake = new FakeHttpClient().Setup(url, response);
        SubscriptionFetcher.Http = fake;

        Assert.Empty(await SubscriptionFetcher.FetchAsync(url, ct: TestContext.Current.CancellationToken));
        Assert.Equal(expectedHeader, Assert.Single(fake.SentRequests).Headers!["X-VPNRouter-Capabilities"]);
    }

    [Fact]
    public async Task RefreshAndGeneratePreserveEndpointCredentialsAndOptions()
    {
        var feed = new SubscriptionEntry { Url = Url };
        SubscriptionFetcher.Http = new FakeHttpClient().Setup(Url, $"{TrustLink}\n{MieruLink}");
        Assert.Equal(2, await SubscriptionFetcher.RefreshEntryAsync(feed, ct: TestContext.Current.CancellationToken));
        SubscriptionFetcher.Http = new FakeHttpClient().Setup(Url, $"{MieruLink.Replace("fixture%40secret", "rotated%3Avalue")}\n{TrustLink}");
        Assert.Equal(2, await SubscriptionFetcher.RefreshEntryAsync(feed, ct: TestContext.Current.CancellationToken));

        var trust = Generate(feed.Servers.Single(s => s.Protocol == "trusttunnel"));
        var tls = trust.GetProperty("tls");
        Assert.Equal("2001:db8::2", trust.GetProperty("server").GetString());
        Assert.Equal("fixture:secret", trust.GetProperty("password").GetString());
        Assert.Equal("edge.example.test", tls.GetProperty("server_name").GetString());
        Assert.False(tls.GetProperty("insecure").GetBoolean());
        Assert.Equal(Convert.ToBase64String(Convert.FromHexString(Pin)),
            tls.GetProperty("certificate_public_key_sha256")[0].GetString());
        Assert.True(trust.GetProperty("quic").GetBoolean());
        Assert.False(trust.GetProperty("udp").GetBoolean());

        var mieru = Generate(feed.Servers.Single(s => s.Protocol == "mieru"));
        Assert.Equal("rotated:value", mieru.GetProperty("password").GetString());
        Assert.Equal(8445, mieru.GetProperty("port_bindings")[0].GetProperty("port").GetInt32());
        Assert.Equal("udp", mieru.GetProperty("port_bindings")[0].GetProperty("transport").GetString());
        Assert.Equal("middle", mieru.GetProperty("multiplexing").GetString());
        Assert.Equal("no_wait", mieru.GetProperty("handshake_mode").GetString());
    }

    [Fact]
    public async Task RefreshRebindsThePinnedProfileAfterCredentialRotation()
    {
        var selected = ServerUriParser.Parse(MieruLink);
        var feed = new SubscriptionEntry { Url = Url, Servers = [selected] };
        var settings = new AppSettings();
        settings.App.ConfigMode = "subscribe";
        settings.App.Subscriptions = [feed];
        settings.App.SubscriptionPreference = new SubscriptionServerPreference
        {
            FeedId = feed.Id,
            RowKey = SubscriptionEntry.RowKey(selected),
        };
        var replacement = MieruLink.Replace("fixture%40secret", "rotated%3Avalue");
        var sameEndpointDifferentProtocol = TrustLink.Replace("[2001:db8::2]:8444", "edge.example.test:8445")
            .Replace("#Trust", "#Mieru");
        SubscriptionFetcher.Http = new FakeHttpClient().Setup(Url, $"{sameEndpointDifferentProtocol}\n{replacement}");

        await SubscriptionResolver.ResolveAsync(settings, refreshFromNetwork: true,
            ct: TestContext.Current.CancellationToken);

        var pinned = Assert.Single(VlessServersResolver.GetActiveServers(settings));
        Assert.Equal("mieru", pinned.Protocol);
        Assert.Equal("rotated:value", pinned.Password);
        Assert.Equal(SubscriptionEntry.RowKey(pinned), settings.App.SubscriptionPreference!.RowKey);
    }

    [Fact]
    public void NativeJsonSubscriptionRetainsTheValidatedProtocolOptions()
    {
        var trust = Generate(ServerUriParser.Parse(TrustLink));
        var mieru = Generate(ServerUriParser.Parse(MieruLink));
        var body = JsonSerializer.Serialize(new { outbounds = new[] { trust, mieru } });

        var rows = SubscriptionFetcher.ParseBody(body);
        Assert.Equal(2, rows.Count);
        Assert.Equal(Convert.ToBase64String(Convert.FromHexString(Pin)),
            rows.Single(row => row.Protocol == "trusttunnel").TrustTunnel!.CertificatePublicKeySha256);
        Assert.Equal("no_wait", rows.Single(row => row.Protocol == "mieru").Mieru!.HandshakeMode);
    }

    [Fact]
    public void InvalidProfilesFailClosedAndSensitiveLinksAreRedacted()
    {
        Assert.Null(ServerUriParser.TryParse(TrustLink.Replace(Pin, "1234")));
        Assert.Null(ServerUriParser.TryParse(TrustLink.Replace("pilot:", "pilot%3Ajob:")));
        Assert.Null(ServerUriParser.TryParse(TrustLink.Replace("udp=0", "udp=0&udp=1")));
        Assert.Null(ServerUriParser.TryParse(MieruLink.Replace("transport=udp", "transport=quic")));
        Assert.DoesNotContain("fixture", CanaryPolicy.RedactUrl(TrustLink));
        Assert.DoesNotContain("fixture", CanaryPolicy.RedactUrl(MieruLink));
    }

    private static JsonElement Generate(VlessServerEntry row)
    {
        var settings = new AppSettings { Vless = new VlessConfig { Servers = [row], ActiveServer = row.Name } };
        var config = ConfigGenerator.Generate(new Profile { Name = "Fixture", DnsMode = "vpn_only" }, [], settings);
        using var json = JsonDocument.Parse(ConfigGenerator.Serialize(config));
        return json.RootElement.GetProperty("outbounds").EnumerateArray()
            .Single(o => o.GetProperty("type").GetString() == row.Protocol).Clone();
    }

    [Fact]
    public void SelectingNativeProfileDoesNotGroupOtherProtocolsAtTheSameAddress()
    {
        var trust = ServerUriParser.Parse(TrustLink.Replace("[2001:db8::2]", "edge.example.test"));
        var mieru = ServerUriParser.Parse(MieruLink);
        var config = new VlessConfig { Servers = [trust, mieru], ActiveServer = trust.Name };
        Assert.Same(trust, Assert.Single(config.GetActiveServers()));
    }

    [Theory]
    [InlineData("trusttunnel")]
    [InlineData("mieru")]
    public async Task Native_TransportImport_ReleasedCoreAcceptsGeneratedConfig(string protocol)
    {
        var runtime = Environment.GetEnvironmentVariable("VPNCTL_TEST_CORE")
            ?? Environment.GetEnvironmentVariable("VPNROUTER_TEST_VERIFIER_RUNTIME");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(runtime), "Requires the pinned sing-box runtime");
        Assert.True(File.Exists(runtime));
        var row = ServerUriParser.Parse(protocol == "trusttunnel" ? TrustLink : MieruLink);
        var settings = new AppSettings { Vless = new VlessConfig { Servers = [row], ActiveServer = row.Name } };
        settings.App.RoutingMode = "full";
        var config = ConfigGenerator.Serialize(ConfigGenerator.Generate(
            new Profile { Name = "Fixture", DnsMode = "vpn_only" }, [], settings));
        var result = await new ProcessRunner().RunAsync(new ProcessRequest(runtime!, ["check", "-c", "stdin"],
            StdinInput: config, Timeout: TimeSpan.FromSeconds(15)), TestContext.Current.CancellationToken);
        Assert.False(result.TimedOut);
        Assert.True(result.ExitCode == 0, $"Native core rejected generated {protocol} config.");
    }
    [Theory]
    [InlineData("trusttunnel", false)]
    [InlineData("trusttunnel", true)]
    [InlineData("mieru", false)]
    [InlineData("mieru", true)]
    public async Task Native_TransportSubscription_TransfersTraffic(string protocol, bool useUdpTransport)
    {
        var runtime = Environment.GetEnvironmentVariable("VPNCTL_TEST_CORE")
            ?? Environment.GetEnvironmentVariable("VPNROUTER_TEST_VERIFIER_RUNTIME");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(runtime), "Requires the pinned sing-box runtime");
        Assert.True(File.Exists(runtime));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var ct = timeout.Token;

        using var origin = new TcpListener(IPAddress.Loopback, 0);
        origin.Start();
        var originPort = ((IPEndPoint)origin.LocalEndpoint).Port;
        var serverPort = protocol == "mieru" && useUdpTransport
            ? NetPortUtil.FindFreeUdpPort() : NetPortUtil.FindFreePort();
        var transport = useUdpTransport ? "udp" : "tcp";
        var inbound = new JsonObject
        {
            ["type"] = protocol,
            ["tag"] = "fixture",
            ["listen"] = "127.0.0.1",
            ["listen_port"] = serverPort,
            ["users"] = new JsonArray(new JsonObject { ["username"] = "fixture-user", ["password"] = "fixture-password" }),
        };
        var url = $"{protocol}://fixture-user:fixture-password@127.0.0.1:{serverPort}";
        if (protocol == "trusttunnel")
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var certRequest = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256);
            var now = DateTimeOffset.UtcNow;
            using var cert = certRequest.CreateSelfSigned(now.AddMinutes(-5), now.AddMinutes(30));
            inbound["quic"] = useUdpTransport;
            inbound["tls"] = new JsonObject
            {
                ["enabled"] = true,
                ["certificate"] = cert.ExportCertificatePem(),
                ["key"] = key.ExportPkcs8PrivateKeyPem(),
            };
            var spki = Convert.ToHexString(SHA256.HashData(key.ExportSubjectPublicKeyInfo()));
            url += $"?sni=localhost&quic={(useUdpTransport ? 1 : 0)}&udp=1&pk_sha256={spki}";
        }
        else
        {
            inbound["transport"] = transport;
            url += $"?transport={transport}&multiplexing=middle&handshake=standard";
        }

        var serverConfig = new JsonObject
        {
            ["inbounds"] = new JsonArray(inbound),
            ["outbounds"] = new JsonArray(new JsonObject { ["type"] = "direct", ["tag"] = "origin" }),
            ["route"] = new JsonObject { ["final"] = "origin" },
        };
        SubscriptionFetcher.Http = new FakeHttpClient().Setup(Url, url);
        var row = Assert.Single(await SubscriptionFetcher.FetchAsync(Url, ct: ct));
        var proxy = new VerificationProxy(NetPortUtil.FindFreePort());
        var proxyConfig = VlessDeepVerifier.BuildSingleOutboundConfig(row, proxy);
        var runner = new ProcessRunner();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = runner.Start(new ProcessRequest(runtime!, ["run", "-c", "stdin"],
            StdinInput: serverConfig.ToJsonString()), handle =>
        {
            handle.ErrorLine += (_, line) =>
            {
                if (line.Contains("sing-box started", StringComparison.Ordinal)) ready.TrySetResult();
            };
        });
        try
        {
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
            using var client = runner.Start(new ProcessRequest(runtime!, ["run", "-c", "stdin"],
                StdinInput: proxyConfig), handle => handle.ErrorLine += (_, line) => proxy.ObserveChildOutput(line));
            try
            {
                Assert.True(await proxy.WaitUntilBoundAsync(TimeSpan.FromSeconds(10), ct), proxy.StartupError);
                using var http = new HttpClient(proxy.CreateHttpHandler(TimeSpan.FromSeconds(5)));
                var fetch = http.GetStringAsync($"http://127.0.0.1:{originPort}/", ct);
                using var request = await origin.AcceptTcpClientAsync(ct);
                using var stream = request.GetStream();
                using var reader = new StreamReader(stream, leaveOpen: true);
                while (!string.IsNullOrEmpty(await reader.ReadLineAsync(ct))) { }
                await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nOK"u8.ToArray(), ct);
                Assert.Equal("OK", await fetch);
            }
            finally
            {
                if (!client.HasExited) client.Kill();
            }
        }
        finally
        {
            if (!server.HasExited) server.Kill();
        }
    }

}
