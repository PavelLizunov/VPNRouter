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
public sealed class SubscriptionChainRefreshTests : IDisposable
{
    private const string Feed = "https://chain.invalid/feed";
    private const string Other = "https://other.invalid/feed";
    private const string Uuid = "00000000-0000-4000-8000-000000000001";
    private readonly IHttpClient _previousHttp = SubscriptionFetcher.Http;

    public SubscriptionChainRefreshTests() => SubscriptionFetcher.Http = new FakeHttpClient();
    public void Dispose() => SubscriptionFetcher.Http = _previousHttp;

    private static string Exit(string id = "") =>
        $"vless://{Uuid}@exit.invalid:443?security=none{(id.Length == 0 ? "" : "&detour=" + id)}#node";

    private static string Hop(string id, int port) =>
        $"vless://{Uuid}@hop.invalid:{port}?security=none&outbound={id}#upstream";

    private static AppSettings SavedChain()
    {
        var feed = new SubscriptionEntry { Url = Feed, Name = "registered" };
        var selected = ServerUriParser.Parse(Exit("old"));
        feed.Servers = [ServerUriParser.Parse(Exit()), selected, ServerUriParser.Parse(Hop("old", 443))];
        var settings = new AppSettings();
        settings.App.ConfigMode = "subscribe";
        settings.App.Subscriptions = [feed];
        settings.App.ActiveSubscriptionServer = selected.Name;
        settings.App.ActiveSubscriptionId = feed.Id;
        settings.App.ActiveSubscriptionRow = SubscriptionEntry.RowKey(selected);
        return settings;
    }

    private static string Body(string scenario) => scenario switch
    {
        "changed" => string.Join("\n", Exit(), Exit("fresh"), Hop("fresh", 8443)),
        "equivalent" => string.Join("\n", Exit(), Exit("fresh"), Hop("fresh", 443), Exit("another"), Hop("another", 9443)),
        "bad-reality" => string.Join("\n", Exit(), Exit("fresh"), Hop("fresh", 8443).Replace("security=none", "security=reality&pbk=short")),
        "ambiguous" => string.Join("\n", Exit(), Exit("fresh"), Hop("fresh", 8443), Exit("another"), Hop("another", 9443)),
        "reused-id" => string.Join("\n", Exit(), Exit("old"), Hop("old", 8443), Exit("another"), Hop("another", 9443)),
        "missing-hop" => string.Join("\n", Exit(), Exit("old")),
        "duplicate-hop" => string.Join("\n", Exit(), Exit("fresh"), Hop("fresh", 8443), Hop("fresh", 9443)),
        "cross-feed" => string.Join("\n", Exit(), Exit("fresh")),
        "removed" => Exit(),
        _ => throw new ArgumentOutOfRangeException(nameof(scenario))
    };

    private static void CheckChain(AppSettings settings, bool updated, int? port = null)
    {
        var feed = settings.App.Subscriptions.Single(s => s.Url == Feed);
        var expected = updated ? "fresh" : "old";
        Assert.Equal(feed.Id, settings.App.ActiveSubscriptionId);
        var registered = feed.FindExactRow(settings.App.ActiveSubscriptionServer, settings.App.ActiveSubscriptionRow);
        Assert.Equal(expected, registered?.DetourVia);
        Assert.Contains(feed.Servers, row => row.Name == "node" && row.DetourVia.Length == 0);
        var active = settings.Vless.GetActiveServers();
        Assert.Equal(new[] { "exit.invalid", "hop.invalid" }, active.Select(s => s.Server));
        Assert.Equal(expected, active[0].DetourVia);
        Assert.Equal(expected, active[1].OutboundId);
        Assert.Equal(port ?? (updated ? 8443 : 443), active[1].Port);
    }

    private static AppSettings Recovery(InMemorySettingsStore store)
    {
        var settings = SettingsLoader.Copy(store.Load(AppPaths.ConfigYamlPath));
        VlessServersResolver.Resolve(settings);
        return settings;
    }

    private static MainWindowViewModel Editor(InMemorySettingsStore store) => new(store);

    [AvaloniaTheory]
    [InlineData("silent", "changed")]
    [InlineData("silent", "equivalent")]
    [InlineData("silent", "ambiguous")]
    [InlineData("silent", "removed")]
    [InlineData("silent", "missing-hop")]
    [InlineData("one", "changed")]
    [InlineData("one", "ambiguous")]
    [InlineData("all", "changed")]
    [InlineData("all", "removed")]
    [InlineData("sync", "changed")]
    [InlineData("sync", "removed")]
    public async Task LiveRefreshOnlyReconnectsToAnUnambiguousReplacement(string command, string scenario)
    {
        var store = new InMemorySettingsStore();
        store.Save(SavedChain(), AppPaths.ConfigYamlPath);
        var http = new FakeHttpClient().Setup(Feed, Body(scenario));
        SubscriptionFetcher.Http = http;
        var reconnects = new List<AppSettings>();
        using var vm = Editor(store);
        vm.IsConnected = true;
        vm.SubscriptionRefreshReconnect = _ =>
        {
            reconnects.Add(Recovery(store));
            return Task.CompletedTask;
        };
        var source = Assert.Single(vm.Subscriptions);
        var originalRows = source.UnderlyingEntry.Servers;
        Assert.Equal("old", vm.SelectedSubscriptionServer?.SourceEntry.DetourVia);
        switch (command)
        {
            case "silent": await vm.RefreshSubscriptionSilentAsync(); break;
            case "one": await vm.RefreshSubscriptionCommand.ExecuteAsync(source); break;
            case "all": await vm.RefreshAllSubscriptionsCommand.ExecuteAsync(null); break;
            case "sync":
                vm.SubscriptionUrl = Feed;
                await vm.SyncSubscriptionCommand.ExecuteAsync(null);
                break;
        }

        var updated = scenario is "changed" or "equivalent";
        Assert.Single(http.SentRequests);
        Assert.True(vm.IsConnected);
        Assert.False(vm.IsConnecting);
        Assert.False(source.IsRefreshing);
        Assert.Equal(!updated, source.LastRefreshFailed);
        if (scenario == "changed") CheckChain(Assert.Single(reconnects), true);
        else Assert.Empty(reconnects);
        if (!updated) Assert.Same(originalRows, source.UnderlyingEntry.Servers);
        CheckChain(Recovery(store), updated, scenario == "equivalent" ? 443 : null);
    }

    [Theory]
    [InlineData("changed")]
    [InlineData("ambiguous")]
    [InlineData("reused-id")]
    [InlineData("removed")]
    [InlineData("missing-hop")]
    [InlineData("duplicate-hop")]
    [InlineData("bad-reality")]
    [InlineData("cross-feed")]
    public async Task ResolverRetainsTheSelectedExitAndItsLocallyOwnedUniqueHop(string scenario)
    {
        var settings = SavedChain();
        var http = new FakeHttpClient().Setup(Feed, Body(scenario));
        if (scenario == "cross-feed")
        {
            settings.App.Subscriptions.Add(new SubscriptionEntry { Url = Other });
            http.Setup(Other, Hop("fresh", 8443));
        }
        SubscriptionFetcher.Http = http;

        await SubscriptionResolver.ResolveAsync(settings, refreshFromNetwork: true, ct: TestContext.Current.CancellationToken);

        Assert.Equal(settings.App.Subscriptions.Count, http.SentRequests.Count);
        CheckChain(settings, scenario == "changed");
        if (scenario == "cross-feed") Assert.Empty(settings.App.Subscriptions[1].Servers);
    }

    [AvaloniaFact]
    public async Task SimpleConnectKeepsARequiredDetourWhenOnlyTheDirectSiblingWouldWinTheProbe()
    {
        var store = new InMemorySettingsStore();
        store.Save(SavedChain(), AppPaths.ConfigYamlPath);
        using var vm = Editor(store);
        var probeCalls = 0;
        vm.SimpleProbe = (rows, _) =>
        {
            probeCalls++;
            return Task.FromResult(rows.Select(row => new ServerLiveness(row, row.DetourVia.Length == 0, 1)).ToList());
        };
        AppSettings? started = null;
        vm.SimpleTunnelStart = (settings, _, _) =>
        {
            started = settings;
            return Task.FromResult((Task.CompletedTask, TwoPhaseStartOutcome.Connected));
        };

        await vm.SmpToggleConnectCommand.ExecuteAsync(null);

        Assert.Equal(0, probeCalls);
        Assert.NotNull(started);
        CheckChain(started, false);
        Assert.Empty(((FakeHttpClient)SubscriptionFetcher.Http).SentRequests);
    }

    [AvaloniaFact]
    public async Task ExplicitDirectChoiceDuringRefreshIsKeptAfterReload()
    {
        var store = new InMemorySettingsStore();
        store.Save(SavedChain(), AppPaths.ConfigYamlPath);
        using var vm = Editor(store);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        SubscriptionFetcher.Http = new WaitForEdit(new FakeHttpClient().Setup(Feed, Body("removed")), entered, release.Task);
        var command = vm.RefreshAllSubscriptionsCommand.ExecuteAsync(null);
        var ct = TestContext.Current.CancellationToken;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            vm.SelectedSubscriptionServer = vm.SubscriptionServers.Single(s => s.Name == "node" && s.SourceEntry.DetourVia.Length == 0);
        }
        finally
        {
            release.TrySetResult();
            await command.WaitAsync(TimeSpan.FromSeconds(5), ct);
        }

        Assert.False(Assert.Single(vm.Subscriptions).LastRefreshFailed);
        var recovered = Recovery(store);
        Assert.Empty(Assert.Single(recovered.Vless.GetActiveServers()).DetourVia);
        Assert.Equal(SubscriptionEntry.RowKey(ServerUriParser.Parse(Exit())), recovered.App.ActiveSubscriptionRow);
    }

    private sealed class WaitForEdit(IHttpClient http, TaskCompletionSource entered, Task release) : IHttpClient
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
