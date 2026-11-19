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
public sealed class SimpleConnectSubmissionTests : IDisposable
{
    private readonly IHttpClient _previousHttp = SubscriptionFetcher.Http;
    public SimpleConnectSubmissionTests() => SubscriptionFetcher.Http = new FakeHttpClient();
    public void Dispose() => SubscriptionFetcher.Http = _previousHttp;

    private const string Feed = "https://feed.invalid/Token";
    private const string NextFeed = "https://feed.invalid/token";
    private const string Id = "00000000-0000-4000-8000-000000000001";

    private static string Link(string name) => $"vless://{Id}@{name}.invalid:443?security=none#{name}";

    private static MainWindowViewModel CreateViewModel(InMemorySettingsStore store) =>
        ProductEdition.IsBusiness
            ? new MainWindowViewModel(store, null, null, null, businessBootstrap: (_, _) => null)
            : new MainWindowViewModel(store);

    [AvaloniaFact]
    public async Task DisablingFeedKeepsItsCacheButCannotRestoreItFromTheSimpleUrl()
    {
        var settings = new AppSettings();
        settings.App.ConfigMode = "subscribe";
        settings.App.Subscriptions =
        [
            new() { Url = Feed, Name = "first", Servers = [ServerUriParser.Parse(Link("first"))] },
            new() { Url = NextFeed, Name = "next", Servers = [ServerUriParser.Parse(Link("next"))] }
        ];
        var store = new InMemorySettingsStore();
        store.Save(settings, AppPaths.ConfigYamlPath);
        var http = new FakeHttpClient().Setup(Feed, Link("retrieved"));
        SubscriptionFetcher.Http = http;

        using (var vm = CreateViewModel(store))
        {
            var original = vm.Subscriptions[0];
            Assert.Equal(Feed, vm.SmpInput);
            original.Enabled = false;

            Assert.Equal("next", Assert.Single(vm.SubscriptionServers).Name);
            Assert.Equal(NextFeed, vm.SmpInput);
            Assert.False(store.Load(AppPaths.ConfigYamlPath).App.Subscriptions[0].Enabled);

            vm.SmpInput = Feed;
            await vm.SmpToggleConnectCommand.ExecuteAsync(null);
            await vm.RefreshSubscriptionCommand.ExecuteAsync(original);
            Assert.Empty(http.SentRequests);
        }

        using var reopened = CreateViewModel(store);
        Assert.Equal("next", Assert.Single(reopened.SubscriptionServers).Name);
        Assert.Equal("first", Assert.Single(reopened.Subscriptions[0].UnderlyingEntry.Servers).Name);
        reopened.Subscriptions[0].Enabled = true;
        Assert.Equal(2, reopened.SubscriptionServers.Count);
    }

    [AvaloniaTheory]
    [InlineData("routing")]
    [InlineData("new-feed")]
    [InlineData("selection")]
    [InlineData("subscription-url")]
    [InlineData("refresh-url")]
    [InlineData("server-uri")]
    [InlineData("cached-choice")]
    [InlineData("disable")]
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
                    case "disable": vm.Subscriptions[0].Enabled = false; break;
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
        if (edit is "cancel" or "busy" or "disable")
        {
            Assert.Null(started);
            if (edit != "disable") Assert.Single(probes);
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

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SameLabelAndUuidInTwoFeedsStartsTheProbedEndpoint(bool sharedRow)
    {
        var first = ServerUriParser.Parse(Link("first"));
        var second = sharedRow ? first : ServerUriParser.Parse(Link("second"));
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
        var sourceId = settings.App.Subscriptions[1].Id;
        vm.SelectedSubscriptionServer = vm.SubscriptionServers.Single(s => s.SubscriptionId == sourceId);
        string? probed = null;
        string? started = null;
        string? chosenFeed = null;
        vm.SimpleProbe = (rows, _) =>
        {
            probed = Assert.Single(rows).Server;
            return Task.FromResult(rows.Select(s => new ServerLiveness(s, Alive: true, LatencyMs: 1)).ToList());
        };
        vm.SimpleTunnelStart = (submission, _, _) =>
        {
            VlessServersResolver.Resolve(submission);
            started = submission.Vless.GetActiveServers().First().Server;
            chosenFeed = submission.App.ActiveSubscriptionId;
            return Task.FromResult((Task.CompletedTask, TwoPhaseStartOutcome.Connected));
        };

        await vm.SmpToggleConnectCommand.ExecuteAsync(null);
        Assert.Equal(second.Server, probed);
        Assert.Equal(second.Server, started);
        Assert.Equal(sourceId, chosenFeed);
        Assert.Equal(sourceId, vm.SelectedSubscriptionServer?.SubscriptionId);
        Assert.Equal(second.Server, vm.SelectedSubscriptionServer?.Server);
        Assert.Equal(new[] { Feed, NextFeed }, store.Load(AppPaths.ConfigYamlPath).App.Subscriptions.Select(s => s.Url));
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SilentRefreshAndEditorReorderKeepTheRegisteredRow(bool selectFallback)
    {
        var a = ServerUriParser.Parse(Link("a"));
        var b = ServerUriParser.Parse(Link("b"));
        a.Name = b.Name = "node";
        var settings = new AppSettings();
        settings.App.ConfigMode = "subscribe";
        settings.App.ActiveSubscriptionServer = "node";
        settings.App.Subscriptions = [new() { Url = Feed, Servers = [a, b] }];
        var store = new InMemorySettingsStore();
        store.Save(settings, AppPaths.ConfigYamlPath);

        var oldHttp = SubscriptionFetcher.Http;
        var http = new FakeHttpClient().Setup(Feed,
            Link("b").Replace("#b", "#node") + "\n" + Link("a").Replace("#a", "#node"));
        SubscriptionFetcher.Http = http;
        try
        {
            using (var running = CreateViewModel(store))
            {
                running.IsConnected = true;
                running.IsConnecting = true;
                await running.RefreshSubscriptionSilentAsync();
                Assert.Equal(new[] { b.Server, a.Server }, running.SubscriptionServers.Select(s => s.Server));
                Assert.Equal(a.Server, running.SelectedSubscriptionServer?.Server);
            }

            using var editor = CreateViewModel(store);
            // Editor order can differ from the refreshed registration in both directions.
            editor.SubscriptionServers.Move(0, 1);
            var probes = new List<string[]>();
            editor.SimpleProbe = (rows, _) =>
            {
                probes.Add(rows.Select(row => row.Server).ToArray());
                return Task.FromResult(rows.Select(row => new ServerLiveness(row,
                    Alive: !selectFallback || row.Server == b.Server, LatencyMs: 1)).ToList());
            };
            var starts = CaptureSimpleStarts(editor);
            await editor.SmpToggleConnectCommand.ExecuteAsync(null);
            var expected = selectFallback ? b.Server : a.Server;
            Assert.Equal(new[] { a.Server }, probes[0]);
            Assert.Equal(expected, Assert.Single(EngineSelection(Assert.Single(starts))).Server);
            Assert.Equal(expected, editor.SelectedSubscriptionServer?.Server);
            Assert.Equal(new[] { b.Server, a.Server }, Assert.Single(store.Load(AppPaths.ConfigYamlPath).App.Subscriptions).Servers.Select(row => row.Server));
            Assert.Single(http.SentRequests);
        }
        finally { SubscriptionFetcher.Http = oldHttp; }
    }

    [AvaloniaTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task ChoiceDuringProbeRestoresExactRegisteredDetourAfterReopen(bool enterExistingUrl, bool leaveManualEditorOpen)
    {
        var first = ServerUriParser.Parse(Link("first"));
        var direct = ServerUriParser.Parse(Link("second"));
        var chained = ServerUriParser.Parse(Link("second"));
        first.Name = direct.Name = chained.Name = "node";
        var via = ServerUriParser.Parse(Link("via"));
        chained.DetourVia = via.OutboundId = "fixture-hop";
        var settings = new AppSettings();
        settings.App.ConfigMode = "subscribe";
        // Name-only preferences predate registration and row keys.
        settings.App.ActiveSubscriptionServer = "NODE";
        var submittedFeed = new SubscriptionEntry { Url = Feed, Servers = [ServerUriParser.Parse(Link("other")), first] };
        var editedFeed = new SubscriptionEntry { Url = NextFeed, Servers = enterExistingUrl ? [chained, direct, via] : [direct, chained, via] };
        settings.App.Subscriptions = [submittedFeed, editedFeed];
        var store = new InMemorySettingsStore();
        store.Save(settings, AppPaths.ConfigYamlPath);

        using (var editor = CreateViewModel(store))
        {
            Assert.Equal(first.Server, editor.SelectedSubscriptionServer?.Server);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            editor.SimpleProbe = async (rows, _) =>
            {
                entered.TrySetResult();
                await release.Task;
                return rows.Select(row => new ServerLiveness(row, Alive: true, LatencyMs: 1)).ToList();
            };
            var launches = CaptureSimpleStarts(editor);
            var connect = editor.SmpToggleConnectCommand.ExecuteAsync(null);
            var ct = TestContext.Current.CancellationToken;
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
                if (enterExistingUrl)
                    editor.SmpInput = NextFeed;
                else
                {
                    editor.ToggleUiModeCommand.Execute(null);
                    editor.SelectedTabIndex = 1;
                    editor.SelectedSubscriptionServer = editor.SubscriptionServers.Single(row => row.SourceEntry.DetourVia == "fixture-hop");
                    if (leaveManualEditorOpen) editor.SelectedTabIndex = 0;
                    else editor.ToggleUiModeCommand.Execute(null);
                }
            }
            finally
            {
                release.TrySetResult();
                await connect.WaitAsync(TimeSpan.FromSeconds(5), ct);
            }
            Assert.Equal(first.Server, Assert.Single(EngineSelection(Assert.Single(launches))).Server);
            Assert.True(editor.HasPendingAppChanges);
            Assert.Equal(NextFeed, editor.SmpInput);
            Assert.Equal("fixture-hop", editor.SelectedSubscriptionServer?.SourceEntry.DetourVia);
        }

        // Cross the actual YAML serializer boundary before reconstructing the editor.
        var saved = SettingsLoader.Copy(store.Load(AppPaths.ConfigYamlPath));
        Assert.Equal(new[] { submittedFeed.Id, editedFeed.Id }, saved.App.Subscriptions.Select(feed => feed.Id));
        Assert.All(saved.App.Subscriptions, feed => Assert.True(feed.Enabled));
        Assert.Equal(leaveManualEditorOpen ? "generated" : "subscribe", saved.App.ConfigMode);
        Assert.Equal(editedFeed.Id, saved.App.ActiveSubscriptionId);
        Assert.Equal(SubscriptionEntry.RowKey(chained), saved.App.ActiveSubscriptionRow);
        store.Save(saved, AppPaths.ConfigYamlPath);
        using var reopened = CreateViewModel(store);
        if (leaveManualEditorOpen)
        {
            Assert.Empty(reopened.SmpInput);
            reopened.SmpInput = NextFeed;
        }
        else Assert.Equal(NextFeed, reopened.SmpInput);
        Assert.Equal(editedFeed.Id, reopened.SelectedSubscriptionServer?.SubscriptionId);
        Assert.Equal("fixture-hop", reopened.SelectedSubscriptionServer?.SourceEntry.DetourVia);
        reopened.SimpleProbe = (rows, _) => Task.FromResult(rows.Select(row => new ServerLiveness(row, true, 1)).ToList());
        var restarted = CaptureSimpleStarts(reopened);
        await reopened.SmpToggleConnectCommand.ExecuteAsync(null);
        var active = EngineSelection(Assert.Single(restarted));
        Assert.Equal(new[] { chained.Server, via.Server }, active.Select(row => row.Server));
        Assert.Equal(active[0].DetourVia, active[1].OutboundId);
    }

    [AvaloniaFact]
    public async Task UnnamedProbeWinnerBecomesTheEngineSelectionWithNamedFirstRow()
    {
        var named = ServerUriParser.Parse(Link("named"));
        var unnamed = ServerUriParser.Parse(Link("unnamed"));
        unnamed.Name = string.Empty;
        var settings = new AppSettings();
        settings.App.ConfigMode = "subscribe";
        settings.App.Subscriptions = [new() { Url = Feed, Servers = [named, unnamed] }];
        var store = new InMemorySettingsStore();
        store.Save(settings, AppPaths.ConfigYamlPath);
        using var editor = CreateViewModel(store);
        editor.SimpleProbe = (rows, _) => Task.FromResult(rows.Select(row =>
            new ServerLiveness(row, Alive: row.Name == string.Empty, LatencyMs: 1)).ToList());
        var starts = CaptureSimpleStarts(editor);
        await editor.SmpToggleConnectCommand.ExecuteAsync(null);
        var submitted = Assert.Single(starts);
        Assert.Equal(named.Server, submitted.App.Subscriptions[0].Servers[0].Server);
        Assert.Equal(unnamed.Server, Assert.Single(EngineSelection(submitted)).Server);
        Assert.Equal(unnamed.Server, editor.SelectedSubscriptionServer?.Server);
    }

    [AvaloniaFact]
    public async Task RefreshedDetourKeepsTheChosenHopAcrossNewOpaqueIds()
    {
        // Both exit rows have the same label, endpoint and UUID in the saved feed.
        var direct = ServerUriParser.Parse(Link("exit"));
        var via = ServerUriParser.Parse(Link("via"));
        var target = ServerUriParser.Parse(Link("exit"));
        target.DetourVia = via.OutboundId = "old-hop";
        var settings = new AppSettings();
        settings.App.ConfigMode = "subscribe";
        var source = new SubscriptionEntry { Url = Feed, Servers = [direct, target, via] };
        settings.App.Subscriptions = [source];
        settings.App.ActiveSubscriptionId = source.Id;
        settings.App.ActiveSubscriptionServer = target.Name;
        settings.App.ActiveSubscriptionRow = SubscriptionEntry.RowKey(target);
        var store = new InMemorySettingsStore();
        store.Save(settings, AppPaths.ConfigYamlPath);
        var http = new FakeHttpClient().Setup(Feed, string.Join("\n", Link("exit"),
            Link("exit").Replace("#exit", "&detour=new-hop#exit"),
            Link("via").Replace("#via", "&outbound=new-hop#via")));
        SubscriptionFetcher.Http = http;
        using (var running = CreateViewModel(store))
        {
            running.IsConnected = true;
            running.IsConnecting = true;
            await running.RefreshSubscriptionSilentAsync();
            Assert.Equal("new-hop", running.SelectedSubscriptionServer?.SourceEntry.DetourVia);
        }
        using var editor = CreateViewModel(store);
        editor.SimpleProbe = (rows, _) => Task.FromResult(rows.Select(row => new ServerLiveness(row, true, 1)).ToList());
        var starts = CaptureSimpleStarts(editor);
        await editor.SmpToggleConnectCommand.ExecuteAsync(null);
        var active = EngineSelection(Assert.Single(starts));
        Assert.Equal(new[] { target.Server, via.Server }, active.Select(s => s.Server));
        Assert.Equal("new-hop", active[0].DetourVia);
        Assert.Equal(active[0].DetourVia, active[1].OutboundId);
        Assert.Single(http.SentRequests);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemovingSelectedFeedRebindsItsUrlAndPreservesAnIndependentDraft(bool hasDraft)
    {
        var a = new SubscriptionEntry { Url = Feed, Servers = [ServerUriParser.Parse(Link("a"))] };
        var b = new SubscriptionEntry { Url = NextFeed, Servers = [ServerUriParser.Parse(Link("b"))] };
        var c = new SubscriptionEntry { Url = Feed + "/c", Servers = [ServerUriParser.Parse(Link("c"))] };
        var settings = new AppSettings();
        settings.App.ConfigMode = "subscribe";
        settings.App.Subscriptions = [a, b, c];
        var store = new InMemorySettingsStore();
        store.Save(settings, AppPaths.ConfigYamlPath);
        using var vm = new MainWindowViewModel(store, null, null, null, businessBootstrap: (_, _) => null);
        vm.SelectedSubscriptionServer = vm.SubscriptionServers.Single(row => row.SubscriptionId == b.Id);
        Assert.Equal(b.Url, vm.SmpInput);
        if (hasDraft) vm.SmpInput = c.Url;
        vm.RemoveSubscriptionCommand.Execute(vm.Subscriptions.Single(feed => feed.Id == b.Id));
        var expected = hasDraft ? c : a;
        Assert.Equal(expected.Url, vm.SmpInput);
        vm.SimpleProbe = (rows, _) => Task.FromResult(rows.Select(row => new ServerLiveness(row, true, 1)).ToList());
        var starts = CaptureSimpleStarts(vm);
        await vm.SmpToggleConnectCommand.ExecuteAsync(null);
        Assert.Equal(expected.Servers[0].Server, Assert.Single(EngineSelection(Assert.Single(starts))).Server);
        var saved = SettingsLoader.Copy(store.Load(AppPaths.ConfigYamlPath));
        Assert.Equal(new[] { a.Id, c.Id }, saved.App.Subscriptions.Select(feed => feed.Id));
        Assert.Equal(expected.Id, saved.App.ActiveSubscriptionId);
    }

    [AvaloniaFact]
    public async Task TypingTheOriginalFeedAfterTheProbeWinnerIsPublishedSurvivesRestart()
    {
        var a = new SubscriptionEntry { Url = Feed, Servers = [ServerUriParser.Parse(Link("a"))] };
        var b = new SubscriptionEntry { Url = NextFeed, Servers = [ServerUriParser.Parse(Link("b"))] };
        var settings = new AppSettings();
        settings.App.ConfigMode = "subscribe";
        settings.App.Subscriptions = [a, b];
        var store = new InMemorySettingsStore();
        store.Save(settings, AppPaths.ConfigYamlPath);
        using (var vm = new MainWindowViewModel(store, null, null, null, businessBootstrap: (_, _) => null))
        {
            Assert.Equal(a.Url, vm.SmpInput);
            vm.SimpleProbe = (rows, _) => Task.FromResult(rows.Select(row => new ServerLiveness(row, row.Name == "b", 1)).ToList());
            AppSettings? started = null;
            string? inputAtStart = null;
            vm.SimpleTunnelStart = (submitted, _, _) =>
            {
                started = submitted;
                inputAtStart = vm.SmpInput;
                vm.SmpInput = a.Url;
                return Task.FromResult((Task.CompletedTask, TwoPhaseStartOutcome.Connected));
            };
            await vm.SmpToggleConnectCommand.ExecuteAsync(null);
            Assert.Equal(b.Url, inputAtStart);
            Assert.Equal(b.Servers[0].Server, Assert.Single(EngineSelection(Assert.IsType<AppSettings>(started))).Server);
            Assert.True(vm.HasPendingAppChanges);
        }
        var saved = SettingsLoader.Copy(store.Load(AppPaths.ConfigYamlPath));
        Assert.Equal(a.Id, saved.App.ActiveSubscriptionId);
        store.Save(saved, AppPaths.ConfigYamlPath);
        using var reopened = new MainWindowViewModel(store, null, null, null, businessBootstrap: (_, _) => null);
        Assert.Equal(a.Url, reopened.SmpInput);
        Assert.Equal(a.Servers[0].Server, reopened.SelectedSubscriptionServer?.Server);
    }

    private static List<AppSettings> CaptureSimpleStarts(MainWindowViewModel vm)
    {
        var starts = new List<AppSettings>();
        vm.SimpleTunnelStart = (submission, _, _) =>
        {
            starts.Add(submission);
            return Task.FromResult((Task.CompletedTask, TwoPhaseStartOutcome.Connected));
        };
        return starts;
    }

    private static List<VlessServerEntry> EngineSelection(AppSettings submission)
    {
        var settings = SettingsLoader.Copy(submission);
        VlessServersResolver.Resolve(settings);
        return settings.Vless.GetActiveServers();
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
