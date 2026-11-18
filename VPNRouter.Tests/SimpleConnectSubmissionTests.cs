using Avalonia.Headless.XUnit;
using VPNRouter.App.ViewModels;
using VPNRouter.App.ViewModels.Internals;
using VPNRouter.Core;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;
using Xunit;

namespace VPNRouter.Tests;

[Collection(SubscriptionFetcherCollection.Name)]
public sealed class SimpleConnectSubmissionTests
{
    private const string Feed = "https://feed.invalid/Token";
    private const string NextFeed = "https://feed.invalid/token";
    private const string Id = "00000000-0000-4000-8000-000000000001";

    private static string Link(string name) => $"vless://{Id}@{name}.invalid:443?security=none#{name}";

    private static MainWindowViewModel CreateViewModel(InMemorySettingsStore store) =>
        ProductEdition.IsBusiness
            ? new MainWindowViewModel(store, null, null, null, businessBootstrap: (_, _) => null)
            : new MainWindowViewModel(store);

    [AvaloniaTheory]
    [InlineData("routing")]
    [InlineData("new-feed")]
    [InlineData("selection")]
    [InlineData("subscription-url")]
    [InlineData("refresh-url")]
    [InlineData("server-uri")]
    [InlineData("cached-choice")]
    [InlineData("cancel")]
    [InlineData("busy")]
    public async Task SimpleConnectStartsTheSubmissionAndPreservesEditsDuringProbe(string edit)
    {
        var cached = edit == "cached-choice";
        var settings = new AppSettings();
        settings.App.ConfigMode = "subscribe";
        settings.App.RoutingMode = "split";
        List<VlessServerEntry> servers = [];
        if (cached) servers = [ServerUriParser.Parse(Link("first")), ServerUriParser.Parse(Link("manual"))];
        if (edit == "new-feed") servers = [ServerUriParser.Parse(Link("previous"))];
        settings.App.Subscriptions = [new SubscriptionEntry
        {
            Enabled = true, Name = "feed", Url = edit == "new-feed" ? NextFeed : Feed, Servers = servers
        }];
        var store = new InMemorySettingsStore();
        store.Save(settings, AppPaths.ConfigYamlPath);
        using var vm = new MainWindowViewModel(store, null, null, null, businessBootstrap: (_, _) => null);
        if (edit == "new-feed") vm.SmpInput = Feed;
        if (cached) vm.SelectedSubscriptionServer = vm.SubscriptionServers.Single(s => s.Name == "manual");

        var http = new FakeHttpClient().Setup(Feed, string.Join("\n", Link("first"), Link("winner"), Link("manual")));
        var originalHttp = SubscriptionFetcher.Http;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probes = new List<string[]>();
        vm.SimpleProbe = async (rows, _) =>
        {
            probes.Add(rows.Select(s => s.Name).ToArray());
            entered.TrySetResult();
            await release.Task;
            return rows.Select(s => new ServerLiveness(s, Alive: s.Name != "first", LatencyMs: s.Name == "winner" ? 10 : 80)).ToList();
        };
        AppSettings? started = null;
        vm.SimpleTunnelStart = (submission, _, _) =>
        {
            started = submission;
            return Task.FromResult((Task.CompletedTask, TwoPhaseStartOutcome.Connected));
        };

        var ct = TestContext.Current.CancellationToken;
        try
        {
            SubscriptionFetcher.Http = edit == "refresh-url" ? new DelayedRefresh(http, entered, release.Task) : http;
            var command = vm.SmpToggleConnectCommand.ExecuteAsync(null);
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
                Assert.True(vm.IsConnecting);
                vm.IsSplitTunnel = false;
                switch (edit)
                {
                    case "selection":
                    case "cached-choice":
                        vm.SelectedSubscriptionServer = vm.SubscriptionServers.Single(s => s.Name == (cached ? "first" : "manual"));
                        break;
                    case "refresh-url":
                    case "subscription-url":
                        vm.SmpInput = NextFeed;
                        break;
                    case "server-uri": vm.SmpInput = Link("next"); break;
                    case "cancel": vm.IsConnecting = false; break;
                    case "busy": vm.IsConnected = true; break;
                }
            }
            finally
            {
                release.TrySetResult();
                await command.WaitAsync(TimeSpan.FromSeconds(5), ct);
            }
        }
        finally { SubscriptionFetcher.Http = originalHttp; }

        if (cached) Assert.Empty(http.SentRequests);
        else Assert.Equal(Feed, Assert.Single(http.SentRequests).Uri.AbsoluteUri);
        Assert.False(vm.IsConnecting);
        Assert.False(vm.IsSplitTunnel);
        Assert.True(vm.HasPendingAppChanges);
        var saved = store.Load(AppPaths.ConfigYamlPath);
        Assert.Equal("full", saved.App.RoutingMode);
        if (edit is "cancel" or "busy")
        {
            Assert.Null(started);
            Assert.Single(probes);
            Assert.Equal(edit == "busy", vm.IsConnected);
            return;
        }

        Assert.True(vm.IsConnected);
        Assert.NotNull(started);
        Assert.Equal("split", started.App.RoutingMode);
        Assert.Equal("subscribe", started.App.ConfigMode);
        var source = Assert.Single(started.App.Subscriptions);
        Assert.Equal(Feed, source.Url);
        var expectedRows = cached ? new[] { "first", "manual" } : new[] { "first", "winner", "manual" };
        Assert.Equal(expectedRows, source.Servers.Select(s => s.Name));
        Assert.Equal(expectedRows, started.Vless.Servers.Select(s => s.Name));
        var chosen = cached ? "manual" : "winner";
        Assert.Equal(chosen, started.App.ActiveSubscriptionServer);
        Assert.Equal(chosen, started.Vless.ActiveServer);
        Assert.Equal(new[] { cached ? "manual" : "first" }, probes[0]);
        if (edit is "subscription-url" or "refresh-url")
        {
            Assert.Equal(NextFeed, vm.SmpInput);
            Assert.Equal(NextFeed, Assert.Single(saved.App.Subscriptions).Url);
            Assert.Empty(vm.SubscriptionServers);
        }
        else if (edit == "server-uri")
        {
            Assert.Equal(Link("next"), vm.SmpInput);
            Assert.Equal("generated", saved.App.ConfigMode);
            Assert.Equal("next", saved.Vless.ActiveServer);
            Assert.Equal("next.invalid", Assert.Single(saved.Vless.Servers).Server);
            using var reopened = new MainWindowViewModel(store, null, null, null, businessBootstrap: (_, _) => null);
            Assert.Equal(string.Empty, reopened.SmpInput);
            Assert.Equal("next", reopened.SelectedServer?.Name);
        }
        else
        {
            var pending = cached ? "first" : edit == "selection" ? "manual" : chosen;
            Assert.Equal(pending, vm.SelectedSubscriptionServer?.Name);
            Assert.Equal(pending, saved.App.ActiveSubscriptionServer);
            Assert.NotSame(source.Servers[0], Assert.Single(saved.App.Subscriptions).Servers[0]);
        }
    }

    [AvaloniaFact]
    public async Task SameLabelAndUuidInTwoFeedsStartsTheProbedEndpoint()
    {
        var first = ServerUriParser.Parse(Link("first"));
        var second = ServerUriParser.Parse(Link("second"));
        first.Name = second.Name = "node";
        var settings = new AppSettings();
        settings.App.ConfigMode = "subscribe";
        settings.App.Subscriptions =
        [
            new() { Enabled = true, Url = Feed, Servers = [first] },
            new() { Enabled = true, Url = NextFeed, Servers = [second] }
        ];
        var store = new InMemorySettingsStore();
        store.Save(settings, AppPaths.ConfigYamlPath);
        using var vm = CreateViewModel(store);
        vm.SelectedSubscriptionServer = vm.SubscriptionServers.Single(s => s.Server == second.Server);
        string? probed = null;
        string? started = null;
        vm.SimpleProbe = (rows, _) =>
        {
            probed = Assert.Single(rows).Server;
            return Task.FromResult(rows.Select(s => new ServerLiveness(s, Alive: true, LatencyMs: 1)).ToList());
        };
        vm.SimpleTunnelStart = (submission, _, _) =>
        {
            VlessServersResolver.Resolve(submission);
            started = Assert.Single(submission.Vless.GetActiveServers()).Server;
            return Task.FromResult((Task.CompletedTask, TwoPhaseStartOutcome.Connected));
        };

        await vm.SmpToggleConnectCommand.ExecuteAsync(null);
        Assert.Equal(second.Server, probed);
        Assert.Equal(second.Server, started);
        Assert.Equal(second.Server, vm.SelectedSubscriptionServer?.Server);
        Assert.Equal(new[] { Feed, NextFeed }, store.Load(AppPaths.ConfigYamlPath).App.Subscriptions.Select(s => s.Url));
    }

    private sealed class DelayedRefresh(FakeHttpClient http, TaskCompletionSource entered, Task release) : IHttpClient
    {
        public async Task<HttpResponse> SendAsync(HttpRequest request, CancellationToken ct = default)
        {
            entered.TrySetResult();
            await release.WaitAsync(ct);
            return await http.SendAsync(request, ct);
        }

        public Task<IHttpStreamingResponse> SendStreamingAsync(HttpRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
