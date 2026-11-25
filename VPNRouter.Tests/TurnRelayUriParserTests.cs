using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;
using Xunit;

namespace VPNRouter.Tests;

[Collection("SingBoxFeaturesSerial")]
public sealed class TurnRelayUriParserTests : IDisposable
{
    private readonly bool? _previous;

    public TurnRelayUriParserTests()
    {
        SingBoxFeatures.ResetForTests();
        _previous = SingBoxFeatures.OverrideTurnRelay;
        SingBoxFeatures.OverrideTurnRelay = true;
    }

    public void Dispose()
    {
        SingBoxFeatures.OverrideTurnRelay = _previous;
        SingBoxFeatures.ResetForTests();
    }

    private static readonly string Fingerprint = new('a', 64);

    private static string Link(
        string id = "turnrelay-relay-1",
        string name = "Relay one",
        Dictionary<string, object?>? overrides = null)
    {
        var payload = new Dictionary<string, object?>
        {
            ["id"] = id,
            ["provider"] = "static",
            ["server_type"] = "exit",
            ["server"] = "203.0.113.20",
            ["server_port"] = 53492,
            ["turn_server"] = "192.0.2.10:3478",
            ["turn_username"] = "user",
            ["turn_password"] = "secret",
            ["password"] = "exit-secret",
            ["server_fingerprint"] = Fingerprint,
            ["connections"] = 2,
            ["mode"] = "dtls",
            ["udp"] = true,
        };
        if (overrides != null)
            foreach (var pair in overrides)
                payload[pair.Key] = pair.Value;
        return BuildLink(payload, name);
    }

    private static string BuildLink(Dictionary<string, object?> payload, string name)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        var encoded = Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"turnrelay://{encoded}#{Uri.EscapeDataString(name)}";
    }

    [Fact]
    public void Parse_DefaultsAndIdentity_MatchLockedContract()
    {
        var uri = Link(overrides: new Dictionary<string, object?>
        {
            ["provider"] = null,
            ["server_type"] = null,
            ["connections"] = 0,
            ["mode"] = null,
            ["udp"] = null,
            ["server_fingerprint"] = Fingerprint.ToUpperInvariant(),
        });

        var entry = ServerUriParser.Parse(uri);

        Assert.Equal("turnrelay", entry.Protocol);
        Assert.Equal("Relay one", entry.Name);
        Assert.Equal("turnrelay-relay-1", entry.OutboundId);
        Assert.True(entry.IsTurnRelay);
        Assert.Equal("203.0.113.20", entry.Server);
        Assert.Equal(53492, entry.Port);
        Assert.Equal(TurnRelayConfig.DefaultProvider, entry.TurnRelay!.Provider);
        Assert.Equal(TurnRelayConfig.DefaultServerType, entry.TurnRelay.ServerType);
        Assert.Equal(TurnRelayConfig.DefaultConnections, entry.TurnRelay.Connections);
        Assert.Equal(TurnRelayConfig.DefaultMode, entry.TurnRelay.Mode);
        Assert.True(entry.TurnRelay.Udp);
        Assert.Equal(Fingerprint, entry.TurnRelay.ServerFingerprint);
        Assert.Equal("192.0.2.10:3478", entry.TurnRelay.TurnServer);
        Assert.Equal("exit-secret", entry.TurnRelay.Password);
        Assert.True(ServerUriParser.IsSupportedScheme(uri));
    }

    [Fact]
    public void Parse_ExplicitValues_ArePreserved()
    {
        var entry = ServerUriParser.Parse(Link());

        Assert.Equal("static", entry.TurnRelay!.Provider);
        Assert.Equal("exit", entry.TurnRelay.ServerType);
        Assert.Equal(2, entry.TurnRelay.Connections);
        Assert.Equal("dtls", entry.TurnRelay.Mode);
        Assert.Equal("user", entry.TurnRelay.TurnUsername);
        Assert.Equal("secret", entry.TurnRelay.TurnPassword);
        Assert.Equal(Fingerprint, entry.TurnRelay.ServerFingerprint);
    }

    [Theory]
    [InlineData("provider", "other")]
    [InlineData("server_type", "entry")]
    [InlineData("mode", "tcp")]
    [InlineData("connections", 61)]
    [InlineData("connections", -1)]
    [InlineData("server", "relay.example.test")]
    [InlineData("server", "0.0.0.0")]
    [InlineData("server", "::")]
    [InlineData("server_port", 0)]
    [InlineData("server_port", 70000)]
    [InlineData("turn_server", "192.0.2.10")]
    [InlineData("turn_server", "192.0.2.10:0")]
    [InlineData("turn_username", "")]
    [InlineData("turn_password", "")]
    [InlineData("password", "")]
    [InlineData("server_fingerprint", "abcd")]
    [InlineData("server_fingerprint", "g" + "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcde")]
    [InlineData("udp", "yes")]
    public void Parse_RejectsInvalidFields(string field, object value)
    {
        var uri = Link(overrides: new Dictionary<string, object?> { [field] = value });
        Assert.Throws<FormatException>(() => ServerUriParser.Parse(uri));
    }

    [Fact]
    public void Parse_RejectsDuplicateKeys()
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?>
        {
            ["id"] = "turnrelay-a",
            ["server"] = "203.0.113.20",
            ["server_port"] = 443,
        });
        var text = Encoding.UTF8.GetString(json).Insert(
            json.Length - 1,
            ",\"server\":\"203.0.113.21\"");
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(text))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        Assert.Throws<FormatException>(() => ServerUriParser.Parse($"turnrelay://{encoded}#Relay"));
    }

    [Fact]
    public void Parse_RejectsControlCharactersInDisplayName()
    {
        var uri = Link(name: "Relay\u0007Name");
        Assert.Throws<FormatException>(() => ServerUriParser.Parse(uri));
    }

    [Theory]
    [InlineData("bad id")]
    [InlineData("bad/id")]
    [InlineData("")]
    public void Parse_RejectsInvalidOutboundId(string id)
    {
        var uri = Link(id: id);
        Assert.Throws<FormatException>(() => ServerUriParser.Parse(uri));
    }

    [Fact]
    public void Parse_RejectsOversizedPayload()
    {
        var payload = new string('a', 8200);
        Assert.Throws<FormatException>(() =>
            ServerUriParser.Parse($"turnrelay://{payload}#Relay"));
    }

    [Fact]
    public void Parse_RequiresTurnRelayCapability()
    {
        SingBoxFeatures.OverrideTurnRelay = false;
        Assert.False(ServerUriParser.IsSupportedScheme(Link()));
        Assert.Throws<FormatException>(() => ServerUriParser.Parse(Link()));
    }

    [Fact]
    public void ChainOutput_EmitsTurnRelayAsChainEntry_AndVlessTargetAsProxy()
    {
        var hop = ServerUriParser.Parse(Link());
        var target = new VlessServerEntry
        {
            Name = "Exit",
            Protocol = "vless",
            Server = "exit.example.test",
            Port = 443,
            Uuid = "11111111-1111-1111-1111-111111111111",
            Security = "tls",
            OutboundId = "vless-target-1",
            DetourVia = hop.OutboundId,
        };
        var settings = Settings(hop, target);

        var config = ConfigGenerator.Generate(
            new Profile { Name = "t", DnsMode = "vpn_only" }, Array.Empty<string>(), settings);

        Assert.Equal(4, config.Outbounds.Count);
        var proxy = Assert.Single(config.Outbounds, o => o.Tag == "proxy");
        Assert.Equal("vless", proxy.Type);
        Assert.Equal("chain-entry", proxy.Detour);

        var entry = Assert.Single(config.Outbounds, o => o.Tag == "chain-entry");
        Assert.Equal("turnrelay", entry.Type);
        Assert.Equal("203.0.113.20", entry.Server);
        Assert.Equal(53492, entry.ServerPort);
        Assert.Equal("static", entry.TurnRelayProvider);
        Assert.Equal("exit", entry.TurnRelayServerType);
        Assert.Equal("192.0.2.10:3478", entry.TurnServer);
        Assert.Equal("user", entry.TurnUsername);
        Assert.Equal("secret", entry.TurnPassword);
        Assert.Equal("exit-secret", entry.Password);
        Assert.Equal(Fingerprint, entry.TurnRelayServerFingerprint);
        Assert.Equal(2, entry.TurnRelayConnections);
        Assert.Equal("dtls", entry.TurnRelayMode);
        Assert.True(entry.TurnRelayUdp);
        Assert.Equal("local-dns", entry.DomainResolver!.Server);
        Assert.Equal("prefer_ipv4", entry.DomainResolver.Strategy);
        Assert.Same(hop, entry.SourceServer);

        Assert.DoesNotContain(config.Outbounds, o => o.Type == "turnrelay" && o.Tag != "chain-entry");
    }

    [Fact]
    public void StandaloneTurnRelaySelection_FailsClosed()
    {
        var hop = ServerUriParser.Parse(Link());
        var settings = Settings(hop, null, active: hop.Name);

        Assert.Throws<InvalidOperationException>(() => ConfigGenerator.Generate(
            new Profile { Name = "t", DnsMode = "vpn_only" }, Array.Empty<string>(), settings));
    }

    [Fact]
    public void VlessConfig_ExcludesTurnRelayHopFromPools()
    {
        var hop = ServerUriParser.Parse(Link());
        var plain = new VlessServerEntry
        {
            Name = "Plain", Protocol = "vless", Server = "edge.example.test", Port = 443,
            Uuid = "11111111-1111-1111-1111-111111111111",
        };
        var config = new VlessConfig
        {
            AutoSelectBestServer = true,
            Servers = [hop, plain],
            ActiveServer = plain.Name,
        };

        var active = config.GetActiveServers();
        Assert.DoesNotContain(active, server => server.IsTurnRelay);
        Assert.False(SubscriptionServerPreference.CanAutoSelect(hop));
        Assert.True(SubscriptionChainSelection.IsUsableChainUpstream(hop));
        Assert.False(SubscriptionChainSelection.IsUsableChainTarget(hop));
    }

    [Theory]
    [InlineData(false, false, "detour-v1, vmess-v1")]
    [InlineData(true, false, "detour-v1, dnstt-v1, vmess-v1")]
    [InlineData(false, true, "detour-v1, turnrelay-v1, vmess-v1")]
    [InlineData(true, true, "detour-v1, dnstt-v1, turnrelay-v1, vmess-v1")]
    public async Task SubscriptionHeaders_AdvertiseTurnRelayOnlyOverHttps(
        bool dnstt, bool turnrelay, string expected)
    {
        var previousHttp = SubscriptionFetcher.Http;
        var fake = new FakeHttpClient().Setup("https://provider.example/api", string.Empty);
        SubscriptionFetcher.Http = fake;
        SingBoxFeatures.OverrideDnstt = dnstt;
        SingBoxFeatures.OverrideTurnRelay = turnrelay;
        try
        {
            await SubscriptionFetcher.FetchAsync(
                "https://provider.example/api", ct: TestContext.Current.CancellationToken);
            Assert.Equal(expected, Assert.Single(fake.SentRequests).Headers!["X-VPNRouter-Capabilities"]);
        }
        finally { SubscriptionFetcher.Http = previousHttp; }
    }

    [Fact]
    public void Diagnostics_RedactTurnRelayProfile()
    {
        var uri = Link();
        Assert.Equal("turnrelay://[redacted]", CanaryPolicy.RedactUrl(uri));
        var redacted = VPNRouter.Core.Services.Diagnostics.DiagnosticsRedactor.RedactLogText(
            $"turn_username=user turn_password=secret uri={uri}");
        Assert.DoesNotContain("secret", redacted);
        Assert.DoesNotContain("turnrelay-relay-1", redacted);
    }

    private static AppSettings Settings(VlessServerEntry hop, VlessServerEntry? target, string? active = null) => new()
    {
        App = new AppConfig { LogLevel = "info", RoutingMode = "full", BypassRussianTraffic = false },
        Dns = new DnsSettings { VpnDns = "https://1.1.1.1/dns-query" },
        SingBox = new SingBoxSettings(),
        Tun = new TunSettings(),
        Vless = new VlessConfig
        {
            ActiveServer = active ?? target?.Name ?? hop.Name,
            Servers = target == null ? [hop] : [hop, target],
        },
    };
}
