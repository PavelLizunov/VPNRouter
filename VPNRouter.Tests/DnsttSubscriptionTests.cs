#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;
using Xunit;

namespace VPNRouter.Tests;

[Collection(SubscriptionFetcherCollection.Name)]
public sealed class DnsttSubscriptionTests
{
    // A synthetic, disposable Ed25519 fixture accepted by the native SSH implementation.
    private const string FixturePublic = "AAAAC3NzaC1lZDI1NTE5AAAAINU/+FJlDgzz2zyObeOpgwHpVphkf9n/TFbWGDSi2xFF";
    private const string FixturePrivateBlob =
        "b3BlbnNzaC1rZXktdjEAAAAABG5vbmUAAAAEbm9uZQAAAAAAAAABAAAAMwAAAAtzc2gtZWQyNTUxOQAAACDVP/hSZQ4M89s8jm3jqYMB6VaYZH/Z/0xW1hg0otsRRQAAAJg0xLT4NMS0+AAAAAtzc2gtZWQyNTUxOQAAACDVP/hSZQ4M89s8jm3jqYMB6VaYZH/Z/0xW1hg0otsRRQAAAECO5nXpiNySwNAh00detEjmri8plv0cJ+YYAD0Ynj2opNU/+FJlDgzz2zyObeOpgwHpVphkf9n/TFbWGDSi2xFFAAAAFHZwbnJvdXRlci1kbnN0dC10ZXN0AQ==";

    private static string Link(string id, string resolver = "8.8.8.8", string domain = "t.example.test")
    {
        var encoded = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new
        {
            id, resolver, port = 53, domain, noise_key = new string('a', 64),
            ssh_host_key = "ssh-ed25519 " + FixturePublic,
            ssh_private_key = new string(PemEncoding.Write("OPENSSH PRIVATE KEY", Convert.FromBase64String(FixturePrivateBlob)))
        })).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"dnstt://{encoded}#{id}%20DNSTT";
    }

    [Theory]
    [InlineData("1.14.4-vpnctl.2", true)]
    [InlineData("1.14.4-vpnctl.1", false)]
    [InlineData("1.14.4", false)]
    [InlineData("1.14.2-vpnctl.9", false)]
    public void CapabilityRequiresCompatibleFork(string version, bool allowed) =>
        Assert.Equal(allowed, SingBoxFeatures.HasDnsttVersion(version));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FetchAdvertisesCapabilityOnlyWhenAvailable(bool supported)
    {
        const string url = "https://provider.example/api/v1/app/config/pilot";
        var fake = new FakeHttpClient().Setup(url, string.Join('\n', Link("one"), Link("two"), Link("invalid", "https://resolver.example")));
        var previousCapability = SingBoxFeatures.OverrideDnstt;
        var previousHttp = SubscriptionFetcher.Http;
        SingBoxFeatures.OverrideDnstt = supported;
        SubscriptionFetcher.Http = fake;
        try
        {
            var entries = await SubscriptionFetcher.FetchAsync(url, ct: TestContext.Current.CancellationToken);
            var request = Assert.Single(fake.SentRequests);
            Assert.Equal(supported ? "detour-v1, dnstt-v1" : "detour-v1", request.Headers!["X-VPNRouter-Capabilities"]);
            Assert.Equal(supported ? 2 : 0, entries.Count);
        }
        finally
        {
            SingBoxFeatures.OverrideDnstt = previousCapability;
            SubscriptionFetcher.Http = previousHttp;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReorderedFeedPreservesPinnedGrantWithSharedIdAndResolver(bool swap)
    {
        var previous = SingBoxFeatures.OverrideDnstt;
        SingBoxFeatures.OverrideDnstt = true;
        try
        {
            var pinned = Link("pilot");
            var other = Link("pilot", domain: "other.example.test");
            var entries = SubscriptionFetcher.ParseBody(string.Join('\n', swap ? other : pinned, swap ? pinned : other, pinned));
            Assert.Equal(2, entries.Count);
            var feed = new SubscriptionEntry { Url = "https://provider.example/sub", Servers = entries };
            var selected = ServerUriParser.Parse(pinned);
            var selection = new SubscriptionChainSelection(feed.Id, feed.Url, selected, null);
            Assert.Equal("t.example.test", selection.Match(feed, entries)?.Dnstt?.Domain);
        }
        finally { SingBoxFeatures.OverrideDnstt = previous; }
    }

    [Theory]
    [InlineData("http", "http", "detour-v1")]
    [InlineData("https", "http", "detour-v1, dnstt-v1")]
    public async Task CleartextResponseDoesNotImportDnstt(string sourceScheme, string responseScheme, string capability)
    {
        var url = $"{sourceScheme}://provider.example/api/v1/app/config/pilot";
        var response = new HttpResponse(200, new Dictionary<string, string>(), System.Text.Encoding.UTF8.GetBytes(Link("pilot")), TimeSpan.Zero)
        {
            FinalUri = new Uri($"{responseScheme}://provider.example/api/v1/app/config/pilot")
        };
        var fake = new FakeHttpClient().Setup(url, response);
        var previousCapability = SingBoxFeatures.OverrideDnstt;
        var previousHttp = SubscriptionFetcher.Http;
        SingBoxFeatures.OverrideDnstt = true;
        SubscriptionFetcher.Http = fake;
        try
        {
            Assert.Empty(await SubscriptionFetcher.FetchAsync(url, ct: TestContext.Current.CancellationToken));
            Assert.Equal(capability, Assert.Single(fake.SentRequests).Headers!["X-VPNRouter-Capabilities"]);
        }
        finally
        {
            SingBoxFeatures.OverrideDnstt = previousCapability;
            SubscriptionFetcher.Http = previousHttp;
        }
    }

    [Fact]
    public void SelectedProfileEmitsOneTransportOneSshAndRejectsProxyUdp()
    {
        var previous = SingBoxFeatures.OverrideDnstt;
        SingBoxFeatures.OverrideDnstt = true;
        try
        {
            var entry = ServerUriParser.Parse(Link("pilot"));
            Assert.Equal("pilot DNSTT", entry.Name);
            Assert.Equal("dnstt://[redacted]", CanaryPolicy.RedactUrl(Link("pilot")));
            var alternative = new VlessServerEntry { Name = "Ordinary", Server = entry.Server };
            var settings = new AppSettings { Vless = new VlessConfig { Servers = [alternative, entry], ActiveServer = alternative.Name } };
            settings.App.BypassRussianTraffic = false;
            Assert.Same(alternative, Assert.Single(settings.Vless.GetActiveServers()));
            settings.Vless.ActiveServer = entry.Name;
            settings.App.RoutingMode = "full";
            var config = ConfigGenerator.Generate(new Profile { Name = "DNSTT", DnsMode = "vpn_only" }, [], settings);
            var runtime = new ConfigSanityCheck().CheckBeforeStart(JsonNode.Parse(ConfigGenerator.Serialize(config))!.AsObject());
            Assert.False(runtime.IsDead, runtime.Reason);
            var transport = Assert.Single(config.Outbounds.Where(o => o.Type == "dnstt"));
            var ssh = Assert.Single(config.Outbounds.Where(o => o.Type == "ssh"));
            Assert.Equal("8.8.8.8", transport.Server);
            Assert.Equal(53, transport.ServerPort);
            Assert.Equal("t.example.test", transport.DnsttDomain);
            Assert.Equal("proxy", ssh.Tag);
            Assert.Equal(transport.Tag, ssh.Detour);
            Assert.Equal("vpnctldnstt", ssh.SshUser);
            Assert.Contains(config.Route.Rules, rule => rule.Action == "reject" && rule.Network == "udp" && rule.Port == null);
        }
        finally { SingBoxFeatures.OverrideDnstt = previous; }
    }

    [Fact]
    public async Task BusinessFetchAndSelectionExposeDnsttAndKeepTheSelectedRoute()
    {
        var previous = SingBoxFeatures.OverrideDnstt;
        SingBoxFeatures.OverrideDnstt = true;
        try
        {
            const string ordinary = "vless://00000000-0000-4000-8000-000000000001@8.8.8.8:443?security=none#Ordinary";
            var body = string.Join('\n', Link("pilot"), ordinary);
            using var client = new System.Net.Http.HttpClient(new BusinessClientHttpTests.Handler((request, _) =>
            {
                Assert.Equal("detour-v1, dnstt-v1", Assert.Single(request.Headers.GetValues("X-VPNRouter-Capabilities")));
                return Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new System.Net.Http.StringContent(body)
                });
            }));
            using var http = new VPNRouter.App.Services.BusinessClientHttp(client,
                subscriptionRuntime: () => (true, "1.14.4-vpnctl.3"));
            var imported = BusinessDefaults.ParseConnection(await http.FetchAsync(new Uri("https://provider.example/sub"),
                TestContext.Current.CancellationToken));
            Assert.Equal(2, BusinessDefaults.GetServerChoices(imported).Count);
            var pool = BusinessDefaults.CreateServerPool(imported, null);
            Assert.Equal("Ordinary", pool.ActiveServer);
            pool.ActiveServer = "pilot DNSTT";
            imported.Reverse();
            var refreshed = BusinessDefaults.CreateServerPool(imported, pool, requireSelectedDnstt: true);
            Assert.Equal("dnstt", Assert.Single(refreshed.GetActiveServers()).Protocol);
            Assert.Throws<InvalidDataException>(() => BusinessDefaults.CreateServerPool(
                imported.Where(row => row.Dnstt == null).ToList(), pool, requireSelectedDnstt: true));
        }
        finally { SingBoxFeatures.OverrideDnstt = previous; }
    }

    [Fact]
    public void BusinessPortalAndDestinationRulesUseSelectedTcpOnlyDnstt()
    {
        var previous = SingBoxFeatures.OverrideDnstt;
        SingBoxFeatures.OverrideDnstt = true;
        try
        {
            var entry = ServerUriParser.Parse(Link("pilot"));
            var settings = new AppSettings { Vless = new VlessConfig { Servers = [entry], ActiveServer = entry.Name } };
            settings.App.BypassRussianTraffic = false;
            settings.App.BlockQuicOnTcpProxy = false;
            settings.App.CustomRules = [new() { Type = "domain_suffix", Value = "work.example.test", Action = "proxy", Enabled = true }];
            var config = ConfigGenerator.Generate(new Profile { Name = "DNSTT", DnsMode = "vpn_only" }, [], settings);
            if (!VPNRouter.Core.ProductEdition.IsBusiness)
            {
                BusinessDefaults.ApplyDomainTransports(config, false, false, false);
                ConfigGenerator.ApplyBusinessPortal(config, settings);
            }
            var portal = Assert.Single(config.Route.Rules.Where(rule => rule.Network == "tcp" &&
                rule.DomainSuffix?.Contains(BusinessPolicyDomains.PortalDomain) == true));
            Assert.Equal("proxy", portal.Outbound);
            Assert.Equal("ssh", Assert.Single(config.Outbounds.Where(outbound => outbound.Tag == "proxy")).Type);
            Assert.Equal("proxy", Assert.Single(config.Dns.Servers.Where(dns => dns.Tag == ConfigGenerator.BusinessPortalDns)).Detour);
            var rules = config.Route.Rules.Where(rule => rule.DomainSuffix?.Contains("work.example.test") == true).ToArray();
            Assert.Equal("udp", rules[0].Network);
            Assert.Equal("reject", rules[0].Action);
            Assert.Null(rules[0].Port);
            Assert.Equal("proxy", rules[1].Outbound);
        }
        finally { SingBoxFeatures.OverrideDnstt = previous; }
    }

    [Fact]
    public async Task Native_DnsttImport_ManagedProfile()
    {
        var runtime = Environment.GetEnvironmentVariable("VPNCTL_TEST_CORE")
            ?? Environment.GetEnvironmentVariable("VPNROUTER_TEST_VERIFIER_RUNTIME");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(runtime), "Requires the pinned sing-box runtime");
        Assert.True(File.Exists(runtime), "Supplied sing-box runtime was not found");
        var previous = SingBoxFeatures.OverrideDnstt;
        SingBoxFeatures.OverrideDnstt = true;
        try
        {
            var entry = ServerUriParser.Parse(Link("native"));
            var settings = new AppSettings { Vless = new VlessConfig { Servers = [entry], ActiveServer = entry.Name } };
            settings.App.BypassRussianTraffic = false;
            settings.App.RoutingMode = "full";
            var config = ConfigGenerator.Serialize(
                ConfigGenerator.Generate(new Profile { Name = "DNSTT", DnsMode = "vpn_only" }, [], settings));
            var request = new ProcessRequest(runtime!, ["check", "-c", "stdin"],
                StdinInput: config, Timeout: TimeSpan.FromSeconds(15));
            var result = await new ProcessRunner().RunAsync(request, TestContext.Current.CancellationToken);
            Assert.False(result.TimedOut);
            Assert.True(result.ExitCode == 0, "Native core rejected the generated DNSTT config.");
        }
        finally { SingBoxFeatures.OverrideDnstt = previous; }
    }

}
