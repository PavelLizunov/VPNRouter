#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
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
public sealed class DnsttGeneratedRefreshTests : IDisposable
{
    private const string Feed = "https://feed.invalid/dnstt";
    private const string OtherFeed = "https://other.invalid/dnstt";
    private const string OldDomain = "old.example.test";
    private const string NewDomain = "new.example.test";
    private const string OtherDomain = "other.example.test";

    // Synthetic, disposable Ed25519 fixtures accepted by the native SSH parser.
    private const string FixturePublic = "AAAAC3NzaC1lZDI1NTE5AAAAINU/+FJlDgzz2zyObeOpgwHpVphkf9n/TFbWGDSi2xFF";
    private const string FixturePrivateBlob =
        "b3BlbnNzaC1rZXktdjEAAAAABG5vbmUAAAAEbm9uZQAAAAAAAAABAAAAMwAAAAtzc2gtZWQyNTUxOQAAACDVP/hSZQ4M89s8jm3jqYMB6VaYZH/Z/0xW1hg0otsRRQAAAJg0xLT4NMS0+AAAAAtzc2gtZWQyNTUxOQAAACDVP/hSZQ4M89s8jm3jqYMB6VaYZH/Z/0xW1hg0otsRRQAAAECO5nXpiNySwNAh00detEjmri8plv0cJ+YYAD0Ynj2opNU/+FJlDgzz2zyObeOpgwHpVphkf9n/TFbWGDSi2xFFAAAAFHZwbnJvdXRlci1kbnN0dC10ZXN0AQ==";

    private readonly IHttpClient _previousHttp = SubscriptionFetcher.Http;
    private readonly bool? _previousDnstt = SingBoxFeatures.OverrideDnstt;

    public DnsttGeneratedRefreshTests()
    {
        SubscriptionFetcher.Http = new FakeHttpClient();
        SingBoxFeatures.OverrideDnstt = true;
    }

    public void Dispose()
    {
        SubscriptionFetcher.Http = _previousHttp;
        SingBoxFeatures.OverrideDnstt = _previousDnstt;
    }

    private static string Link(
        string domain, char noise, string id = "senko", string name = "senko DNSTT") =>
        $"dnstt://{Payload(domain, noise, id)}#{Uri.EscapeDataString(name)}";

    private static string Payload(string domain, char noise, string id)
    {
        var encoded = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new
        {
            id,
            resolver = "192.0.2.53",
            port = 53,
            domain,
            noise_key = new string(noise, 64),
            ssh_host_key = "ssh-ed25519 " + FixturePublic,
            ssh_private_key = new string(PemEncoding.Write(
                "OPENSSH PRIVATE KEY", Convert.FromBase64String(FixturePrivateBlob))),
        })).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return encoded;
    }

    private static AppSettings GeneratedSettings(VlessServerEntry active, params SubscriptionEntry[] feeds)
    {
        var settings = new AppSettings();
        settings.App.ConfigMode = "generated";
        settings.Vless.Servers = [active];
        settings.Vless.ActiveServer = active.Name;
        settings.App.Subscriptions = feeds.ToList();
        return settings;
    }

    private static SubscriptionEntry FeedEntry(string url = Feed, params VlessServerEntry[] rows) => new()
    {
        Name = "registered",
        Url = url,
        Enabled = true,
        Servers = rows.ToList(),
    };

    private static AppSettings Recovery(InMemorySettingsStore store) =>
        SettingsLoader.Copy(store.Load(AppPaths.ConfigYamlPath));

    private static VlessServerEntry Active(AppSettings settings) =>
        Assert.Single(settings.Vless.GetActiveServers());

    [AvaloniaFact]
    public async Task OrdinaryGeneratedRefreshRotatesTheSelectedRowAndPersists()
    {
        var store = new InMemorySettingsStore();
        store.Save(GeneratedSettings(
            ServerUriParser.Parse(Link(OldDomain, 'a')),
            FeedEntry(Feed, ServerUriParser.Parse(Link(OldDomain, 'a')))),
            AppPaths.ConfigYamlPath);
        var http = new FakeHttpClient().Setup(Feed, Link(NewDomain, 'b'));
        SubscriptionFetcher.Http = http;
        using var vm = new MainWindowViewModel(store);
        var source = Assert.Single(vm.Subscriptions);

        await vm.RefreshSubscriptionCommand.ExecuteAsync(source);

        Assert.Single(http.SentRequests);
        Assert.False(source.LastRefreshFailed);
        Assert.Equal(NewDomain, Assert.Single(vm.SubscriptionServers).SourceEntry.Dnstt!.Domain);
        Assert.Equal(NewDomain, vm.SelectedServer!.SourceEntry.Dnstt!.Domain);
        Assert.Equal("senko DNSTT", vm.SelectedServer.Name);
        var saved = Recovery(store);
        Assert.Equal(NewDomain, Active(saved).Dnstt!.Domain);
        Assert.Equal(NewDomain, Assert.Single(Assert.Single(saved.App.Subscriptions).Servers).Dnstt!.Domain);
        using var reopened = new MainWindowViewModel(store);
        Assert.Equal(NewDomain, reopened.SelectedServer!.SourceEntry.Dnstt!.Domain);
    }

    [AvaloniaFact]
    public async Task FirstManualGeneratedConnectRefreshesOnceBeforeStart()
    {
        var store = new InMemorySettingsStore();
        store.Save(GeneratedSettings(
            ServerUriParser.Parse(Link(OldDomain, 'a')),
            FeedEntry(Feed, ServerUriParser.Parse(Link(OldDomain, 'a')))),
            AppPaths.ConfigYamlPath);
        var http = new FakeHttpClient().Setup(Feed, Link(NewDomain, 'b'));
        SubscriptionFetcher.Http = http;
        using var vm = new MainWindowViewModel(store);
        AppSettings? started = null;
        vm.SimpleTunnelStart = (submission, _, _) =>
        {
            started = submission;
            return Task.FromResult((Task.CompletedTask, TwoPhaseStartOutcome.Connected));
        };

        await vm.SmpToggleConnectCommand.ExecuteAsync(null);

        Assert.Single(http.SentRequests);
        Assert.NotNull(started);
        Assert.Equal(NewDomain, Active(started).Dnstt!.Domain);
        Assert.Equal("senko DNSTT", started.Vless.ActiveServer);
        Assert.Equal(NewDomain, Active(Recovery(store)).Dnstt!.Domain);
        using var reopened = new MainWindowViewModel(store);
        Assert.Equal(NewDomain, reopened.SelectedServer!.SourceEntry.Dnstt!.Domain);

        // Only the first manual connect fetches; later connects use the saved rotated row.
        vm.IsConnected = false;
        await vm.SmpToggleConnectCommand.ExecuteAsync(null);
        Assert.Single(http.SentRequests);
        Assert.Equal(NewDomain, Active(started).Dnstt!.Domain);
    }

    [AvaloniaFact]
    public async Task OrdinaryRefreshThenFirstConnectDoesNotFetchAgain()
    {
        var store = new InMemorySettingsStore();
        store.Save(GeneratedSettings(
            ServerUriParser.Parse(Link(OldDomain, 'a')),
            FeedEntry(Feed, ServerUriParser.Parse(Link(OldDomain, 'a')))),
            AppPaths.ConfigYamlPath);
        var http = new FakeHttpClient().Setup(Feed, Link(NewDomain, 'b'));
        SubscriptionFetcher.Http = http;
        using var vm = new MainWindowViewModel(store);
        AppSettings? started = null;
        vm.SimpleTunnelStart = (submission, _, _) =>
        {
            started = submission;
            return Task.FromResult((Task.CompletedTask, TwoPhaseStartOutcome.Connected));
        };

        await vm.RefreshSubscriptionCommand.ExecuteAsync(Assert.Single(vm.Subscriptions));
        await vm.SmpToggleConnectCommand.ExecuteAsync(null);

        Assert.Single(http.SentRequests);
        Assert.NotNull(started);
        Assert.Equal(NewDomain, Active(started).Dnstt!.Domain);
        Assert.Equal(NewDomain, Active(Recovery(store)).Dnstt!.Domain);
    }

    [AvaloniaFact]
    public async Task ManualImportOfADifferentProfileIsNotReplacedOnConnect()
    {
        var store = new InMemorySettingsStore();
        store.Save(GeneratedSettings(
            ServerUriParser.Parse(Link(NewDomain, 'b')),
            FeedEntry(Feed, ServerUriParser.Parse(Link(OldDomain, 'a')))),
            AppPaths.ConfigYamlPath);
        var http = new FakeHttpClient().Setup(Feed, Link(NewDomain, 'b'));
        SubscriptionFetcher.Http = http;
        using var vm = new MainWindowViewModel(store);
        AppSettings? started = null;
        vm.SimpleTunnelStart = (submission, _, _) =>
        {
            started = submission;
            return Task.FromResult((Task.CompletedTask, TwoPhaseStartOutcome.Connected));
        };

        await vm.SmpToggleConnectCommand.ExecuteAsync(null);

        Assert.Empty(http.SentRequests);
        Assert.NotNull(started);
        Assert.Equal(NewDomain, Active(started).Dnstt!.Domain);
    }

    [AvaloniaFact]
    public async Task AmbiguousEnabledRegistrationsAreNotRefreshedOnConnect()
    {
        var store = new InMemorySettingsStore();
        store.Save(GeneratedSettings(
            ServerUriParser.Parse(Link(OldDomain, 'a')),
            FeedEntry(Feed, ServerUriParser.Parse(Link(OldDomain, 'a'))),
            FeedEntry(OtherFeed, ServerUriParser.Parse(Link(OtherDomain, 'c')))),
            AppPaths.ConfigYamlPath);
        var http = new FakeHttpClient().Setup(Feed, Link(NewDomain, 'b'));
        SubscriptionFetcher.Http = http;
        using var vm = new MainWindowViewModel(store);
        AppSettings? started = null;
        vm.SimpleTunnelStart = (submission, _, _) =>
        {
            started = submission;
            return Task.FromResult((Task.CompletedTask, TwoPhaseStartOutcome.Connected));
        };

        await vm.SmpToggleConnectCommand.ExecuteAsync(null);

        Assert.Empty(http.SentRequests);
        Assert.NotNull(started);
        Assert.Equal(OldDomain, Active(started).Dnstt!.Domain);
    }

    [AvaloniaFact]
    public async Task UserEditDuringRefreshIsNotOverwritten()
    {
        var store = new InMemorySettingsStore();
        store.Save(GeneratedSettings(
            ServerUriParser.Parse(Link(OldDomain, 'a')),
            FeedEntry(Feed, ServerUriParser.Parse(Link(OldDomain, 'a')))),
            AppPaths.ConfigYamlPath);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var http = new FakeHttpClient().Setup(Feed, Link(NewDomain, 'b'));
        SubscriptionFetcher.Http = new WaitForEdit(http, entered, release.Task);
        using var vm = new MainWindowViewModel(store);
        var source = Assert.Single(vm.Subscriptions);
        AppSettings? started = null;
        vm.SimpleTunnelStart = (submission, _, _) =>
        {
            started = submission;
            return Task.FromResult((Task.CompletedTask, TwoPhaseStartOutcome.Connected));
        };
        var ct = TestContext.Current.CancellationToken;

        var command = vm.SmpToggleConnectCommand.ExecuteAsync(null);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            vm.SelectedServer!.Server = "203.0.113.9";
        }
        finally
        {
            release.TrySetResult();
            await command.WaitAsync(TimeSpan.FromSeconds(5), ct);
        }

        Assert.Single(http.SentRequests);
        Assert.True(source.LastRefreshFailed);
        Assert.NotNull(started);
        var active = Active(started);
        Assert.Equal("203.0.113.9", active.Server);
        Assert.Equal(OldDomain, active.Dnstt!.Domain);
        var saved = Recovery(store);
        Assert.Equal("203.0.113.9", Active(saved).Server);
        Assert.Equal(OldDomain, Assert.Single(Assert.Single(saved.App.Subscriptions).Servers).Dnstt!.Domain);
    }

    [AvaloniaFact]
    public async Task OrdinaryRefreshWithAnEditorEditKeepsTheOldCache()
    {
        var store = new InMemorySettingsStore();
        store.Save(GeneratedSettings(
            ServerUriParser.Parse(Link(OldDomain, 'a')),
            FeedEntry(Feed, ServerUriParser.Parse(Link(OldDomain, 'a')))),
            AppPaths.ConfigYamlPath);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var http = new FakeHttpClient().Setup(Feed, Link(NewDomain, 'b'));
        SubscriptionFetcher.Http = new WaitForEdit(http, entered, release.Task);
        using var vm = new MainWindowViewModel(store);
        var source = Assert.Single(vm.Subscriptions);
        var ct = TestContext.Current.CancellationToken;

        var command = vm.RefreshSubscriptionCommand.ExecuteAsync(source);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            vm.SelectedServer!.Server = "203.0.113.9";
        }
        finally
        {
            release.TrySetResult();
            await command.WaitAsync(TimeSpan.FromSeconds(5), ct);
        }

        Assert.Single(http.SentRequests);
        Assert.True(source.LastRefreshFailed);
        Assert.Equal("203.0.113.9", vm.SelectedServer!.Server);
        Assert.Equal(OldDomain, vm.SelectedServer.SourceEntry.Dnstt!.Domain);
        var saved = Recovery(store);
        Assert.Equal(OldDomain, Active(saved).Dnstt!.Domain);
        Assert.Equal(OldDomain, Assert.Single(Assert.Single(saved.App.Subscriptions).Servers).Dnstt!.Domain);
    }

    [AvaloniaFact]
    public async Task SelectedDnsttChangeDuringRefreshKeepsTheOldCacheBinding()
    {
        const string oldA = "a-old.example.test";
        const string newA = "a-new.example.test";
        const string manualB = "b-manual.example.test";
        var firstA = ServerUriParser.Parse(Link(oldA, 'a', "grant-a", "A DNSTT"));
        var manual = ServerUriParser.Parse(Link(manualB, 'b', "grant-b", "B DNSTT"));
        var store = new InMemorySettingsStore();
        var settings = GeneratedSettings(firstA,
            FeedEntry(Feed, ServerUriParser.Parse(Link(oldA, 'a', "grant-a", "A DNSTT"))));
        settings.Vless.Servers.Add(manual);
        store.Save(settings, AppPaths.ConfigYamlPath);
        var firstHttp = new FakeHttpClient().Setup(Feed,
            string.Join('\n', Link(newA, 'c', "grant-a", "A DNSTT"),
                Link(manualB, 'd', "grant-b", "B DNSTT")));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        SubscriptionFetcher.Http = new WaitForEdit(firstHttp, entered, release.Task);
        using var vm = new MainWindowViewModel(store);
        var source = Assert.Single(vm.Subscriptions);
        var ct = TestContext.Current.CancellationToken;

        var command = vm.RefreshSubscriptionCommand.ExecuteAsync(source);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            vm.SelectedServer = vm.Servers.Single(row => row.Name == "B DNSTT");
        }
        finally
        {
            release.TrySetResult();
            await command.WaitAsync(TimeSpan.FromSeconds(5), ct);
        }

        // The in-flight selection change invalidated the binding: keep the old cache and report
        // the rejected refresh instead of rotating a grant the user did not keep selected.
        Assert.Single(firstHttp.SentRequests);
        Assert.True(source.LastRefreshFailed);
        Assert.Equal(oldA, firstA.Dnstt!.Domain);
        Assert.Equal(manualB, manual.Dnstt!.Domain);
        var preserved = Assert.Single(Assert.Single(Recovery(store).App.Subscriptions).Servers);
        Assert.Equal(oldA, preserved.Dnstt!.Domain);

        // Selecting A again must still find the old cache binding and apply the rotation.
        var secondHttp = new FakeHttpClient().Setup(Feed, Link(newA, 'c', "grant-a", "A DNSTT"));
        SubscriptionFetcher.Http = secondHttp;
        vm.SelectedServer = vm.Servers.Single(row => row.Name == "A DNSTT");
        AppSettings? started = null;
        vm.SimpleTunnelStart = (submission, _, _) =>
        {
            started = submission;
            return Task.FromResult((Task.CompletedTask, TwoPhaseStartOutcome.Connected));
        };

        await vm.SmpToggleConnectCommand.ExecuteAsync(null);

        Assert.Single(secondHttp.SentRequests);
        Assert.NotNull(started);
        Assert.Equal(newA, Active(started).Dnstt!.Domain);
        Assert.Equal(newA, Active(Recovery(store)).Dnstt!.Domain);
        Assert.Equal(manualB, manual.Dnstt!.Domain);
    }

    [AvaloniaFact]
    public async Task SupersededConnectCannotPublishItsRefreshOrStart()
    {
        var store = new InMemorySettingsStore();
        store.Save(GeneratedSettings(
            ServerUriParser.Parse(Link(OldDomain, 'a')),
            FeedEntry(Feed, ServerUriParser.Parse(Link(OldDomain, 'a')))), AppPaths.ConfigYamlPath);
        var http = new FakeHttpClient().Setup(Feed, Link(NewDomain, 'b'));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        SubscriptionFetcher.Http = new WaitForEdit(http, entered, release.Task);
        using var vm = new MainWindowViewModel(store);
        var starts = 0;
        vm.SimpleTunnelStart = (_, _, _) =>
        {
            starts++;
            return Task.FromResult((Task.CompletedTask, TwoPhaseStartOutcome.Connected));
        };
        var ct = TestContext.Current.CancellationToken;
        var command = vm.SmpToggleConnectCommand.ExecuteAsync(null);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            vm.IsConnecting = false;
            vm.IsConnecting = true;
        }
        finally
        {
            release.TrySetResult();
            await command.WaitAsync(TimeSpan.FromSeconds(5), ct);
        }
        Assert.True(vm.IsConnecting);
        Assert.Equal(0, starts);
        Assert.Equal(OldDomain, Active(Recovery(store)).Dnstt!.Domain);
        Assert.Equal(OldDomain, Assert.Single(Assert.Single(Recovery(store).App.Subscriptions).Servers).Dnstt!.Domain);
    }

    [AvaloniaFact]
    public async Task SilentRefreshStillDoesNotRunOutsideSubscribeMode()
    {
        var store = new InMemorySettingsStore();
        store.Save(GeneratedSettings(
            ServerUriParser.Parse(Link(OldDomain, 'a')),
            FeedEntry(Feed, ServerUriParser.Parse(Link(OldDomain, 'a')))),
            AppPaths.ConfigYamlPath);
        var http = new FakeHttpClient().Setup(Feed, Link(NewDomain, 'b'));
        SubscriptionFetcher.Http = http;
        using var vm = new MainWindowViewModel(store);
        vm.IsConnected = true;

        await vm.RefreshSubscriptionSilentAsync();

        Assert.Empty(http.SentRequests);
        Assert.Equal(OldDomain, vm.SelectedServer!.SourceEntry.Dnstt!.Domain);
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
