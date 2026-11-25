#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
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

    private static List<AppSettings> CaptureStarts(MainWindowViewModel vm)
    {
        var starts = new List<AppSettings>();
        vm.SimpleTunnelStart = (submission, _, _) =>
        {
            starts.Add(submission);
            return Task.FromResult((Task.CompletedTask, TwoPhaseStartOutcome.Connected));
        };
        return starts;
    }

    [AvaloniaFact]
    public async Task PastedStaleGrantIsConsumedAndKeepsTheRotatedRowAndManualRows()
    {
        var active = ServerUriParser.Parse(Link(OldDomain, 'a'));
        var manual = ServerUriParser.Parse(Link(OtherDomain, 'c', "manual", "Manual DNSTT"));
        var settings = GeneratedSettings(active,
            FeedEntry(Feed, ServerUriParser.Parse(Link(OldDomain, 'a'))));
        settings.Vless.Servers.Add(manual);
        var store = new InMemorySettingsStore();
        store.Save(settings, AppPaths.ConfigYamlPath);
        var http = new FakeHttpClient().Setup(Feed, Link(NewDomain, 'b'));
        SubscriptionFetcher.Http = http;
        using var vm = new MainWindowViewModel(store);
        vm.SmpInput = Link(OldDomain, 'a');
        var starts = CaptureStarts(vm);

        await vm.SmpToggleConnectCommand.ExecuteAsync(null);

        Assert.Single(http.SentRequests);
        var first = Assert.Single(starts);
        Assert.Equal(2, first.Vless.Servers.Count);
        Assert.Equal(NewDomain, Active(first).Dnstt!.Domain);
        Assert.Contains(first.Vless.Servers, row => row.Name == "Manual DNSTT");
        var saved = Recovery(store);
        Assert.Equal(2, saved.Vless.Servers.Count);
        Assert.Equal(NewDomain, Active(saved).Dnstt!.Domain);
        Assert.Contains(saved.Vless.Servers, row => row.Name == "Manual DNSTT");

        // The retained input must not be re-imported over the rotated row on the next Connect.
        vm.IsConnected = false;
        await vm.SmpToggleConnectCommand.ExecuteAsync(null);

        Assert.Single(http.SentRequests);
        Assert.Equal(2, starts.Count);
        Assert.Equal(NewDomain, Active(starts[1]).Dnstt!.Domain);
        Assert.Equal(2, starts[1].Vless.Servers.Count);
        Assert.Contains(starts[1].Vless.Servers, row => row.Name == "Manual DNSTT");
    }

    [AvaloniaTheory]
    [InlineData("other", "SENKO DNSTT", true)]
    [InlineData("senko", "Other name", true)]
    [InlineData("other", "SENKO DNSTT", false)]
    [InlineData("senko", "Other name", false)]
    public async Task PastedSubscriptionGrantRefusesConflictingSavedRows(
        string otherId, string otherName, bool alreadySaved)
    {
        var pasted = Link(OldDomain, 'a');
        var other = ServerUriParser.Parse(Link(OtherDomain, 'c', otherId, otherName));
        var settings = GeneratedSettings(other, FeedEntry(Feed, ServerUriParser.Parse(pasted)));
        if (alreadySaved) settings.Vless.Servers.Add(ServerUriParser.Parse(pasted));
        var store = new InMemorySettingsStore();
        store.Save(settings, AppPaths.ConfigYamlPath);
        var http = new FakeHttpClient().Setup(Feed, Link(NewDomain, 'b'));
        SubscriptionFetcher.Http = http;
        using var vm = new MainWindowViewModel(store);
        var selectedBefore = vm.SelectedServer;
        var rowsBefore = vm.Servers.ToArray();
        var starts = CaptureStarts(vm);
        vm.SmpInput = pasted;

        await vm.SmpToggleConnectCommand.ExecuteAsync(null);

        Assert.Empty(starts);
        Assert.Empty(http.SentRequests);
        Assert.False(string.IsNullOrWhiteSpace(vm.SmpErrorText));
        Assert.Same(selectedBefore, vm.SelectedServer);
        Assert.Equal(rowsBefore, vm.Servers.ToArray());
        Assert.Equal(OtherDomain, Recovery(store).Vless.Servers[0].Dnstt!.Domain);
    }

    [AvaloniaFact]
    public async Task RepastingTheOriginalLinkKeepsSavedRowsAcrossRotationsAndRestart()
    {
        var original = Link(OldDomain, 'a');
        var store = new SerializingSettingsStore(new InMemorySettingsStore());
        var settings = GeneratedSettings(ServerUriParser.Parse(original),
            FeedEntry(Feed, ServerUriParser.Parse(original)));
        settings.Vless.Servers.Add(ServerUriParser.Parse(Link(OtherDomain, 'c', "manual", "Manual")));
        store.Save(settings, AppPaths.ConfigYamlPath);
        var http = new FakeHttpClient().Setup(Feed, Link(NewDomain, 'b'));
        SubscriptionFetcher.Http = http;
        using (var vm = new MainWindowViewModel(store))
        {
            var starts = CaptureStarts(vm);
            vm.SmpInput = original;
            await vm.SmpToggleConnectCommand.ExecuteAsync(null);
            Assert.Equal(NewDomain, Active(Assert.Single(starts)).Dnstt!.Domain);

            vm.IsConnected = false;
            vm.SmpInput = string.Empty;
            vm.SmpInput = original;
            await vm.SmpToggleConnectCommand.ExecuteAsync(null);
            Assert.Equal(2, starts.Count);
            Assert.Equal(NewDomain, Active(starts[1]).Dnstt!.Domain);
            Assert.Single(http.SentRequests);
            Assert.Equal(2, store.Load(AppPaths.ConfigYamlPath).Vless.Servers.Count);
        }

        const string latest = "latest.example.test";
        using (var restarted = new MainWindowViewModel(store))
        {
            var starts = CaptureStarts(restarted);
            restarted.SmpInput = original;
            SubscriptionFetcher.Http = new FakeHttpClient().Setup(Feed, Link(latest, 'd'));
            await restarted.SmpToggleConnectCommand.ExecuteAsync(null);
            Assert.Equal(latest, Active(Assert.Single(starts)).Dnstt!.Domain);
            var saved = store.Load(AppPaths.ConfigYamlPath);
            Assert.Equal(2, saved.Vless.Servers.Count);
            Assert.Contains(saved.Vless.Servers, row => row.Name == "Manual" && row.Dnstt!.Domain == OtherDomain);
        }

        using var afterRemoval = new MainWindowViewModel(store);
        var refused = CaptureStarts(afterRemoval);
        afterRemoval.SmpInput = original;
        http = new FakeHttpClient().Setup(Feed, Link(OtherDomain, 'c', "different"));
        SubscriptionFetcher.Http = http;
        await afterRemoval.SmpToggleConnectCommand.ExecuteAsync(null);
        Assert.Single(http.SentRequests);
        Assert.Empty(refused);
        Assert.False(string.IsNullOrWhiteSpace(afterRemoval.SmpErrorText));
        Assert.Equal(latest, Active(store.Load(AppPaths.ConfigYamlPath)).Dnstt!.Domain);
        Assert.Equal(2, afterRemoval.Servers.Count);
    }

    [AvaloniaFact]
    public async Task PastedUnrelatedCredentialsWithTheSameIdCannotReplaceOrSelectASavedGrant()
    {
        var original = Link(OldDomain, 'a');
        var settings = GeneratedSettings(ServerUriParser.Parse(original),
            FeedEntry(Feed, ServerUriParser.Parse(original)));
        settings.Vless.Servers.Add(ServerUriParser.Parse(Link(OtherDomain, 'c', "manual", "Manual")));
        var store = new InMemorySettingsStore();
        store.Save(settings, AppPaths.ConfigYamlPath);
        using var vm = new MainWindowViewModel(store);
        var http = new FakeHttpClient().Setup(Feed, Link(NewDomain, 'b'));
        SubscriptionFetcher.Http = http;
        var starts = CaptureStarts(vm);
        await vm.SmpToggleConnectCommand.ExecuteAsync(null);
        Assert.Equal(NewDomain, Active(Assert.Single(starts)).Dnstt!.Domain);

        vm.IsConnected = false;
        vm.SmpInput = Link(OtherDomain, 'c');
        await vm.SmpToggleConnectCommand.ExecuteAsync(null);

        Assert.Single(http.SentRequests);
        Assert.Single(starts);
        Assert.False(string.IsNullOrWhiteSpace(vm.SmpErrorText));
        Assert.Equal(2, vm.Servers.Count);
        Assert.Equal(NewDomain, Active(Recovery(store)).Dnstt!.Domain);
    }

    [AvaloniaFact]
    public async Task PastedManualDnsttThatDoesNotMatchTheCacheIsImported()
    {
        var settings = GeneratedSettings(
            ServerUriParser.Parse(Link(OldDomain, 'a')),
            FeedEntry(Feed, ServerUriParser.Parse(Link(OldDomain, 'a'))));
        var store = new InMemorySettingsStore();
        store.Save(settings, AppPaths.ConfigYamlPath);
        var http = new FakeHttpClient().Setup(Feed, Link(NewDomain, 'b'));
        SubscriptionFetcher.Http = http;
        using var vm = new MainWindowViewModel(store);
        vm.SmpInput = Link(OtherDomain, 'c', "manual", "Manual DNSTT");
        var starts = CaptureStarts(vm);

        await vm.SmpToggleConnectCommand.ExecuteAsync(null);

        Assert.Empty(http.SentRequests);
        var started = Assert.Single(starts);
        Assert.Equal("Manual DNSTT", started.Vless.ActiveServer);
        Assert.Equal(OtherDomain, Active(started).Dnstt!.Domain);
        Assert.Equal(Link(OtherDomain, 'c', "manual", "Manual DNSTT"), vm.SmpInput);
    }

    [AvaloniaFact]
    public async Task SecondGeneratedSelectionRefreshesItsOwnGrantOnly()
    {
        const string bNew = "b-new.example.test";
        var a = ServerUriParser.Parse(Link(OldDomain, 'a', "grant-a", "A DNSTT"));
        var b = ServerUriParser.Parse(Link(OtherDomain, 'c', "grant-b", "B DNSTT"));
        var settings = GeneratedSettings(a,
            FeedEntry(Feed,
                ServerUriParser.Parse(Link(OldDomain, 'a', "grant-a", "A DNSTT")),
                ServerUriParser.Parse(Link(OtherDomain, 'c', "grant-b", "B DNSTT"))));
        settings.Vless.Servers.Add(b);
        var store = new InMemorySettingsStore();
        store.Save(settings, AppPaths.ConfigYamlPath);
        var firstHttp = new FakeHttpClient().Setup(Feed,
            string.Join('\n', Link(NewDomain, 'b', "grant-a", "A DNSTT"),
                Link(OtherDomain, 'c', "grant-b", "B DNSTT")));
        SubscriptionFetcher.Http = firstHttp;
        using var vm = new MainWindowViewModel(store);
        var starts = CaptureStarts(vm);

        await vm.SmpToggleConnectCommand.ExecuteAsync(null);

        Assert.Single(firstHttp.SentRequests);
        Assert.Equal(NewDomain, Active(starts[0]).Dnstt!.Domain);

        vm.IsConnected = false;
        vm.SelectedServer = vm.Servers.Single(row => row.Name == "B DNSTT");
        var secondHttp = new FakeHttpClient().Setup(Feed,
            string.Join('\n', Link(NewDomain, 'b', "grant-a", "A DNSTT"),
                Link(bNew, 'd', "grant-b", "B DNSTT")));
        SubscriptionFetcher.Http = secondHttp;

        await vm.SmpToggleConnectCommand.ExecuteAsync(null);

        Assert.Single(secondHttp.SentRequests);
        Assert.Equal(2, starts.Count);
        var second = starts[1];
        Assert.Equal("B DNSTT", second.Vless.ActiveServer);
        Assert.Equal(bNew, Active(second).Dnstt!.Domain);
        Assert.Equal(NewDomain,
            second.Vless.Servers.Single(row => row.Name == "A DNSTT").Dnstt!.Domain);
        var savedSecond = Recovery(store);
        var savedB = savedSecond.App.Subscriptions.Single().Servers.Single(row => row.Name == "B DNSTT");
        Assert.Equal(bNew, savedB.Dnstt!.Domain);
        Assert.Equal(new string('d', 64), savedB.Dnstt.NoiseKey);
        Assert.False(string.IsNullOrWhiteSpace(savedB.Dnstt.SshHostKey));
        Assert.False(string.IsNullOrWhiteSpace(savedB.Dnstt.SshPrivateKey));
    }

    [AvaloniaFact]
    public async Task AdvancedImmediateSelectionRefreshesTheVisibleRow()
    {
        const string bNew = "b-new.example.test";
        var a = ServerUriParser.Parse(Link(OldDomain, 'a', "grant-a", "A DNSTT"));
        var b = ServerUriParser.Parse(Link(OtherDomain, 'c', "grant-b", "B DNSTT"));
        var settings = GeneratedSettings(a,
            FeedEntry(Feed,
                ServerUriParser.Parse(Link(OldDomain, 'a', "grant-a", "A DNSTT")),
                ServerUriParser.Parse(Link(OtherDomain, 'c', "grant-b", "B DNSTT"))));
        settings.Vless.Servers.Add(b);
        var store = new InMemorySettingsStore();
        store.Save(settings, AppPaths.ConfigYamlPath);
        var http = new FakeHttpClient().Setup(Feed,
            string.Join('\n', Link(OldDomain, 'a', "grant-a", "A DNSTT"),
                Link(bNew, 'd', "grant-b", "B DNSTT")));
        SubscriptionFetcher.Http = http;
        using var vm = new MainWindowViewModel(store);
        vm.SelectedServer = vm.Servers.Single(row => row.Name == "B DNSTT");
        var starts = CaptureStarts(vm);

        await vm.ToggleConnectionCommand.ExecuteAsync(null);

        Assert.Single(http.SentRequests);
        var started = Assert.Single(starts);
        Assert.Equal("B DNSTT", started.Vless.ActiveServer);
        Assert.Equal(bNew, Active(started).Dnstt!.Domain);
        Assert.Equal(OldDomain,
            started.Vless.Servers.Single(row => row.Name == "A DNSTT").Dnstt!.Domain);
    }

    [AvaloniaFact]
    public async Task AdvancedSecondConnectAfterReloadRefreshesTheNextSelection()
    {
        const string aNew = "a-new.example.test";
        const string bNew = "b-new.example.test";
        var a = ServerUriParser.Parse(Link(OldDomain, 'a', "grant-a", "A DNSTT"));
        var b = ServerUriParser.Parse(Link(OtherDomain, 'c', "grant-b", "B DNSTT"));
        var settings = GeneratedSettings(a,
            FeedEntry(Feed,
                ServerUriParser.Parse(Link(OldDomain, 'a', "grant-a", "A DNSTT")),
                ServerUriParser.Parse(Link(OtherDomain, 'c', "grant-b", "B DNSTT"))));
        settings.Vless.Servers.Add(b);
        var store = new SerializingSettingsStore(new InMemorySettingsStore());
        store.Save(settings, AppPaths.ConfigYamlPath);
        var firstHttp = new FakeHttpClient().Setup(Feed,
            string.Join('\n', Link(aNew, 'b', "grant-a", "A DNSTT"),
                Link(bNew, 'd', "grant-b", "B DNSTT")));
        SubscriptionFetcher.Http = firstHttp;
        using var vm = new MainWindowViewModel(store);
        vm.SelectedServer = vm.Servers.Single(row => row.Name == "B DNSTT");
        var starts = CaptureStarts(vm);

        await vm.ToggleConnectionCommand.ExecuteAsync(null);

        Assert.Single(firstHttp.SentRequests);
        Assert.Equal(bNew, Active(starts[0]).Dnstt!.Domain);
        Assert.Equal(OldDomain, starts[0].Vless.Servers.Single(row => row.Name == "A DNSTT").Dnstt!.Domain);

        // The command reloads settings after the first connect. Select the other grant and
        // connect again; the live feed graph must still be the one the views own.
        vm.IsConnected = false;
        vm.SelectedServer = vm.Servers.Single(row => row.Name == "A DNSTT");
        var secondHttp = new FakeHttpClient().Setup(Feed,
            string.Join('\n', Link(aNew, 'b', "grant-a", "A DNSTT"),
                Link(bNew, 'd', "grant-b", "B DNSTT")));
        SubscriptionFetcher.Http = secondHttp;

        await vm.ToggleConnectionCommand.ExecuteAsync(null);

        Assert.Single(secondHttp.SentRequests);
        Assert.Equal(2, starts.Count);
        Assert.Equal("A DNSTT", starts[1].Vless.ActiveServer);
        Assert.Equal(aNew, Active(starts[1]).Dnstt!.Domain);
        Assert.Equal(bNew,
            starts[1].Vless.Servers.Single(row => row.Name == "B DNSTT").Dnstt!.Domain);
        var saved = SettingsLoader.Copy(store.Load(AppPaths.ConfigYamlPath));
        Assert.Equal(aNew, Active(saved).Dnstt!.Domain);
    }

    [AvaloniaFact]
    public async Task AdvancedUnsavedEditIsNotReplacedByTheFeed()
    {
        var a = ServerUriParser.Parse(Link(OldDomain, 'a', "grant-a", "A DNSTT"));
        var b = ServerUriParser.Parse(Link(OtherDomain, 'c', "grant-b", "B DNSTT"));
        var settings = GeneratedSettings(a,
            FeedEntry(Feed,
                ServerUriParser.Parse(Link(OldDomain, 'a', "grant-a", "A DNSTT")),
                ServerUriParser.Parse(Link(OtherDomain, 'c', "grant-b", "B DNSTT"))));
        settings.Vless.Servers.Add(b);
        var store = new InMemorySettingsStore();
        store.Save(settings, AppPaths.ConfigYamlPath);
        var http = new FakeHttpClient().Setup(Feed,
            string.Join('\n', Link(OldDomain, 'a', "grant-a", "A DNSTT"),
                Link("b-new.example.test", 'd', "grant-b", "B DNSTT")));
        SubscriptionFetcher.Http = http;
        using var vm = new MainWindowViewModel(store);
        vm.SelectedServer = vm.Servers.Single(row => row.Name == "B DNSTT");
        vm.SelectedServer.Server = "203.0.113.9";
        var starts = CaptureStarts(vm);

        await vm.ToggleConnectionCommand.ExecuteAsync(null);

        Assert.Empty(http.SentRequests);
        var started = Assert.Single(starts);
        Assert.Equal("B DNSTT", started.Vless.ActiveServer);
        Assert.Equal("203.0.113.9", Active(started).Server);
        Assert.Equal(OtherDomain, Active(started).Dnstt!.Domain);
        Assert.Equal(OtherDomain,
            Recovery(store).App.Subscriptions.Single().Servers.Single(row => row.Name == "B DNSTT").Dnstt!.Domain);
    }

    [AvaloniaFact]
    public async Task SecondSelectionDuringFetchPreventsTheFirstResponseFromApplying()
    {
        const string bNew = "b-new.example.test";
        var a = ServerUriParser.Parse(Link(OldDomain, 'a', "grant-a", "A DNSTT"));
        var b = ServerUriParser.Parse(Link(OtherDomain, 'c', "grant-b", "B DNSTT"));
        var settings = GeneratedSettings(a,
            FeedEntry(Feed,
                ServerUriParser.Parse(Link(OldDomain, 'a', "grant-a", "A DNSTT")),
                ServerUriParser.Parse(Link(OtherDomain, 'c', "grant-b", "B DNSTT"))));
        settings.Vless.Servers.Add(b);
        var store = new InMemorySettingsStore();
        store.Save(settings, AppPaths.ConfigYamlPath);
        var http = new FakeHttpClient().Setup(Feed,
            string.Join('\n', Link(NewDomain, 'b', "grant-a", "A DNSTT"),
                Link(OtherDomain, 'c', "grant-b", "B DNSTT")));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        SubscriptionFetcher.Http = new WaitForEdit(http, entered, release.Task);
        using var vm = new MainWindowViewModel(store);
        var source = Assert.Single(vm.Subscriptions);
        var starts = CaptureStarts(vm);
        var ct = TestContext.Current.CancellationToken;
        var command = vm.SmpToggleConnectCommand.ExecuteAsync(null);
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

        Assert.Single(http.SentRequests);
        Assert.True(source.LastRefreshFailed);
        Assert.Empty(starts);
        Assert.False(string.IsNullOrWhiteSpace(vm.SmpErrorText));
        Assert.Equal(OldDomain,
            Recovery(store).App.Subscriptions.Single().Servers.Single(row => row.Name == "A DNSTT").Dnstt!.Domain);

        vm.IsConnected = false;
        var secondHttp = new FakeHttpClient().Setup(Feed,
            string.Join('\n', Link(OldDomain, 'a', "grant-a", "A DNSTT"),
                Link(bNew, 'd', "grant-b", "B DNSTT")));
        SubscriptionFetcher.Http = secondHttp;

        await vm.SmpToggleConnectCommand.ExecuteAsync(null);

        Assert.Single(secondHttp.SentRequests);
        Assert.Single(starts);
        Assert.Equal(bNew, Active(starts[0]).Dnstt!.Domain);
        Assert.Equal(OldDomain,
            starts[0].Vless.Servers.Single(row => row.Name == "A DNSTT").Dnstt!.Domain);
    }

    [AvaloniaFact]
    public async Task ConnectDuringOrdinaryRefreshRefusesAndRetriesAfterItSettles()
    {
        var active = ServerUriParser.Parse(Link(OldDomain, 'a'));
        var store = new InMemorySettingsStore();
        store.Save(GeneratedSettings(active,
            FeedEntry(Feed, ServerUriParser.Parse(Link(OldDomain, 'a')))), AppPaths.ConfigYamlPath);
        var http = new FakeHttpClient().Setup(Feed, Link(NewDomain, 'b'));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        SubscriptionFetcher.Http = new WaitForEdit(http, entered, release.Task);
        using var vm = new MainWindowViewModel(store);
        var source = Assert.Single(vm.Subscriptions);
        var starts = CaptureStarts(vm);
        var ct = TestContext.Current.CancellationToken;
        var refreshing = vm.RefreshSubscriptionCommand.ExecuteAsync(source);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            var connect = vm.SmpToggleConnectCommand.ExecuteAsync(null);
            await connect.WaitAsync(TimeSpan.FromSeconds(5), ct);
            Assert.Empty(starts);
            Assert.False(vm.IsConnecting);
            Assert.False(string.IsNullOrWhiteSpace(vm.SmpErrorText));
        }
        finally
        {
            release.TrySetResult();
            await refreshing.WaitAsync(TimeSpan.FromSeconds(5), ct);
        }

        // The superseded ordinary refresh must not publish its rotated row.
        Assert.Equal(OldDomain, vm.SelectedServer!.SourceEntry.Dnstt!.Domain);
        Assert.Equal(OldDomain,
            Recovery(store).App.Subscriptions.Single().Servers.Single().Dnstt!.Domain);

        var retry = new FakeHttpClient().Setup(Feed, Link(NewDomain, 'b'));
        SubscriptionFetcher.Http = retry;
        await vm.SmpToggleConnectCommand.ExecuteAsync(null);

        Assert.Single(retry.SentRequests);
        Assert.Single(starts);
        Assert.Equal(NewDomain, Active(starts[0]).Dnstt!.Domain);
    }

    [AvaloniaFact]
    public async Task RemovedGrantKeepsTheCacheAndBlocksTheManualConnect()
    {
        var active = ServerUriParser.Parse(Link(OldDomain, 'a'));
        var store = new InMemorySettingsStore();
        store.Save(GeneratedSettings(active,
            FeedEntry(Feed, ServerUriParser.Parse(Link(OldDomain, 'a')))), AppPaths.ConfigYamlPath);
        var http = new FakeHttpClient().Setup(Feed, Link(OtherDomain, 'c', "other", "Other DNSTT"));
        SubscriptionFetcher.Http = http;
        using var vm = new MainWindowViewModel(store);
        var source = Assert.Single(vm.Subscriptions);
        var starts = CaptureStarts(vm);

        await vm.SmpToggleConnectCommand.ExecuteAsync(null);

        Assert.Empty(starts);
        Assert.False(vm.IsConnected);
        Assert.False(vm.IsConnecting);
        Assert.False(string.IsNullOrWhiteSpace(vm.SmpErrorText));
        Assert.True(source.LastRefreshFailed);
        var saved = Recovery(store);
        Assert.Equal(OldDomain, Active(saved).Dnstt!.Domain);
        Assert.Equal(OldDomain,
            saved.App.Subscriptions.Single().Servers.Single().Dnstt!.Domain);
    }

    [AvaloniaFact]
    public async Task OfflineRetryAfterRemovalKeepsTheRefusalAcrossRestart()
    {
        var store = new SerializingSettingsStore(new InMemorySettingsStore());
        var link = Link(OldDomain, 'a');
        store.Save(GeneratedSettings(ServerUriParser.Parse(link),
            FeedEntry(Feed, ServerUriParser.Parse(link))), AppPaths.ConfigYamlPath);
        SubscriptionFetcher.Http = new FakeHttpClient().Setup(Feed, Link(OtherDomain, 'c', "other"));
        using (var vm = new MainWindowViewModel(store))
        {
            var starts = CaptureStarts(vm);
            await vm.SmpToggleConnectCommand.ExecuteAsync(null);
            Assert.Empty(starts);
        }

        using var restarted = new MainWindowViewModel(store);
        var retried = CaptureStarts(restarted);
        restarted.SmpInput = link;
        SubscriptionFetcher.Http = new FakeHttpClient().ThrowOn(Feed, new HttpRequestException("offline"));
        await restarted.SmpToggleConnectCommand.ExecuteAsync(null);
        Assert.Empty(retried);
        Assert.False(string.IsNullOrWhiteSpace(restarted.SmpErrorText));

        SubscriptionFetcher.Http = new FakeHttpClient().Setup(Feed, Link(NewDomain, 'b'));
        await restarted.SmpToggleConnectCommand.ExecuteAsync(null);
        Assert.Equal(NewDomain, Active(Assert.Single(retried)).Dnstt!.Domain);
        Assert.Null(Active(store.Load(AppPaths.ConfigYamlPath)).DnsttRefreshBinding);
    }

    [AvaloniaFact]
    public async Task RemovedGrantAfterASuccessfulRotationIsRefetchedAndBlocked()
    {
        var active = ServerUriParser.Parse(Link(OldDomain, 'a'));
        var store = new InMemorySettingsStore();
        store.Save(GeneratedSettings(active,
            FeedEntry(Feed, ServerUriParser.Parse(Link(OldDomain, 'a')))), AppPaths.ConfigYamlPath);
        var firstHttp = new FakeHttpClient().Setup(Feed, Link(NewDomain, 'b'));
        SubscriptionFetcher.Http = firstHttp;
        using var vm = new MainWindowViewModel(store);
        var source = Assert.Single(vm.Subscriptions);
        var starts = CaptureStarts(vm);

        await vm.SmpToggleConnectCommand.ExecuteAsync(null);

        Assert.Single(starts);
        Assert.Equal(NewDomain, Active(starts[0]).Dnstt!.Domain);

        vm.IsConnected = false;
        var ordinaryRemoval = new FakeHttpClient().Setup(Feed,
            Link(OtherDomain, 'c', "other", "Other DNSTT"));
        SubscriptionFetcher.Http = ordinaryRemoval;

        await vm.RefreshSubscriptionCommand.ExecuteAsync(source);

        Assert.Single(ordinaryRemoval.SentRequests);
        Assert.True(source.LastRefreshFailed);
        Assert.Equal(NewDomain, vm.SelectedServer!.SourceEntry.Dnstt!.Domain);
        Assert.Equal(NewDomain,
            Recovery(store).App.Subscriptions.Single().Servers.Single().Dnstt!.Domain);

        var connectRemoval = new FakeHttpClient().Setup(Feed,
            Link(OtherDomain, 'c', "other", "Other DNSTT"));
        SubscriptionFetcher.Http = connectRemoval;
        var blockedStarts = CaptureStarts(vm);

        await vm.SmpToggleConnectCommand.ExecuteAsync(null);

        Assert.Single(connectRemoval.SentRequests);
        Assert.Empty(blockedStarts);
        Assert.False(vm.IsConnected);
        Assert.False(string.IsNullOrWhiteSpace(vm.SmpErrorText));
        Assert.Equal(NewDomain, Recovery(store).Vless.Servers.Single().Dnstt!.Domain);
    }

    [AvaloniaFact]
    public async Task AmbiguousGrantKeepsTheCacheAndBlocksTheManualConnect()
    {
        var active = ServerUriParser.Parse(Link(OldDomain, 'a'));
        var store = new InMemorySettingsStore();
        store.Save(GeneratedSettings(active,
            FeedEntry(Feed, ServerUriParser.Parse(Link(OldDomain, 'a')))), AppPaths.ConfigYamlPath);
        var http = new FakeHttpClient().Setup(Feed,
            string.Join('\n', Link(NewDomain, 'b'), Link(OtherDomain, 'c')));
        SubscriptionFetcher.Http = http;
        using var vm = new MainWindowViewModel(store);
        var starts = CaptureStarts(vm);

        await vm.SmpToggleConnectCommand.ExecuteAsync(null);

        Assert.Single(http.SentRequests);
        Assert.Empty(starts);
        Assert.False(vm.IsConnected);
        Assert.False(string.IsNullOrWhiteSpace(vm.SmpErrorText));
        Assert.Equal(OldDomain, Recovery(store).Vless.Servers.Single().Dnstt!.Domain);
    }

    [AvaloniaTheory]
    [InlineData("empty")]
    [InlineData("throw")]
    public async Task TransientFeedFailureKeepsTheOldGrantAndTheNextConnectRetries(string failure)
    {
        var active = ServerUriParser.Parse(Link(OldDomain, 'a'));
        var store = new InMemorySettingsStore();
        store.Save(GeneratedSettings(active,
            FeedEntry(Feed, ServerUriParser.Parse(Link(OldDomain, 'a')))), AppPaths.ConfigYamlPath);
        var http = new FakeHttpClient();
        if (failure == "empty") http.Setup(Feed, string.Empty);
        else http.ThrowOn(Feed, new HttpRequestException("fixture"));
        SubscriptionFetcher.Http = http;
        using var vm = new MainWindowViewModel(store);
        var source = Assert.Single(vm.Subscriptions);
        var starts = CaptureStarts(vm);

        await vm.SmpToggleConnectCommand.ExecuteAsync(null);

        Assert.Single(http.SentRequests);
        Assert.Single(starts);
        Assert.Equal(OldDomain, Active(starts[0]).Dnstt!.Domain);
        Assert.True(source.LastRefreshFailed);

        vm.IsConnected = false;
        var retry = new FakeHttpClient().Setup(Feed, Link(NewDomain, 'b'));
        SubscriptionFetcher.Http = retry;

        await vm.SmpToggleConnectCommand.ExecuteAsync(null);

        Assert.Single(retry.SentRequests);
        Assert.Equal(2, starts.Count);
        Assert.Equal(NewDomain, Active(starts[1]).Dnstt!.Domain);
    }

    [AvaloniaFact]
    public async Task FailedSaveLeavesTheFirstConnectRetryable()
    {
        var active = ServerUriParser.Parse(Link(OldDomain, 'a'));
        var settings = GeneratedSettings(active,
            FeedEntry(Feed, ServerUriParser.Parse(Link(OldDomain, 'a'))));
        settings.App.Language = "en";
        var inner = new InMemorySettingsStore();
        var store = new FailingSettingsStore(inner);
        store.Save(settings, AppPaths.ConfigYamlPath);
        var http = new FakeHttpClient().Setup(Feed, Link(NewDomain, 'b'));
        SubscriptionFetcher.Http = http;
        using var vm = new MainWindowViewModel(store);
        var starts = CaptureStarts(vm);

        store.FailNextSave = true;
        await vm.SmpToggleConnectCommand.ExecuteAsync(null);

        Assert.Single(http.SentRequests);
        Assert.Single(starts);
        Assert.Equal(NewDomain, Active(starts[0]).Dnstt!.Domain);
        Assert.Equal(NewDomain,
            Active(SettingsLoader.Copy(store.Load(AppPaths.ConfigYamlPath))).Dnstt!.Domain);

        // The in-refresh save failed, so the one-shot skip must be unset and the next connect
        // must fetch and save again rather than relying on memory only.
        vm.IsConnected = false;
        await vm.SmpToggleConnectCommand.ExecuteAsync(null);

        Assert.Equal(2, http.SentRequests.Count);
        Assert.Equal(2, starts.Count);
        Assert.Equal(NewDomain, Active(SettingsLoader.Copy(store.Load(AppPaths.ConfigYamlPath))).Dnstt!.Domain);

        vm.IsConnected = false;
        await vm.SmpToggleConnectCommand.ExecuteAsync(null);

        Assert.Equal(2, http.SentRequests.Count);
        Assert.Equal(3, starts.Count);
    }

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

    private sealed class SerializingSettingsStore(ISettingsStore inner) : ISettingsStore
    {
        public AppSettings Load(string? path = null) => SettingsLoader.Copy(inner.Load(path));

        public void Save(AppSettings settings, string? path = null) =>
            inner.Save(SettingsLoader.Copy(settings), path);

        public string? ResetToDefaults(string? path = null) => inner.ResetToDefaults(path);

        public string? ConsumeRecoveryNotice() => inner.ConsumeRecoveryNotice();

        public (int Count, string AtUtc) ConsumePlaceholderPruneNotice(AppSettings settings) =>
            inner.ConsumePlaceholderPruneNotice(settings);

        public void StartWatching(string? path = null, Action<AppSettings>? onReload = null) =>
            inner.StartWatching(path, onReload);

        public void StopWatching() => inner.StopWatching();
    }

    private sealed class FailingSettingsStore(ISettingsStore inner) : ISettingsStore
    {
        public bool FailNextSave { get; set; }

        public AppSettings Load(string? path = null) => inner.Load(path);

        public void Save(AppSettings settings, string? path = null)
        {
            if (FailNextSave)
            {
                FailNextSave = false;
                throw new IOException("fixture save failure");
            }
            inner.Save(settings, path);
        }

        public string? ResetToDefaults(string? path = null) => inner.ResetToDefaults(path);

        public string? ConsumeRecoveryNotice() => inner.ConsumeRecoveryNotice();

        public (int Count, string AtUtc) ConsumePlaceholderPruneNotice(AppSettings settings) =>
            inner.ConsumePlaceholderPruneNotice(settings);

        public void StartWatching(string? path = null, Action<AppSettings>? onReload = null) =>
            inner.StartWatching(path, onReload);

        public void StopWatching() => inner.StopWatching();
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
