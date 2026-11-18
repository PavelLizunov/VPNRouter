using System.Text;
using System.Text.Json;
using VPNRouter.Android;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;

namespace VPNRouter.Tests;

[Collection(SubscriptionFetcherCollection.Name)]
public class AndroidSubscriptionRefreshTests
{
    private const string Feed = "https://fixture.invalid/feed";
    private const string ManualUri = "vless://00000000-0000-4000-8000-000000000001@manual.invalid:443?security=none#manual";
    private const string FreshUri = "vless://00000000-0000-4000-8000-000000000003@fresh.invalid:443?security=none#fresh";
    private const string OldTargetUri = "vless://00000000-0000-4000-8000-000000000001@exit.invalid:443?security=none#old%20target";
    private const string DuplicateTargetUri = "vless://00000000-0000-4000-8000-000000000002@alias.invalid:443?security=none#old%20alias";
    private const string UnsupportedBody = """
        {"outbounds":[{"type":"vless","tag":"old target","server":"exit.invalid","server_port":443,
                       "uuid":"00000000-0000-4000-8000-000000000001","detour":"unavailable"}]}
        """;

    [Theory]
    [InlineData(200, false)]
    [InlineData(200, true)]
    [InlineData(503, false)]
    [InlineData(503, true)]
    public async Task QrReimport_EvictsRejectedCacheButKeepsUsableDataOnNetworkFailure(int status, bool manualSelected)
    {
        var oldTarget = VlessUriParser.Parse(OldTargetUri);
        var aliasTarget = VlessUriParser.Parse(DuplicateTargetUri);
        var manual = VlessUriParser.Parse(ManualUri);
        var feed = new SubscriptionEntry { Id = "feed", Url = Feed, UserInfo = "first quota", Servers = [oldTarget], LastServerCount = 1 };
        var other = new SubscriptionEntry
        {
            Id = "other", Url = "https://fixture.invalid/FEED", Enabled = false, UserInfo = "offline quota",
            Servers = [new() { Name = "other server", Server = "other.invalid" }], LastServerCount = 1
        };
        var duplicate = new SubscriptionEntry
        {
            Id = "duplicate", Name = "second registration", Url = Feed, Enabled = !manualSelected, UserInfo = "second quota",
            Servers = [oldTarget, aliasTarget], LastServerCount = 2
        };
        using var prefs = new PreferenceFile(new()
        {
            ["subscriptions_json"] = JsonSerializer.Serialize(new[] { feed, other, duplicate }),
            ["servers_json"] = JsonSerializer.Serialize(new[] { oldTarget, aliasTarget, manual }),
            ["selected_server_name"] = manualSelected ? manual.Name : oldTarget.Name,
            ["config_mode"] = manualSelected ? "manual" : "subscribe",
            [manualSelected ? "vless_uri" : "subscription_url"] = manualSelected ? ManualUri : Feed,
            ["theme"] = "light"
        });
        var before = prefs.Contents;
        var detached = JsonSerializer.Deserialize<List<SubscriptionEntry>>(prefs.Read("subscriptions_json")!)![0];
        Task<int> Refresh() => ReimportAsync(detached, prefs, UnsupportedBody, status);

        if (status == 503)
        {
            Assert.Equal(0, await Refresh());
            Assert.Equal(0, prefs.CommitCount);
            Assert.Equal(before, prefs.Contents);
            return;
        }

        await Assert.ThrowsAsync<UnsupportedSubscriptionChainException>(Refresh);
        Assert.Equal(1, prefs.CommitCount);
        var saved = prefs.Reload();
        var feeds = JsonSerializer.Deserialize<List<SubscriptionEntry>>(saved["subscriptions_json"])!;
        Assert.Empty(feeds[0].Servers);
        Assert.Equal(0, feeds[0].LastServerCount);
        Assert.Equal(feed.UserInfo, feeds[0].UserInfo);
        Assert.NotNull(feeds[0].LastRefreshedAt);
        Assert.Equal(JsonSerializer.Serialize(other), JsonSerializer.Serialize(feeds[1]));
        var savedDuplicate = Assert.Single(feeds, s => s.Id == duplicate.Id);
        Assert.Empty(savedDuplicate.Servers);
        Assert.Equal(0, savedDuplicate.LastServerCount);
        Assert.Equal(feeds[0].LastRefreshedAt, savedDuplicate.LastRefreshedAt);
        Assert.Equal(duplicate.Name, savedDuplicate.Name);
        Assert.Equal(duplicate.Enabled, savedDuplicate.Enabled);
        Assert.Equal(duplicate.UserInfo, savedDuplicate.UserInfo);
        var standalone = JsonSerializer.Deserialize<List<VlessServerEntry>>(saved["servers_json"])!;
        Assert.Equal(manual.Server, Assert.Single(standalone).Server);
        Assert.Equal("light", saved["theme"]);
        if (manualSelected)
        {
            Assert.Equal(manual.Name, saved["selected_server_name"]);
            Assert.Equal(ManualUri, saved["vless_uri"]);
            Assert.Equal("manual", saved["config_mode"]);
        }
        else
        {
            Assert.NotEqual(oldTarget.Name, saved.GetValueOrDefault("selected_server_name"));
            Assert.Equal(Feed, saved["subscription_url"]);
        }
    }

    [Fact]
    public async Task QrReimport_LegacySubscriptionCannotReloadItsFlattenedStandaloneCache()
    {
        var old = VlessUriParser.Parse(OldTargetUri);
        using var prefs = new PreferenceFile(new()
        {
            ["subscription_url"] = Feed,
            ["servers_json"] = JsonSerializer.Serialize(new[] { old }),
            ["selected_server_name"] = old.Name,
            ["vless_uri"] = OldTargetUri,
            ["config_mode"] = "subscribe"
        });
        // The old single-feed format did not persist an ID.
        var legacy = new SubscriptionEntry { Url = Feed, Servers = [old] };
        await Assert.ThrowsAsync<UnsupportedSubscriptionChainException>(() => ReimportAsync(legacy, prefs, UnsupportedBody));

        var saved = prefs.Reload();
        Assert.Empty(JsonSerializer.Deserialize<List<VlessServerEntry>>(saved["servers_json"])!);
        var registered = Assert.Single(JsonSerializer.Deserialize<List<SubscriptionEntry>>(saved["subscriptions_json"])!);
        Assert.Equal(Feed, registered.Url);
        Assert.Empty(registered.Servers);
        Assert.False(saved.ContainsKey("selected_server_name"));
        Assert.False(saved.ContainsKey("vless_uri"));
    }

    [Fact]
    public async Task ValidQrImport_SelectsTheSubscriptionAndKeepsTheSavedCustomConfig()
    {
        var feed = new SubscriptionEntry { Id = "feed", Url = Feed, Enabled = false };
        using var prefs = new PreferenceFile(new()
        {
            ["subscriptions_json"] = JsonSerializer.Serialize(new[] { feed }),
            ["config_mode"] = "custom",
            ["custom_config_name"] = "previous config"
        });

        Assert.Equal(1, await ReimportAsync(feed, prefs, FreshUri));

        var saved = prefs.Reload();
        Assert.Equal("subscribe", saved["config_mode"]);
        Assert.Equal("fresh", saved["selected_server_name"]);
        Assert.Equal(Feed, saved["subscription_url"]);
        Assert.Equal("previous config", saved["custom_config_name"]);
        var registered = Assert.Single(JsonSerializer.Deserialize<List<SubscriptionEntry>>(saved["subscriptions_json"])!);
        Assert.True(registered.Enabled);
        Assert.Equal("fresh.invalid", Assert.Single(registered.Servers).Server);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QrRejection_PreservesProfilesWithIndependentOwners(bool otherEnabled)
    {
        var shared = VlessUriParser.Parse(OldTargetUri);
        var manual = VlessUriParser.Parse(ManualUri);
        var retired = VlessUriParser.Parse(DuplicateTargetUri);
        var first = new SubscriptionEntry { Id = "first", Url = Feed, Servers = [shared] };
        var duplicate = new SubscriptionEntry { Id = "second", Url = Feed, Servers = [manual, retired] };
        var other = new SubscriptionEntry { Id = "other", Url = "https://fixture.invalid/FEED", Enabled = otherEnabled, Servers = [shared] };
        using var prefs = new PreferenceFile(new()
        {
            ["subscriptions_json"] = JsonSerializer.Serialize(new[] { first, duplicate, other }),
            ["servers_json"] = JsonSerializer.Serialize(new[] { shared, manual, retired }),
            ["subscription_url"] = Feed,
            ["selected_server_name"] = manual.Name,
            ["vless_uri"] = ManualUri,
            ["config_mode"] = "manual"
        });

        await Assert.ThrowsAsync<UnsupportedSubscriptionChainException>(() => ReimportAsync(first, prefs, UnsupportedBody));

        var saved = prefs.Reload();
        var feeds = JsonSerializer.Deserialize<List<SubscriptionEntry>>(saved["subscriptions_json"])!;
        Assert.Empty(feeds[0].Servers);
        Assert.Empty(feeds[1].Servers);
        Assert.Equal(JsonSerializer.Serialize(other), JsonSerializer.Serialize(feeds[2]));
        Assert.Equal(JsonSerializer.Serialize(new[] { shared, manual }), saved["servers_json"]);
        Assert.Equal(manual.Name, saved["selected_server_name"]);
        Assert.Equal(ManualUri, saved["vless_uri"]);
        Assert.Equal("manual", saved["config_mode"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentRefreshes_DuplicateRegistrationsCommitInRequestOrder(bool rejectFirst)
    {
        var first = new SubscriptionEntry { Id = "first", Url = Feed, Servers = [VlessUriParser.Parse(OldTargetUri)] };
        var second = new SubscriptionEntry { Id = "second", Url = Feed, Servers = [VlessUriParser.Parse(DuplicateTargetUri)] };
        var manual = VlessUriParser.Parse(ManualUri);
        using var prefs = new PreferenceFile(new()
        {
            ["subscriptions_json"] = JsonSerializer.Serialize(new[] { first, second }),
            ["servers_json"] = JsonSerializer.Serialize(new[] { first.Servers[0], second.Servers[0], manual }),
            ["subscription_url"] = Feed,
            ["selected_server_name"] = second.Servers[0].Name
        });
        List<SubscriptionEntry> Reopen() => JsonSerializer.Deserialize<List<SubscriptionEntry>>(prefs.Read("subscriptions_json")!)!;
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstCommitVisible = false;
        var previous = SubscriptionFetcher.Http;
        SubscriptionFetcher.Http = new OrderedHttp(async requestNumber =>
        {
            if (requestNumber == 1)
            {
                firstStarted.SetResult();
                await releaseFirst.Task;
            }
            else
            {
                var snapshot = Reopen();
                firstCommitVisible = rejectFirst ? snapshot.All(s => s.Servers.Count == 0)
                    : snapshot[0].Servers.Single().Name == "fresh";
            }
            var reject = requestNumber == 1 ? rejectFirst : !rejectFirst;
            return reject ? UnsupportedBody : FreshUri;
        });
        try
        {
            Task<int> Refresh(SubscriptionEntry entry) => AndroidSubscriptionRefresh.RefreshAsync(entry, (updated, rejected) =>
                AndroidSubscriptionRefresh.Save(updated, rejected, false, prefs.Read, prefs.Commit, JsonSerializerOptions.Default));

            var earlier = Refresh(first);
            await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            var later = Refresh(second);
            releaseFirst.SetResult();
            var rejected = rejectFirst ? earlier : later;
            var accepted = rejectFirst ? later : earlier;
            await Assert.ThrowsAsync<UnsupportedSubscriptionChainException>(() => rejected);
            Assert.Equal(1, await accepted);
            Assert.True(firstCommitVisible);
            Assert.Equal(2, prefs.CommitCount);

            var saved = Reopen();
            Assert.Empty(saved[0].Servers);
            if (rejectFirst)
                Assert.Equal("fresh.invalid", Assert.Single(saved[1].Servers).Server);
            else
                Assert.Empty(saved[1].Servers);
            Assert.Null(prefs.Read("selected_server_name"));
            var standalone = JsonSerializer.Deserialize<List<VlessServerEntry>>(prefs.Read("servers_json")!)!;
            Assert.Equal(manual.Server, Assert.Single(standalone).Server);
        }
        finally
        {
            releaseFirst.TrySetResult();
            SubscriptionFetcher.Http = previous;
        }
    }

    private sealed class OrderedHttp(Func<int, Task<string>> reply) : IHttpClient
    {
        private int _requests;

        public async Task<HttpResponse> SendAsync(HttpRequest request, CancellationToken ct = default)
        {
            var body = await reply(Interlocked.Increment(ref _requests));
            return new HttpResponse(200, new Dictionary<string, string>(), Encoding.UTF8.GetBytes(body), TimeSpan.Zero);
        }

        public Task<IHttpStreamingResponse> SendStreamingAsync(HttpRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private static async Task<int> ReimportAsync(SubscriptionEntry entry, PreferenceFile prefs, string body, int status = 200)
    {
        var previous = SubscriptionFetcher.Http;
        SubscriptionFetcher.Http = new FakeHttpClient().Setup(Feed, body, status);
        try
        {
            return await AndroidSubscriptionRefresh.RefreshAsync(entry, (updated, rejected) =>
                AndroidSubscriptionRefresh.Save(updated, rejected, true, prefs.Read, prefs.Commit, JsonSerializerOptions.Default));
        }
        finally
        {
            SubscriptionFetcher.Http = previous;
        }
    }

    // Models the Android preference boundary. Every read reopens the file.
    private sealed class PreferenceFile : IDisposable
    {
        private readonly string _path = Path.GetTempFileName();

        public PreferenceFile(Dictionary<string, string> initial) =>
            File.WriteAllText(_path, JsonSerializer.Serialize(initial));

        public string Contents => File.ReadAllText(_path);
        public int CommitCount { get; private set; }
        public Dictionary<string, string> Reload() => JsonSerializer.Deserialize<Dictionary<string, string>>(Contents)!;
        public string? Read(string key) => Reload().GetValueOrDefault(key);

        public void Commit(IReadOnlyDictionary<string, string?> changes)
        {
            var values = Reload();
            foreach (var (key, value) in changes)
            {
                if (value is null) values.Remove(key);
                else values[key] = value;
            }
            File.WriteAllText(_path, JsonSerializer.Serialize(values));
            CommitCount++;
        }

        public void Dispose() => File.Delete(_path);
    }
}
