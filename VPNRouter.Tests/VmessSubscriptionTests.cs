#nullable enable
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

[Collection(SubscriptionFetcherCollection.Name)]
public sealed class VmessSubscriptionTests
{
    private const string UuidA = "00000000-0000-4000-8000-000000000101";
    private const string UuidB = "00000000-0000-4000-8000-000000000102";
    private const string FakePbk = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    private static Dictionary<string, object?> StandardPayload(
        string add = "198.51.100.7",
        string port = "443",
        string id = UuidA,
        string aid = "0",
        string scy = "aes-128-gcm",
        string net = "ws",
        string type = "none",
        string host = "cdn.example.com",
        string path = "/vm",
        string tls = "tls",
        string sni = "cdn.example.com",
        string alpn = "http/1.1",
        string? outbound = null,
        string? detour = null,
        string ps = "VMess Node")
    {
        var payload = new Dictionary<string, object?>
        {
            ["v"] = "2",
            ["ps"] = ps,
            ["add"] = add,
            ["port"] = port,
            ["id"] = id,
            ["aid"] = aid,
            ["scy"] = scy,
            ["net"] = net,
            ["type"] = type,
            ["host"] = host,
            ["path"] = path,
            ["tls"] = tls,
            ["sni"] = sni,
            ["alpn"] = alpn,
        };
        if (outbound is not null) payload["outbound"] = outbound;
        if (detour is not null) payload["detour"] = detour;
        return payload;
    }

    private static string VmessUri(object payload, string? fragment = null)
    {
        var encoded = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(payload));
        return "vmess://" + encoded + (fragment is null ? string.Empty : "#" + Uri.EscapeDataString(fragment));
    }

    private static string VlessUri(string server, string outbound, string name = "entry")
    {
        var query = "security=reality&sni=yahoo.com&fp=firefox" +
                    "&pbk=" + FakePbk + "&sid=deadbeef&spx=%2F&type=tcp" +
                    "&flow=xtls-rprx-vision&outbound=" + Uri.EscapeDataString(outbound);
        return $"vless://{UuidB}@{server}:443?{query}#{Uri.EscapeDataString(name)}";
    }

    private static AppSettings CreateSettings(params VlessServerEntry[] servers)
    {
        var settings = new AppSettings
        {
            App = new AppConfig { LogLevel = "info" },
            Tun = new TunSettings(),
            Dns = new DnsSettings { VpnDns = "https://1.1.1.1/dns-query" },
            SingBox = new SingBoxSettings(),
            Vless = new VlessConfig
            {
                Servers = new List<VlessServerEntry>(servers),
                ActiveServer = servers[0].Name,
            },
        };
        return settings;
    }

    private static Profile CreateProfile() => new()
    {
        Name = "VMessTestProfile",
        DnsMode = "vpn_only",
        Processes = new List<ProcessRule>
        {
            new() { Name = "Discord.exe", ScanPatterns = new[] { "Discord.exe" } },
        },
    };

    [Fact]
    public void Parse_StandardPayload_PreservesTlsHostAndAeadFields()
    {
        var entry = ServerUriParser.Parse(VmessUri(StandardPayload()));

        Assert.Equal("vmess", entry.Protocol);
        Assert.Equal("VMess Node", entry.Name);
        Assert.Equal("198.51.100.7", entry.Server);
        Assert.Equal(443, entry.Port);
        Assert.Equal(UuidA, entry.Uuid);
        Assert.Equal("aes-128-gcm", entry.VmessSecurity ?? string.Empty);
        Assert.Equal(0, entry.AlterId);
        Assert.Equal("tls", entry.Security);
        Assert.True(entry.Tls.Enabled);
        Assert.Equal("cdn.example.com", entry.Tls.ServerName);
        Assert.Equal("http/1.1", entry.Tls.Alpn);
        Assert.Equal("ws", entry.Transport.Type);
        Assert.Equal("/vm", entry.Transport.Path);
        Assert.Equal("cdn.example.com", entry.Transport.Headers["Host"]);
    }

    [Fact]
    public void Parse_ChainMetadata_PreservesOpaqueIds()
    {
        var entry = ServerUriParser.Parse(VmessUri(StandardPayload(
            outbound: "target-node-1", detour: "entry-node-1")));

        Assert.Equal("target-node-1", entry.OutboundId);
        Assert.Equal("entry-node-1", entry.DetourVia);
    }

    [Theory]
    [InlineData("cdn.example.com", "cdn.example.com")]
    [InlineData("", "198.51.100.7")]
    public void QuickProbe_UsesTlsNameOrDialHostForCachedVmessWithRealityDefaults(string tlsName, string expected)
    {
        var entry = ServerUriParser.Parse(VmessUri(StandardPayload()));
        entry.Reality = new VlessRealityConfig { Enabled = true, ServerName = "legacy.example.com" };
        entry.Tls.ServerName = tlsName;

        Assert.Equal(expected, TcpTlsProbe.ResolveTlsSni(entry, entry.Server));
    }

    [Fact]
    public void Parse_UrlSafeUnpaddedPayload_IsAccepted()
    {
        var encoded = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(StandardPayload()))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        var entry = ServerUriParser.Parse("vmess://" + encoded);

        Assert.Equal("vmess", entry.Protocol);
        Assert.Equal("198.51.100.7", entry.Server);
    }

    [Theory]
    [InlineData("1", "aes-128-gcm")]
    [InlineData("invalid", "aes-128-gcm")]
    [InlineData("0", "none")]
    [InlineData("0", "zero")]
    [InlineData("0", "aes-128-ctr")]
    public void Parse_RejectsLegacyAlterIdAndNonAeadCiphers(string aid, string scy)
    {
        var uri = VmessUri(StandardPayload(aid: aid, scy: scy));

        Assert.Throws<FormatException>(() => ServerUriParser.Parse(uri));
    }

    [Fact]
    public void Parse_RejectsInvalidUuidAndMalformedDetour()
    {
        Assert.Throws<FormatException>(() => ServerUriParser.Parse(VmessUri(StandardPayload(id: "invalid"))));

        var payload = StandardPayload();
        payload["detour"] = 123;
        Assert.Throws<FormatException>(() => ServerUriParser.Parse(VmessUri(payload)));
    }

    [Fact]
    public void ActiveVmessSelection_DoesNotGroupAnUnrelatedProtocolAtTheSameHost()
    {
        var vmess = ServerUriParser.Parse(VmessUri(StandardPayload()));
        var vless = ServerUriParser.Parse(VlessUri(vmess.Server, "entry"));
        var settings = CreateSettings(vmess, vless);
        settings.Vless.AutoSelectBestServer = false;

        Assert.Same(vmess, Assert.Single(settings.Vless.GetActiveServers()));
    }

    [Fact]
    public void Parse_RejectsUnsupportedTransport()
    {
        var uri = VmessUri(StandardPayload(net: "grpc"));

        Assert.Throws<FormatException>(() => ServerUriParser.Parse(uri));
    }

    [Fact]
    public void RedactUrl_DoesNotEchoVmessPayload()
    {
        var uri = VmessUri(StandardPayload());

        Assert.Equal("vmess://[redacted]", CanaryPolicy.RedactUrl(uri));
    }

    [Fact]
    public void ParseBody_Base64FeedImportsVmessNextToVlessAtSameEndpoint()
    {
        var payload = StandardPayload(add: "203.0.113.80", host: "cdn.example.com", sni: "cdn.example.com");
        var vless = $"vless://{UuidA}@203.0.113.80:443?security=none#ordinary";
        var vmess = VmessUri(payload);
        var body = Convert.ToBase64String(Encoding.UTF8.GetBytes(vless + "\n" + vmess));

        var rows = SubscriptionFetcher.ParseBody(body);

        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, row => row.Protocol == "vless");
        Assert.Contains(rows, row => row.Protocol == "vmess");
    }

    [Fact]
    public async Task RefreshEntryAsync_ImportsBothVmessRoutesFromTheExistingAppFeed()
    {
        const string url = "https://provider.example/api/v1/app/config/vmess";
        var hop = VlessUri("203.0.113.9", "entry-node-1");
        var chained = VmessUri(StandardPayload(outbound: "target-node-1", detour: "entry-node-1"));
        var direct = VmessUri(StandardPayload(add: "cdn.example.com", ps: "VMess Direct"));
        var config = Convert.ToBase64String(Encoding.UTF8.GetBytes(string.Join("\n", hop, chained, direct)));
        var body = JsonSerializer.Serialize(new { config });
        var previous = SubscriptionFetcher.Http;
        SubscriptionFetcher.Http = new FakeHttpClient().Setup(url, body);
        try
        {
            var entry = new SubscriptionEntry
            {
                Url = url,
                LastServerCount = 1,
                Servers = new List<VlessServerEntry>
                {
                    new() { Name = "old", Server = "203.0.113.1", Port = 443, Uuid = UuidB },
                },
            };

            var count = await SubscriptionFetcher.RefreshEntryAsync(
                entry, ct: TestContext.Current.CancellationToken);

            Assert.Equal(3, count);
            var refreshed = Assert.Single(entry.Servers, row => row.Name == "VMess Node");
            Assert.Equal("vmess", refreshed.Protocol);
            Assert.Equal("198.51.100.7", refreshed.Server);
            Assert.Equal("entry-node-1", refreshed.DetourVia);
            Assert.Contains(entry.Servers, row => row.Protocol == "vless" && row.OutboundId == "entry-node-1");
            Assert.Contains(entry.Servers, row => row.Name == "VMess Direct" && row.Server == "cdn.example.com");
        }
        finally
        {
            SubscriptionFetcher.Http = previous;
        }
    }

    [Fact]
    public void Generate_DirectEndpoint_EmitsDedicatedAeadTlsWsOutbound()
    {
        var entry = ServerUriParser.Parse(VmessUri(StandardPayload(
            add: "edge.example.com", host: "edge.example.com", sni: "edge.example.com", path: "/vmess")));
        var settings = CreateSettings(entry);

        var config = ConfigGenerator.Generate(
            CreateProfile(), new[] { "Discord.exe" }, settings);

        var proxy = Assert.Single(config.Outbounds, outbound => outbound.Tag == "proxy");
        Assert.Equal("vmess", proxy.Type);
        Assert.Equal("edge.example.com", proxy.Server);
        Assert.Equal(443, proxy.ServerPort!.Value);
        Assert.Equal(UuidA, proxy.Uuid);
        Assert.Equal("aes-128-gcm", proxy.VmessSecurity ?? string.Empty);
        Assert.Equal(0, proxy.AlterId!.Value);
        Assert.Equal("edge.example.com", proxy.Tls!.ServerName);
        Assert.Equal("ws", proxy.Transport!.Type);
        Assert.Equal("/vmess", proxy.Transport.Path);
        Assert.Equal("edge.example.com", proxy.Transport.Headers!["Host"]);
        Assert.Equal("local-dns", proxy.DomainResolver!.Server);

        using var document = JsonDocument.Parse(ConfigGenerator.Serialize(config));
        var proxyJson = document.RootElement.GetProperty("outbounds").EnumerateArray()
            .Single(outbound => outbound.GetProperty("tag").GetString() == "proxy");
        Assert.Equal("vmess", proxyJson.GetProperty("type").GetString());
        Assert.Equal("aes-128-gcm", proxyJson.GetProperty("security").GetString());
        Assert.Equal(0, proxyJson.GetProperty("alter_id").GetInt32());
        Assert.Equal("edge.example.com",
            proxyJson.GetProperty("tls").GetProperty("server_name").GetString());
        Assert.Equal("ws",
            proxyJson.GetProperty("transport").GetProperty("type").GetString());
        Assert.Equal("edge.example.com",
            proxyJson.GetProperty("transport").GetProperty("headers").GetProperty("Host").GetString());
        Assert.Equal("local-dns", proxyJson.GetProperty("domain_resolver").GetString());

        var imported = Assert.Single(SubscriptionFetcher.ParseBody(ConfigGenerator.Serialize(config)),
            row => row.Protocol == "vmess");
        Assert.Equal("edge.example.com", imported.Server);
        Assert.Equal("edge.example.com", imported.Tls.ServerName);
        Assert.Equal("/vmess", imported.Transport.Path);

        var sanity = new ConfigSanityCheck().CheckBeforeStart(
            System.Text.Json.Nodes.JsonNode.Parse(ConfigGenerator.Serialize(config))!.AsObject());
        Assert.False(sanity.IsDead, sanity.Reason);
    }

    [Fact]
    public void Generate_ChainedVmessTarget_UsesVlessEntryAndPublicCertificateHost()
    {
        var entry = ServerUriParser.Parse(VlessUri("203.0.113.9", "entry-node-1"));
        var target = ServerUriParser.Parse(VmessUri(StandardPayload(
            outbound: "target-node-1", detour: "entry-node-1"), "VMess Exit"));
        var settings = CreateSettings(entry, target);
        settings.Vless.ActiveServer = target.Name;

        var config = ConfigGenerator.Generate(
            CreateProfile(), new[] { "Discord.exe" }, settings);

        var proxy = Assert.Single(config.Outbounds, outbound => outbound.Tag == "proxy");
        Assert.Equal("vmess", proxy.Type);
        Assert.Equal("198.51.100.7", proxy.Server);
        Assert.Equal("cdn.example.com", proxy.Tls!.ServerName);
        Assert.Equal("cdn.example.com", proxy.Transport!.Headers!["Host"]);
        Assert.Equal("chain-entry", proxy.Detour);
        Assert.Equal("aes-128-gcm", proxy.VmessSecurity ?? string.Empty);
        Assert.Equal(0, proxy.AlterId!.Value);

        var upstream = Assert.Single(config.Outbounds, outbound => outbound.Tag == "chain-entry");
        Assert.Equal("vless", upstream.Type);
        Assert.Equal("203.0.113.9", upstream.Server);
        Assert.True(upstream.Tls!.Reality!.Enabled);
        Assert.Equal(FakePbk, upstream.Tls.Reality.PublicKey);

        using var document = JsonDocument.Parse(ConfigGenerator.Serialize(config));
        var proxyJson = document.RootElement.GetProperty("outbounds").EnumerateArray()
            .Single(outbound => outbound.GetProperty("tag").GetString() == "proxy");
        Assert.Equal("chain-entry", proxyJson.GetProperty("detour").GetString());
        Assert.Equal("cdn.example.com",
            proxyJson.GetProperty("tls").GetProperty("server_name").GetString());

        var sanity = new ConfigSanityCheck().CheckBeforeStart(
            System.Text.Json.Nodes.JsonNode.Parse(ConfigGenerator.Serialize(config))!.AsObject());
        Assert.False(sanity.IsDead, sanity.Reason);
    }

    [Fact]
    public void Generate_ChainedVmessTargetWithoutEntry_FailsClosed()
    {
        var target = ServerUriParser.Parse(VmessUri(StandardPayload(
            outbound: "target-node-1", detour: "missing-entry-node"), "VMess Exit"));
        var settings = CreateSettings(target);
        settings.Vless.ActiveServer = target.Name;

        var error = Assert.Throws<InvalidOperationException>(() =>
            ConfigGenerator.Generate(CreateProfile(), new[] { "Discord.exe" }, settings));

        Assert.Contains("upstream", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Generate_ChainedVmessTargetWithInvalidRealityEntry_FailsClosed()
    {
        var entry = ServerUriParser.Parse(VlessUri("203.0.113.9", "entry-node-1"));
        entry.Reality.PublicKey = "invalid";
        var target = ServerUriParser.Parse(VmessUri(StandardPayload(
            outbound: "target-node-1", detour: "entry-node-1"), "VMess Exit"));
        var settings = CreateSettings(entry, target);
        settings.Vless.ActiveServer = target.Name;

        var error = Assert.Throws<InvalidOperationException>(() =>
            ConfigGenerator.Generate(CreateProfile(), new[] { "Discord.exe" }, settings));

        Assert.Contains("unusable upstream", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ChainSelection_RefreshWithNewOpaqueIds_KeepsVmessTarget()
    {
        var oldEntry = ServerUriParser.Parse(VlessUri("203.0.113.9", "entry-old"));
        var oldTarget = ServerUriParser.Parse(VmessUri(StandardPayload(
            outbound: "target-old", detour: "entry-old"), "VMess Exit"));
        var feed = new SubscriptionEntry
        {
            Id = "feed-one",
            Url = "https://provider.example/sub",
            Servers = new List<VlessServerEntry> { oldEntry, oldTarget },
        };
        var selection = new SubscriptionChainSelection(
            feed.Id, feed.Url, oldTarget, SubscriptionEntry.RouteKey(oldTarget, feed.Servers));

        var newEntry = ServerUriParser.Parse(VlessUri("203.0.113.9", "entry-new"));
        var newTarget = ServerUriParser.Parse(VmessUri(StandardPayload(
            outbound: "target-new", detour: "entry-new"), "VMess Exit"));
        var refreshed = new SubscriptionEntry
        {
            Id = feed.Id,
            Url = feed.Url,
            Servers = new List<VlessServerEntry> { newEntry, newTarget },
        };

        var matched = selection.Match(refreshed, refreshed.Servers);

        Assert.Same(newTarget, matched);
    }

    [Fact]
    public void BuildSingleOutboundConfig_Vmess_EmitsVerifierShape()
    {
        var entry = ServerUriParser.Parse(VmessUri(StandardPayload()));

        var json = VlessDeepVerifier.BuildSingleOutboundConfig(
            entry, new VerificationProxy(10808));

        using var document = JsonDocument.Parse(json);
        var proxy = document.RootElement.GetProperty("outbounds").EnumerateArray()
            .Single(outbound => outbound.GetProperty("tag").GetString() == "proxy");
        Assert.Equal("vmess", proxy.GetProperty("type").GetString());
        Assert.Equal("aes-128-gcm", proxy.GetProperty("security").GetString());
        Assert.Equal(0, proxy.GetProperty("alter_id").GetInt32());
        Assert.Equal("cdn.example.com", proxy.GetProperty("tls").GetProperty("server_name").GetString());
        Assert.Equal("cdn.example.com",
            proxy.GetProperty("transport").GetProperty("headers").GetProperty("Host").GetString());
    }
}
