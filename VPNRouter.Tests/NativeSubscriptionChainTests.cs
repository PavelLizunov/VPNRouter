using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;

namespace VPNRouter.Tests;

[Collection(SubscriptionFetcherCollection.Name)]
public class NativeSubscriptionChainTests
{
    private const string JsonChain = """
        {"outbounds":[
          {"type":"vless","tag":"target","server":"exit.invalid","server_port":443,"uuid":"00000000-0000-4000-8000-000000000001","detour":"via & one"},
          {"type":"vless","tag":"direct-exit","server":"exit.invalid","server_port":443,"uuid":"00000000-0000-4000-8000-000000000001","detour":""},
          {"type":"vless","tag":"via & one","server":"entry.invalid","server_port":8443,"uuid":"00000000-0000-4000-8000-000000000001"}
        ]}
        """;

    private const string YamlChain = """
        proxies:
          - {name: target, type: vless, server: exit.invalid, port: 443, uuid: 00000000-0000-4000-8000-000000000001, dialer-proxy: "via & one"}
          - {name: direct-exit, type: vless, server: exit.invalid, port: 443, uuid: 00000000-0000-4000-8000-000000000001, dialer-proxy: ""}
          - {name: "via & one", type: vless, server: entry.invalid, port: 8443, uuid: 00000000-0000-4000-8000-000000000001}
        """;

    [Theory]
    [InlineData(JsonChain)]
    [InlineData(YamlChain)]
    public void ParseBody_KeepsSelectedVlessChainAndIndependentRowAtSameEndpoint(string body)
    {
        var rows = SubscriptionFetcher.ParseBody(body);

        Assert.Equal(new[] { "target", "direct-exit", "via & one" }, rows.Select(row => row.Name));
        var target = rows[0];
        var upstream = rows[2];
        Assert.False(string.IsNullOrEmpty(target.DetourVia));
        Assert.Equal(upstream.OutboundId, target.DetourVia);
        Assert.NotEqual(upstream.Name, target.DetourVia);
        Assert.True(string.IsNullOrEmpty(rows[1].DetourVia));
        Assert.True(string.IsNullOrEmpty(upstream.DetourVia));

        var otherFeed = SubscriptionFetcher.ParseBody(body.Replace("entry.invalid", "other-feed.invalid"));
        var config = new VlessConfig { Servers = [.. rows, .. otherFeed], ActiveServer = target.Name };
        Assert.Collection(config.GetActiveServers(),
            exit => Assert.Equal("exit.invalid", exit.Server),
            entry => Assert.Equal("entry.invalid", entry.Server));
    }

    [Fact]
    public void ParseBody_ExistingShareLinksKeepTheirProviderAssignedChainIds()
    {
        const string body = """
            vless://00000000-0000-4000-8000-000000000001@exit.invalid:443?security=none#standalone
            vless://00000000-0000-4000-8000-000000000001@exit.invalid:443?security=none&outbound=exit-id&detour=entry-id#target
            vless://00000000-0000-4000-8000-000000000001@entry.invalid:443?security=none&outbound=entry-id#entry
            """;

        var rows = SubscriptionFetcher.ParseBody(body);

        Assert.Equal(3, rows.Count);
        Assert.Empty(rows[0].DetourVia);
        Assert.Equal("exit-id", rows[1].OutboundId);
        Assert.Equal("entry-id", rows[1].DetourVia);
        Assert.Equal(rows[1].DetourVia, rows[2].OutboundId);
    }

    [Fact]
    public void ParseBody_SkipsTargetWhoseNameWouldSelectADifferentRow()
    {
        var body = JsonChain.Replace("direct-exit", "TARGET");

        var rows = SubscriptionFetcher.ParseBody(body);

        Assert.Equal(new[] { "TARGET", "via & one" }, rows.Select(row => row.Name));
        Assert.All(rows, row => Assert.Empty(row.DetourVia));
    }

    [Fact]
    public void ParseBody_UnsupportedSingBoxDetoursKeepOnlyTheStandaloneServer()
    {
        const string json = """
            {"outbounds":[
              {"type":"vless","tag":"private-row-a","detour":"private-selector","server":"private-exit.invalid","server_port":443,"uuid":"00000000-0000-4000-8000-000000000001"},
              {"type":"selector","tag":"private-selector","outbounds":["standalone"]},
              {"type":"hysteria2","tag":"private-row-b","detour":"standalone","server":"private-other.invalid","server_port":443,"password":"private-password"},
              {"type":"vless","tag":"private-row-c","detour":"standalone","server":"private-bound.invalid","server_port":443,"uuid":"00000000-0000-4000-8000-000000000001","bind_interface":"private-interface"},
              {"type":"vless","tag":"standalone","server":"ordinary.invalid","server_port":443,"uuid":"00000000-0000-4000-8000-000000000001"}
            ]}
            """;

        var sink = new CapturingSink();
        using var logger = sink.CreateLogger();
        var rows = SubscriptionFetcher.ParseBody(json, logger);

        Assert.Equal("ordinary.invalid", Assert.Single(rows).Server);
        Assert.Contains(sink.Events, e => e.RenderMessage().Contains("proxy chain", StringComparison.OrdinalIgnoreCase));
        sink.AssertExcludes("private-");
    }

    [Fact]
    public void ParseBody_ClashDialerGroupsKeepTheirMembersAndNeverChooseAProxyForTheGroup()
    {
        const string yaml = """
            defaults: &routing {dialer-proxy: private-choice}
            proxies:
              - {name: private-target-a, type: vless, server: private-a.invalid, port: 443, uuid: 00000000-0000-4000-8000-000000000001, dialer-proxy: private-choice}
              - {name: private-target-b, type: vless, server: private-b.invalid, port: 443, uuid: 00000000-0000-4000-8000-000000000001, dialer-proxy: private-collision}
              - {<<: *routing, name: private-target-c, type: vless, server: private-c.invalid, port: 443, uuid: 00000000-0000-4000-8000-000000000001}
              - {name: private-target-d, type: vless, server: private-d.invalid, port: 443, uuid: 00000000-0000-4000-8000-000000000001, <<: {dialer-proxy: private-choice}}
              - {name: private-bound, type: vless, server: private-bound.invalid, port: 443, uuid: 00000000-0000-4000-8000-000000000001, dialer-proxy: member, interface-name: private-interface}
              - {name: member, type: vless, server: member.invalid, port: 443, uuid: 00000000-0000-4000-8000-000000000001}
              - {name: private-collision, type: vless, server: separate.invalid, port: 443, uuid: 00000000-0000-4000-8000-000000000001}
              - {name: override, type: vless, server: override.invalid, port: 443, uuid: 00000000-0000-4000-8000-000000000001, dialer-proxy: "", <<: *routing}
            proxy-groups:
              - {name: private-choice, type: select, proxies: [member]}
              - {name: private-collision, type: url-test, proxies: [member]}
            """;

        var sink = new CapturingSink();
        using var logger = sink.CreateLogger();
        var rows = SubscriptionFetcher.ParseBody(yaml, logger);

        Assert.Equal(new[] { "member.invalid", "separate.invalid", "override.invalid" }, rows.Select(row => row.Server));
        Assert.All(rows, row => Assert.True(string.IsNullOrEmpty(row.DetourVia)));
        Assert.Contains(sink.Events, e => e.RenderMessage().Contains("proxy chain", StringComparison.OrdinalIgnoreCase));
        sink.AssertExcludes("private-");
    }

    [Theory]
    [InlineData("""{"outbounds":[{"type":"vless","tag":"private-exit","server":"private-exit.invalid","server_port":443,"uuid":"00000000-0000-4000-8000-000000000001","detour":"private-absent"}]}""")]
    [InlineData("""
        proxies:
          - {name: private-exit, type: vless, server: private-exit.invalid, port: 443, uuid: 00000000-0000-4000-8000-000000000001, dialer-proxy: private-absent}
        """)]
    public async Task MissingUpstreams_ReportGenericReasonAndEvictPreviouslyFlattenedCache(string body)
    {
        const string url = "https://fixture.invalid/subscription";
        Assert.Empty(SubscriptionFetcher.ParseBody(body));

        var previous = SubscriptionFetcher.Http;
        SubscriptionFetcher.Http = new FakeHttpClient().Setup(url, body);
        try
        {
            var error = await Assert.ThrowsAnyAsync<InvalidOperationException>(() =>
                SubscriptionFetcher.FetchAsync(url, ct: TestContext.Current.CancellationToken));
            Assert.Contains("proxy chain", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("private-", error.Message, StringComparison.OrdinalIgnoreCase);

            var cached = new SubscriptionEntry
            {
                Url = url,
                LastServerCount = 1,
                Servers = [new()
                {
                    Name = "private-exit", Server = "private-exit.invalid", Port = 443,
                    Uuid = "00000000-0000-4000-8000-000000000001"
                }]
            };
            var refreshError = await Assert.ThrowsAnyAsync<InvalidOperationException>(() =>
                SubscriptionFetcher.RefreshEntryAsync(cached, ct: TestContext.Current.CancellationToken));
            Assert.Equal(error.Message, refreshError.Message);
            Assert.Empty(cached.Servers);
            Assert.Equal(0, cached.LastServerCount);
        }
        finally
        {
            SubscriptionFetcher.Http = previous;
        }
    }
}
