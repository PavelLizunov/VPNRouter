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
    public void SaveServerLinks_KeepsTheHopAndSelectsTheChainedTarget()
    {
        var hop = new VlessServerEntry
        {
            Name = "relay",
            Protocol = "turnrelay",
            Server = "203.0.113.20",
            Port = 53492,
            OutboundId = "turnrelay-hop-1",
            TurnRelay = new TurnRelayConfig
            {
                TurnServer = "192.0.2.10:3478",
                TurnUsername = "user",
                TurnPassword = "secret",
                Password = "exit-secret",
                ServerFingerprint = new string('a', 64),
                Connections = 2,
                Mode = "dtls",
            },
        };
        const string targetUri =
            "vless://00000000-0000-4000-8000-000000000001@exit.invalid:443?security=none" +
            "&outbound=target-1&detour=turnrelay-hop-1#exit";
        var target = VlessUriParser.Parse(targetUri);

        using var prefs = new PreferenceFile(new());
        var saved = AndroidSubscriptionRefresh.SaveServerLinks(
            new[] { hop, target }, targetUri, "qr-command",
            prefs.Read, prefs.Commit, JsonSerializerOptions.Default);

        Assert.True(saved);
        Assert.Equal(2, prefs.Standalone.Count);
        Assert.Equal("exit", prefs.Read(AndroidSubscriptionRefresh.SelectedName));
        Assert.Equal("target-uri", prefs.Read(AndroidSubscriptionRefresh.ManualUri));
        Assert.Equal("manual", prefs.Read(AndroidSubscriptionRefresh.ConfigMode));

        var resolved = Assert.IsType<VlessServerEntry>(prefs.Resolve("qr-command"));
        var config = prefs.Prepare(resolved);
        Assert.Contains(config.Servers, row => row.IsTurnRelay && row.OutboundId == "turnrelay-hop-1");
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
            Task<int> Refresh(SubscriptionEntry entry) => AndroidSubscriptionRefresh.RefreshAsync(entry, prefs.ImportRevision, (updated, rejected, revision) =>
                AndroidSubscriptionRefresh.Save(updated, rejected, null, prefs.Read, prefs.Commit, JsonSerializerOptions.Default, revision));

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

    [Theory]
    [InlineData("free", false)]
    [InlineData("free", true)]
    [InlineData("subscription", false)]
    [InlineData("subscription", true)]
    [InlineData("disconnect", false)]
    [InlineData("disconnect", true)]
    public async Task SelectingRefresh_CannotReplaceNewerCommandAfterPreferencesReopen(string later, bool reject)
    {
        var old = VlessUriParser.Parse(OldTargetUri);
        var manual = VlessUriParser.Parse(ManualUri);
        var feed = new SubscriptionEntry { Id = "first", Url = Feed, Enabled = false, Servers = [old] };
        var second = new SubscriptionEntry { Id = "second", Url = "https://fixture.invalid/other", Enabled = false };
        using var prefs = new PreferenceFile(new()
        {
            ["subscriptions_json"] = JsonSerializer.Serialize(new[] { feed, second }),
            ["servers_json"] = JsonSerializer.Serialize(new[] { old, manual }),
            ["config_mode"] = "manual",
            ["vless_uri"] = ManualUri,
            ["selected_server_name"] = manual.Name,
        });
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var previous = SubscriptionFetcher.Http;
        SubscriptionFetcher.Http = new OrderedHttp(async request =>
        {
            if (request != 1) return FreshUri.Replace("fresh", "second");
            firstStarted.SetResult();
            await release.Task;
            return reject ? UnsupportedBody : FreshUri;
        });

        var selectionResult = true;
        Task<int> EarlyRefresh() => AndroidSubscriptionRefresh.RefreshAsync(feed, prefs.ImportRevision, (row, rejected, revision) =>
            selectionResult = AndroidSubscriptionRefresh.Save(row, rejected, "qr-command",
                prefs.Read, prefs.Commit, JsonSerializerOptions.Default, revision));
        var early = EarlyRefresh();
        try
        {
            await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            prefs.Commit(new Dictionary<string, string?> { [AndroidSubscriptionRefresh.UiCommand] = "newer" });
            if (later == "free")
            {
                var uri = ManualUri.Replace("manual", "free");
                Assert.True(AndroidSubscriptionRefresh.SaveSingleServer(VlessUriParser.Parse(uri), uri, "newer",
                    prefs.Read, prefs.Commit, JsonSerializerOptions.Default));
            }
            else if (later == "subscription")
            {
                bool selected = false;
                var secondCount = await AndroidSubscriptionRefresh.RefreshAsync(second, prefs.ImportRevision, (row, rejected, revision) =>
                    selected = AndroidSubscriptionRefresh.Save(row, rejected, "newer", prefs.Read, prefs.Commit,
                        JsonSerializerOptions.Default, revision)).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
                Assert.True(selected);
                Assert.Equal(1, secondCount);
            }
            var selectedState = prefs.Reload();
            release.SetResult();
            if (reject) await Assert.ThrowsAsync<UnsupportedSubscriptionChainException>(() => early);
            else Assert.Equal(1, await early);

            var reopened = prefs.Reload();
            foreach (var key in new[] { "vpn_ui_command", "selected_server_name", "vless_uri", "subscription_url", "config_mode" })
                Assert.Equal(selectedState.GetValueOrDefault(key), reopened.GetValueOrDefault(key));
            Assert.False(selectionResult);
            var saved = JsonSerializer.Deserialize<List<SubscriptionEntry>>(reopened["subscriptions_json"])!;
            Assert.False(saved[0].Enabled);
            if (reject) Assert.Empty(saved[0].Servers);
            else Assert.Equal("fresh.invalid", Assert.Single(saved[0].Servers).Server);
            Assert.Equal(later == "subscription", saved[1].Enabled);
            if (later == "subscription") Assert.Equal("second.invalid", Assert.Single(saved[1].Servers).Server);
            var standalone = JsonSerializer.Deserialize<List<VlessServerEntry>>(reopened["servers_json"])!;
            Assert.DoesNotContain(standalone, row => row.Server == old.Server);
            Assert.Contains(standalone, row => row.Server == manual.Server);

            var before = prefs.Contents;
            Assert.False(AndroidSubscriptionRefresh.SaveSingleServer(old, OldTargetUri, "qr-command",
                prefs.Read, prefs.Commit, JsonSerializerOptions.Default));
            Assert.Equal(before, prefs.Contents);
        }
        finally
        {
            release.TrySetResult();
            try { await early; }
            catch (UnsupportedSubscriptionChainException) { }
            finally { SubscriptionFetcher.Http = previous; }
        }
    }

    [Fact]
    public void SimpleConnect_UnchangedPaddedFreeUriKeepsTheSelectedServerWithAnotherSubscription()
    {
        var other = new SubscriptionEntry { Url = Feed, Enabled = true, Servers = [ServerUriParser.Parse(OldTargetUri)] };
        using var prefs = new PreferenceFile(new()
        {
            ["subscriptions_json"] = JsonSerializer.Serialize(new[] { other }),
            ["subscription_url"] = Feed,
            ["selected_server_name"] = other.Servers[0].Name,
            ["config_mode"] = "subscribe"
        });
        const string raw = " " + FreshUri + "\t";
        Assert.True(AndroidSubscriptionRefresh.SaveSingleServer(ServerUriParser.Parse(raw), raw, "qr-command",
            prefs.Read, prefs.Commit, JsonSerializerOptions.Default));

        var reopened = prefs.Reload();
        var saved = reopened["vless_uri"];
        Assert.Equal(raw, saved);
        Assert.False(AndroidSubscriptionRefresh.HasServerInputEdit(FreshUri, saved));
        Assert.False(AndroidSubscriptionRefresh.HasServerInputEdit(raw, FreshUri));
        Assert.True(AndroidSubscriptionRefresh.HasServerInputEdit(OldTargetUri, saved));
        var selected = Assert.Single(JsonSerializer.Deserialize<List<VlessServerEntry>>(reopened["servers_json"])!,
            row => row.Name == reopened["selected_server_name"]);
        Assert.True(selected.UseAsSingleOutbound);
        Assert.Equal("fresh.invalid", selected.Server);
        Assert.Equal("manual", reopened["config_mode"]);
        var retained = Assert.Single(JsonSerializer.Deserialize<List<SubscriptionEntry>>(reopened["subscriptions_json"])!);
        Assert.True(retained.Enabled);
        Assert.Equal(other.Servers[0].Server, Assert.Single(retained.Servers).Server);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ManualEditResolvesTheTypedServerAfterReopenWithAnotherFeedEnabled(bool legacyScalar)
    {
        var feed = new SubscriptionEntry { Url = Feed, Enabled = true, Servers = [ServerUriParser.Parse(OldTargetUri)] };
        using var prefs = new PreferenceFile(new()
        {
            ["subscriptions_json"] = JsonSerializer.Serialize(new[] { feed }),
            ["config_mode"] = "manual",
            ["vless_uri"] = ManualUri
        });
        if (!legacyScalar)
        {
            Assert.True(AndroidSubscriptionRefresh.SaveSingleServer(ServerUriParser.Parse(FreshUri), FreshUri, "qr-command",
                prefs.Read, prefs.Commit, JsonSerializerOptions.Default));
            Assert.True(AndroidSubscriptionRefresh.HasServerInputEdit(ManualUri, prefs.Read("vless_uri")));
            Assert.True(AndroidSubscriptionRefresh.SaveSingleServer(ServerUriParser.Parse(ManualUri), ManualUri, "qr-command",
                prefs.Read, prefs.Commit, JsonSerializerOptions.Default));
            var accepted = prefs.Contents;
            Assert.False(AndroidSubscriptionRefresh.SaveSingleServer(ServerUriParser.Parse(FreshUri), FreshUri, "obsolete",
                prefs.Read, prefs.Commit, JsonSerializerOptions.Default));
            Assert.Equal(accepted, prefs.Contents);
        }

        else
        {
            // The old Simple summary saved this unrelated name immediately after the manual edit.
            prefs.Commit(new Dictionary<string, string?> { ["selected_server_name"] = feed.Servers[0].Name });
        }
        var active = prefs.Resolve();
        Assert.NotNull(active);
        Assert.Equal(ServerUriParser.Parse(ManualUri).Server, active.Server);
        Assert.True(active.UseAsSingleOutbound);
        var afterMigration = prefs.Contents;
        Assert.Equal(active.Server, prefs.Resolve()?.Server);
        Assert.Equal(afterMigration, prefs.Contents);
        Assert.Equal(active.Name, prefs.Read("selected_server_name"));
        var feeds = prefs.Feeds;
        Assert.True(Assert.Single(feeds).Enabled);
        Assert.Equal("exit.invalid", Assert.Single(feeds[0].Servers).Server);
    }

    [Fact]
    public void SharedManualProfileReplacesSelectionBeforeTheNextConnect()
    {
        using var prefs = new PreferenceFile(new());
        Assert.True(AndroidSubscriptionRefresh.SaveSingleServer(ServerUriParser.Parse(FreshUri), FreshUri, "qr-command",
            prefs.Read, prefs.Commit, JsonSerializerOptions.Default));
        var snapshot = ConfigShareDocument.TryParse(ConfigShareDocument.Serialize(new ConfigShareDocument
        {
            ConfigMode = "manual", ManualVlessUri = ManualUri
        })).Document!;
        AndroidSubscriptionRefresh.ImportProfile(snapshot, prefs.Read, prefs.Commit, JsonSerializerOptions.Default);
        Assert.False(AndroidSubscriptionRefresh.HasServerInputEdit(ManualUri, prefs.Read("vless_uri")));
        Assert.False(AndroidSubscriptionRefresh.IsCurrentSelection("qr-command", prefs.Read));
        Assert.Equal("qr-command", prefs.Read(AndroidSubscriptionRefresh.UiCommand));
        var chosen = prefs.Resolve();
        Assert.NotNull(chosen);
        Assert.True(chosen.UseAsSingleOutbound);
        Assert.Equal("manual.invalid", chosen.Server);
        var beforeFailure = prefs.Contents;
        Assert.False(AndroidSubscriptionRefresh.SaveSingleServer(ServerUriParser.Parse(FreshUri), FreshUri, "qr-command",
            prefs.Read, prefs.Commit, JsonSerializerOptions.Default));
        snapshot.ManualVlessUri = "invalid";
        Assert.Throws<FormatException>(() => AndroidSubscriptionRefresh.ImportProfile(snapshot, prefs.Read, prefs.Commit,
            JsonSerializerOptions.Default));
        Assert.Equal(beforeFailure, prefs.Contents);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SelectedFeedOwnsItsDisplayNameAfterReopen(bool useRefresh)
    {
        var manual = ServerUriParser.Parse(ManualUri);
        var a = ServerUriParser.Parse(OldTargetUri);
        var b = ServerUriParser.Parse(FreshUri);
        a.Name = b.Name = manual.Name;
        var feed = new SubscriptionEntry { Url = Feed, Id = "feed", Servers = [b] };
        var other = new SubscriptionEntry { Url = Feed, Id = "other", Servers = [a] };
        using var prefs = new PreferenceFile(new()
        {
            ["subscriptions_json"] = JsonSerializer.Serialize(new[] { other, feed })
        });
        Assert.True(AndroidSubscriptionRefresh.SaveSingleServer(manual, ManualUri, "qr-command",
            prefs.Read, prefs.Commit, JsonSerializerOptions.Default));
        if (useRefresh)
            Assert.True(AndroidSubscriptionRefresh.Save(feed, false, "qr-command", prefs.Read, prefs.Commit,
                JsonSerializerOptions.Default, prefs.ImportRevision));
        else
            Assert.True(AndroidSubscriptionRefresh.SaveFeedSelection(feed, b, null, prefs.Feeds, prefs.Read, prefs.Commit,
                JsonSerializerOptions.Default));
        var chosen = prefs.Resolve();
        Assert.NotNull(chosen);
        Assert.Equal("fresh.invalid", chosen.Server);
        Assert.Equal(feed.Id, prefs.Read(AndroidSubscriptionRefresh.SelectedFeedId));
        var config = prefs.Prepare(chosen);
        Assert.Equal(3, config.GetActiveServers().Count);
        Assert.Equal("fresh.invalid", config.GetActiveServers()[0].Server);

        // Old clients saved only the subscription URL. A retained manual row cannot shadow that source.
        other.Url += "/other";
        prefs.Commit(new Dictionary<string, string?>
        {
            [AndroidSubscriptionRefresh.SelectedFeedId] = null,
            [AndroidSubscriptionRefresh.SelectedFeedRow] = null,
            ["subscriptions_json"] = JsonSerializer.Serialize(new[] { other, feed })
        });
        Assert.Equal("fresh.invalid", prefs.Resolve()?.Server);
    }

    [Fact]
    public void LegacyPickerNameOverridesAnOlderUrlOnlyWhenUnambiguous()
    {
        var a = new SubscriptionEntry { Id = "a", Url = Feed, Servers = [ServerUriParser.Parse(OldTargetUri)] };
        var b = new SubscriptionEntry { Id = "b", Url = Feed + "/b", Servers = [ServerUriParser.Parse(FreshUri)] };
        using var prefs = new PreferenceFile(new()
        {
            ["subscription_url"] = a.Url, ["selected_server_name"] = b.Servers[0].Name,
            ["subscriptions_json"] = JsonSerializer.Serialize(new[] { a, b })
        });
        Assert.Equal(b.Url, prefs.SimpleInput);
        Assert.Equal("fresh.invalid", prefs.GeneratePrimary(prefs.Resolve()!));
        Assert.Equal(b.Id, prefs.Read(AndroidSubscriptionRefresh.SelectedFeedId));
        Assert.Equal(b.Url, prefs.Read("subscription_url"));

        a.Servers[0].Name = b.Servers[0].Name;
        prefs.Commit(new Dictionary<string, string?>
        {
            [AndroidSubscriptionRefresh.SelectedFeedId] = null, [AndroidSubscriptionRefresh.SelectedFeedRow] = null,
            ["subscription_url"] = a.Url, ["subscriptions_json"] = JsonSerializer.Serialize(new[] { a, b })
        });
        Assert.Equal("exit.invalid", prefs.GeneratePrimary(prefs.Resolve()!));
    }

    [Theory]
    [InlineData("remove")]
    [InlineData("disable")]
    [InlineData("change-url")]
    [InlineData("remove-last")]
    public void RegistrationEditReconcilesSelectionAndUnchangedSimpleInput(string edit)
    {
        var a = new SubscriptionEntry { Id = "a", Url = Feed, Servers = [ServerUriParser.Parse(OldTargetUri)] };
        var b = new SubscriptionEntry { Id = "b", Url = Feed + "/b", Enabled = edit != "remove-last", Servers = [ServerUriParser.Parse(FreshUri)] };
        using var prefs = new PreferenceFile(new() { ["subscriptions_json"] = JsonSerializer.Serialize(new[] { a, b }) });
        prefs.Select(a, a.Servers[0]);
        var displayed = prefs.SimpleInput;
        var pending = prefs.Resolve()!;
        var next = prefs.Feeds;
        if (edit == "change-url") next[0].Url = Feed + "/edited";
        else if (edit == "disable") next[0].Enabled = false;
        else next.RemoveAt(0);
        prefs.Edit(next);

        var savedInput = prefs.SimpleInput;
        var expectedUrl = edit == "change-url" ? next[0].Url : edit == "remove-last" ? string.Empty : b.Url;
        Assert.Equal(expectedUrl, savedInput);
        var rebound = AndroidSubscriptionRefresh.RebindSimpleInput(displayed, displayed, savedInput);
        Assert.Equal(expectedUrl, rebound);
        Assert.False(AndroidSubscriptionRefresh.HasServerInputEdit(rebound, savedInput));
        Assert.Equal(edit == "change-url" ? expectedUrl : null, prefs.SubscriptionToRefresh(rebound));
        Assert.Equal(b.Servers[0].Server, prefs.Feeds.Single(f => f.Id == "b").Servers.Single().Server);
        Assert.Throws<InvalidOperationException>(() => prefs.GeneratePrimary(pending));
        if (edit == "change-url" || edit == "remove-last")
        {
            Assert.Null(prefs.Resolve());
            Assert.Null(prefs.Read(AndroidSubscriptionRefresh.SelectedFeedRow));
            if (edit == "change-url") Assert.Empty(prefs.Feeds.Single(f => f.Id == "a").Servers);
        }
        else Assert.Equal("fresh.invalid", prefs.GeneratePrimary(prefs.Resolve()!));
    }

    [Theory]
    [InlineData("reject")]
    [InlineData("remove-feed")]
    [InlineData("remove-row")]
    public async Task GenerationRejectsRetiredSourceEvenWhenAnotherFeedHasTheIdenticalRow(string change)
    {
        var row = ServerUriParser.Parse(OldTargetUri);
        var replacement = ServerUriParser.Parse(FreshUri);
        replacement.Name = row.Name;
        var a = new SubscriptionEntry { Id = "a", Url = Feed, Servers = [row, replacement] };
        var b = new SubscriptionEntry { Id = "b", Url = Feed + "/b", Servers = [row] };
        using var prefs = new PreferenceFile(new() { ["subscriptions_json"] = JsonSerializer.Serialize(new[] { a, b }) });
        prefs.Select(a, row);
        var pending = prefs.Resolve()!;
        if (change == "reject")
            await Assert.ThrowsAsync<UnsupportedSubscriptionChainException>(() => ReimportAsync(a, prefs, UnsupportedBody));
        else if (change == "remove-feed")
            prefs.Edit(prefs.Feeds.Where(f => f.Id == b.Id).ToList());
        else
        {
            var next = prefs.Feeds;
            next[0].Servers.RemoveAt(0);
            prefs.Edit(next);
            Assert.Equal("fresh.invalid", prefs.GeneratePrimary(prefs.Resolve()!));
        }
        Assert.Throws<InvalidOperationException>(() => prefs.GeneratePrimary(pending));
        Assert.Equal(row.Server, prefs.Feeds.Single(f => f.Id == b.Id).Servers.Single().Server);
    }

    [Theory]
    [InlineData(false, false, "a")]
    [InlineData(true, false, "a")]
    [InlineData(false, true, "a")]
    [InlineData(true, true, "a")]
    [InlineData(false, false, null)]
    [InlineData(true, false, null)]
    [InlineData(false, true, null)]
    [InlineData(true, true, null)]
    public async Task PageRefreshRetiringTheResolvedRowCancelsDispatch(bool autoSelect, bool rejected, string? storedId)
    {
        var row = ServerUriParser.Parse(OldTargetUri);
        var a = new SubscriptionEntry { Id = storedId!, Url = Feed, Servers = [row] };
        var b = new SubscriptionEntry { Id = "b", Url = Feed + "/b", Servers = [ServerUriParser.Parse(ManualUri)] };
        using var prefs = new PreferenceFile(new() { ["subscriptions_json"] = JsonSerializer.Serialize(new[] { a, b }) });
        prefs.Select(a, row);
        prefs.Commit(new Dictionary<string, string?> { [AndroidSubscriptionRefresh.UiCommand] = "connect" });
        var selected = prefs.Resolve("connect")!;
        var candidate = autoSelect ? prefs.Prepare(selected) : new VlessConfig { Servers = [selected] };
        Assert.Contains("exit.invalid", PreferenceFile.GenerateMembers(candidate));

        // The old configuration already exists when the non-selecting page refresh completes.
        if (rejected)
            await Assert.ThrowsAsync<UnsupportedSubscriptionChainException>(() => ReimportAsync(a, prefs, UnsupportedBody, command: null));
        else
            Assert.Equal(1, await ReimportAsync(a, prefs, FreshUri, command: null));
        Assert.False(AndroidSubscriptionRefresh.IsCurrentSelection("connect", prefs.Read));
        Assert.DoesNotContain(prefs.Feeds.Single(f => f.Id == a.Id).Servers, s => s.Server == "exit.invalid");
    }

    [Theory]
    [InlineData("unchanged")]
    [InlineData("unrelated")]
    [InlineData("new-command")]
    [InlineData("established")]
    public async Task PageRefreshLeavesUnaffectedCommandInPlace(string scenario)
    {
        var row = ServerUriParser.Parse(OldTargetUri);
        var a = new SubscriptionEntry { Id = "a", Url = Feed, Servers = [row] };
        var b = new SubscriptionEntry { Id = "b", Url = Feed + "/b", Servers = [ServerUriParser.Parse(ManualUri)] };
        using var prefs = new PreferenceFile(new() { ["subscriptions_json"] = JsonSerializer.Serialize(new[] { a, b }) });
        var selection = scenario == "unrelated" ? b : a;
        prefs.Select(selection, selection.Servers[0]);
        var command = "connect";
        prefs.Commit(new Dictionary<string, string?> { [AndroidSubscriptionRefresh.UiCommand] = command });
        Assert.NotNull(prefs.Resolve(command));
        if (scenario == "new-command")
        {
            command = "next";
            prefs.Commit(new Dictionary<string, string?> { [AndroidSubscriptionRefresh.UiCommand] = command });
        }
        if (scenario == "established")
            prefs.Commit(new Dictionary<string, string?> { ["vpn_state_command"] = command, ["vpn_state"] = "connected" });
        Assert.Equal(1, await ReimportAsync(a, prefs, scenario == "unchanged" ? OldTargetUri : FreshUri, command: null));
        Assert.True(AndroidSubscriptionRefresh.IsCurrentSelection(command, prefs.Read));
    }

    [Theory]
    [InlineData("shared", false)]
    [InlineData("shared", true)]
    [InlineData(null, false)]
    [InlineData(null, true)]
    public async Task RefreshUsesTheUrlToDistinguishRegistrationsWithTheSameId(string? storedId, bool rejected)
    {
        var first = new SubscriptionEntry { Id = storedId!, Url = Feed, Servers = [ServerUriParser.Parse(OldTargetUri)] };
        var second = new SubscriptionEntry { Id = storedId!, Url = Feed + "/b", Servers = [ServerUriParser.Parse(ManualUri)] };
        second.Servers[0].Name = first.Servers[0].Name;
        using var prefs = new PreferenceFile(new() { ["subscriptions_json"] = JsonSerializer.Serialize(new[] { first, second }) });
        prefs.Select(second, second.Servers[0]);
        prefs.Commit(new Dictionary<string, string?> { [AndroidSubscriptionRefresh.UiCommand] = "connect" });
        var selected = prefs.Resolve("connect")!;
        Assert.Equal("manual.invalid", selected.Server);

        if (rejected)
            await Assert.ThrowsAsync<UnsupportedSubscriptionChainException>(() => ReimportAsync(first, prefs, UnsupportedBody, command: null));
        else
            Assert.Equal(1, await ReimportAsync(first, prefs, FreshUri, command: null));
        Assert.True(AndroidSubscriptionRefresh.IsCurrentSelection("connect", prefs.Read));
        Assert.Equal("manual.invalid", prefs.GeneratePrimary(selected));

        Assert.Equal(1, await ReimportAsync(second, prefs, FreshUri, command: null));
        Assert.False(AndroidSubscriptionRefresh.IsCurrentSelection("connect", prefs.Read));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingNativeRouteSurvivesOnlyAnEquivalentRefresh(bool changeUpstream)
    {
        const string body = """
            {"outbounds":[
              {"type":"vless","tag":"target","server":"exit.invalid","server_port":443,"uuid":"00000000-0000-4000-8000-000000000001","detour":"hop"},
              {"type":"vless","tag":"hop","server":"via.invalid","server_port":8443,"uuid":"00000000-0000-4000-8000-000000000002"}
            ]}
            """;
        var feed = new SubscriptionEntry { Id = "chain", Url = Feed };
        using var prefs = new PreferenceFile(new() { ["subscriptions_json"] = JsonSerializer.Serialize(new[] { feed }) });
        Assert.Equal(2, await ReimportAsync(feed, prefs, body));
        var cached = Assert.Single(prefs.Feeds);
        prefs.Select(cached, cached.Servers.Single(row => !string.IsNullOrEmpty(row.DetourVia)));
        prefs.Commit(new Dictionary<string, string?> { [AndroidSubscriptionRefresh.UiCommand] = "connect" });
        var pending = prefs.Resolve("connect")!;
        void CheckGeneratedChain()
        {
            var generated = PreferenceFile.Generate(prefs.Prepare(pending));
            var proxy = generated.Outbounds.Single(row => row.Tag == "proxy");
            Assert.Equal("exit.invalid", proxy.Server);
            Assert.Equal("chain-entry", proxy.Detour);
            var hop = generated.Outbounds.Single(row => row.Tag == proxy.Detour);
            Assert.Equal("via.invalid", hop.Server);
            Assert.Equal(8443, hop.ServerPort);
        }
        CheckGeneratedChain();
        Assert.Equal(2, await ReimportAsync(feed, prefs, changeUpstream ? body.Replace("8443", "9443") : body, command: null));
        var rows = Assert.Single(prefs.Feeds).Servers;
        var refreshed = rows.Single(row => !string.IsNullOrEmpty(row.DetourVia));
        Assert.NotEqual(pending.OutboundId, refreshed.OutboundId);
        Assert.NotEqual(pending.DetourVia, refreshed.DetourVia);
        Assert.Equal(rows.Single(row => row.Server == "via.invalid").OutboundId, refreshed.DetourVia);
        Assert.Equal(!changeUpstream, AndroidSubscriptionRefresh.IsCurrentSelection("connect", prefs.Read));
        if (changeUpstream)
            Assert.Throws<InvalidOperationException>(() => prefs.Prepare(pending));
        else
            CheckGeneratedChain();
    }

    [Fact]
    public void EditingLegacyUrlRemovesRetiredMembersFromTheWholeGeneratedGroup()
    {
        var rows = new[] { ServerUriParser.Parse(OldTargetUri), ServerUriParser.Parse(DuplicateTargetUri) };
        using var prefs = new PreferenceFile(new()
        {
            ["subscription_url"] = Feed, ["servers_json"] = JsonSerializer.Serialize(rows)
        });
        List<SubscriptionEntry> ReadLegacy() => [AndroidSubscriptionRefresh.LegacyFeed(Feed, prefs.Standalone)];
        var edited = ReadLegacy();
        edited[0].Url += "/new";
        AndroidSubscriptionRefresh.SaveRegistrations(ReadLegacy(), edited, "subscribe", prefs.Read, prefs.Commit, JsonSerializerOptions.Default);
        Assert.Empty(prefs.Standalone);
        var refreshed = Assert.Single(prefs.Feeds);
        refreshed.Servers = [ServerUriParser.Parse(FreshUri), ServerUriParser.Parse(ManualUri)];
        prefs.Commit(new Dictionary<string, string?> { [AndroidSubscriptionRefresh.UiCommand] = "new-feed" });
        Assert.True(AndroidSubscriptionRefresh.Save(refreshed, false, "new-feed", prefs.Read, prefs.Commit, JsonSerializerOptions.Default, null));
        var members = PreferenceFile.GenerateMembers(prefs.Prepare(prefs.Resolve()!));
        Assert.Equal(new[] { "fresh.invalid", "manual.invalid" }, members);
    }

    [Fact]
    public void UnnamedExplicitSelectionOwnsTheIndicatorAndGeneratedOutbound()
    {
        var first = ServerUriParser.Parse(OldTargetUri);
        var second = ServerUriParser.Parse(FreshUri);
        first.Name = second.Name = string.Empty;
        var a = new SubscriptionEntry { Id = "a", Url = Feed, Servers = [first, second] };
        var b = new SubscriptionEntry { Id = "b", Url = Feed, Servers = [second] };
        using var prefs = new PreferenceFile(new() { ["subscriptions_json"] = JsonSerializer.Serialize(new[] { b, a }) });
        prefs.Select(a, second);
        var reopened = prefs.Feeds;
        var indicator = AndroidSubscriptionRefresh.FindFeedRow(reopened, prefs.Read)!;
        Assert.Null(indicator.In(reopened[0]));
        Assert.Same(reopened[1].Servers[1], indicator.In(reopened[1]));
        Assert.Equal("fresh.invalid", prefs.GeneratePrimary(prefs.Resolve()!));
    }

    [Fact]
    public void RepeatedRefreshCannotReplaceAnUnavailableDnsttSelection()
    {
        var selected = new VlessServerEntry
        {
            Name = "DNSTT", Protocol = "dnstt", Server = "8.8.8.8", Port = 53,
            OutboundId = "dnstt:node", Dnstt = new DnsttProfile { Domain = "tunnel.example.invalid" }
        };
        var original = new SubscriptionEntry { Id = "a", Url = Feed, Servers = [selected] };
        using var prefs = new PreferenceFile(new() { ["subscriptions_json"] = JsonSerializer.Serialize(new[] { original }) });
        prefs.Select(original, selected);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var refreshed = new SubscriptionEntry { Id = original.Id, Url = Feed, Servers = [ServerUriParser.Parse(FreshUri)] };
            Assert.False(AndroidSubscriptionRefresh.Save(refreshed, false, null, prefs.Read, prefs.Commit,
                JsonSerializerOptions.Default, prefs.ImportRevision));
            Assert.Null(AndroidSubscriptionRefresh.FindFeedRow(prefs.Feeds, prefs.Read));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LateRefreshCannotReplaceImportedRegistrationWithSameIdAndUrl(bool reject)
    {
        var a = new SubscriptionEntry { Id = "a", Url = Feed, Servers = [ServerUriParser.Parse(OldTargetUri)] };
        using var prefs = new PreferenceFile(new() { ["subscriptions_json"] = JsonSerializer.Serialize(new[] { a }) });
        prefs.Select(a, a.Servers[0]);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reply = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var previous = SubscriptionFetcher.Http;
        SubscriptionFetcher.Http = new OrderedHttp(_ => { started.SetResult(); return reply.Task; });
        var refresh = AndroidSubscriptionRefresh.RefreshAsync(a, prefs.ImportRevision, (updated, rejected, revision) =>
            AndroidSubscriptionRefresh.Save(updated, rejected, null, prefs.Read, prefs.Commit, JsonSerializerOptions.Default, revision));
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            var imported = new SubscriptionEntry { Id = a.Id, Url = a.Url, Servers = [ServerUriParser.Parse(FreshUri)] };
            var payload = ConfigShareDocument.Serialize(new ConfigShareDocument { ConfigMode = "subscribe", Subscriptions = [imported] });
            AndroidSubscriptionRefresh.ImportProfile(ConfigShareDocument.TryParse(payload).Document!,
                prefs.Read, prefs.Commit, JsonSerializerOptions.Default);
            var importedFile = prefs.Contents;
            reply.SetResult(reject ? UnsupportedBody : DuplicateTargetUri);
            await Assert.ThrowsAsync<InvalidOperationException>(() => refresh);
            Assert.Equal(importedFile, prefs.Contents);
            Assert.Equal("fresh.invalid", prefs.GeneratePrimary(prefs.Resolve()!));
        }
        finally
        {
            reply.TrySetResult(FreshUri);
            try { await refresh; }
            catch (InvalidOperationException) { }
            finally { SubscriptionFetcher.Http = previous; }
        }
        prefs.Commit(new Dictionary<string, string?> { [AndroidSubscriptionRefresh.UiCommand] = "next-qr" });
        Assert.Equal(1, await ReimportAsync(a, prefs, OldTargetUri, command: "next-qr"));
        Assert.Equal("exit.invalid", prefs.GeneratePrimary(prefs.Resolve()!));
    }

    [Fact]
    public void CustomSaveFromFeedRebindsHomeAndKeepsCustomOnFirstConnectAndReopen()
    {
        var a = new SubscriptionEntry { Id = "a", Url = Feed, Servers = [ServerUriParser.Parse(OldTargetUri)] };
        var b = new SubscriptionEntry { Id = "b", Url = Feed + "/b", Servers = [ServerUriParser.Parse(FreshUri)] };
        using var prefs = new PreferenceFile(new() { ["subscriptions_json"] = JsonSerializer.Serialize(new[] { a, b }) });
        prefs.Select(a, a.Servers[0]);
        var feedInput = prefs.SimpleInput;
        var registrations = prefs.Read("subscriptions_json");
        const string raw = """{"outbounds":[{"type":"socks","tag":"proxy","server":"custom.invalid","server_port":1080}]}""";
        Assert.True(CustomConfigInjector.Validate(raw).IsValid);
        AndroidSubscriptionRefresh.SaveCustomProfile(raw, prefs.Read, prefs.Commit, JsonSerializerOptions.Default);
        var afterSave = AndroidSubscriptionRefresh.RebindSimpleInput(feedInput, null, prefs.SimpleInput);
        Assert.Equal(string.Empty, afterSave);
        var homeInput = AndroidSubscriptionRefresh.RebindSimpleInput(afterSave, prefs.SimpleInput, prefs.SimpleInput);
        var reopenedInput = AndroidSubscriptionRefresh.RebindSimpleInput(null, null, prefs.SimpleInput);
        foreach (var input in new[] { homeInput, reopenedInput })
        {
            Assert.Equal(string.Empty, input);
            Assert.False(AndroidSubscriptionRefresh.HasServerInputEdit(input, prefs.SimpleInput));
            Assert.Null(prefs.SubscriptionToRefresh(input));
        }
        Assert.Equal(string.Empty, AndroidSubscriptionRefresh.RebindSimpleInput(feedInput, feedInput, prefs.SimpleInput));
        Assert.Null(prefs.Read("subscription_url"));
        Assert.Null(prefs.Resolve());
        Assert.Equal("custom", prefs.Read("config_mode"));
        Assert.Equal(registrations, prefs.Read("subscriptions_json"));
        Assert.Equal(raw, prefs.Read("custom_config_json"));
        var settings = new AppSettings();
        settings.App.BypassRussianTraffic = false;
        settings.App.RoutingMode = "full";
        using var generated = JsonDocument.Parse(CustomConfigInjector.Inject(prefs.Read("custom_config_json")!, [], settings));
        var outbound = generated.RootElement.GetProperty("outbounds").EnumerateArray().Single(o => o.GetProperty("tag").GetString() == "proxy");
        Assert.Equal("custom.invalid", outbound.GetProperty("server").GetString());
        Assert.Equal(FreshUri, AndroidSubscriptionRefresh.RebindSimpleInput(FreshUri, feedInput, prefs.SimpleInput));
    }

    [Fact]
    public void RetypingPreviousSimpleUriAfterManualListPickUsesTheTypedServer()
    {
        var row = ServerUriParser.Parse(FreshUri);
        var manual = new SubscriptionEntry { Name = "Manual", Url = string.Empty, Servers = [row] };
        var feed = new SubscriptionEntry { Url = Feed, Servers = [ServerUriParser.Parse(OldTargetUri)] };
        using var prefs = new PreferenceFile(new() { ["subscriptions_json"] = JsonSerializer.Serialize(new[] { manual, feed }) });
        Assert.True(AndroidSubscriptionRefresh.SaveSingleServer(ServerUriParser.Parse(ManualUri), ManualUri, "qr-command",
            prefs.Read, prefs.Commit, JsonSerializerOptions.Default));
        prefs.Select(manual, row);
        Assert.Equal("fresh.invalid", prefs.GeneratePrimary(prefs.Resolve()!));
        var openManualList = prefs.Feeds.Single(f => f.Id == manual.Id);
        var selected = AndroidSubscriptionRefresh.FindFeedRow(prefs.Feeds, prefs.Read);
        Assert.Same(openManualList.Servers.Single(), selected?.In(openManualList));
        Assert.Null(selected?.In(feed));
        var input = AndroidSubscriptionRefresh.RebindSimpleInput(ManualUri, null, prefs.SimpleInput);
        Assert.Empty(input);
        input = AndroidSubscriptionRefresh.RebindSimpleInput(ManualUri, input, prefs.SimpleInput);
        Assert.Equal(ManualUri, input);
        Assert.Null(prefs.SubscriptionToRefresh(input));
        Assert.True(AndroidSubscriptionRefresh.HasServerInputEdit(input, prefs.SimpleInput));
        prefs.Commit(new Dictionary<string, string?> { [AndroidSubscriptionRefresh.UiCommand] = "new-connect" });
        Assert.True(AndroidSubscriptionRefresh.SaveSingleServer(ServerUriParser.Parse(input), input, "new-connect",
            prefs.Read, prefs.Commit, JsonSerializerOptions.Default));
        Assert.Equal("manual.invalid", prefs.GeneratePrimary(prefs.Resolve()!));
        Assert.Equal(ManualUri, prefs.SimpleInput);
        Assert.True(prefs.Feeds.Single(f => f.Id == feed.Id).Enabled);
    }

    [Fact]
    public void EditingAnIndependentRegistrationKeepsTheSavedFreeProfile()
    {
        var other = new SubscriptionEntry { Url = Feed, Servers = [ServerUriParser.Parse(FreshUri)] };
        using var prefs = new PreferenceFile(new() { ["subscriptions_json"] = JsonSerializer.Serialize(new[] { other }) });
        Assert.True(AndroidSubscriptionRefresh.SaveSingleServer(ServerUriParser.Parse(ManualUri), ManualUri, "qr-command",
            prefs.Read, prefs.Commit, JsonSerializerOptions.Default));
        var before = prefs.Reload();
        var next = prefs.Feeds;
        next[0].Url += "/edited";
        prefs.Edit(next);
        foreach (var key in new[] { "selected_server_name", "config_mode", "vless_uri", "servers_json" })
            Assert.Equal(before[key], prefs.Read(key));
        Assert.Equal(ManualUri, prefs.SimpleInput);
        Assert.True(prefs.Resolve()!.UseAsSingleOutbound);
    }

    [Fact]
    public void CustomSavePreservesTheSingleFeedLegacyLayoutAsARegistration()
    {
        var old = ServerUriParser.Parse(OldTargetUri);
        using var prefs = new PreferenceFile(new()
        {
            ["subscription_url"] = Feed, ["servers_json"] = JsonSerializer.Serialize(new[] { old })
        });
        AndroidSubscriptionRefresh.SaveCustomProfile("{}", prefs.Read, prefs.Commit, JsonSerializerOptions.Default);
        var feed = Assert.Single(prefs.Feeds);
        Assert.Equal(Feed, feed.Url);
        Assert.Equal(old.Server, Assert.Single(feed.Servers).Server);
        Assert.Null(prefs.Resolve());
        Assert.Null(prefs.SubscriptionToRefresh(prefs.SimpleInput));
    }

    [Fact]
    public async Task LaterFreeSelection_PreservesLegacyFeedOwnershipForRejection()
    {
        var old = VlessUriParser.Parse(OldTargetUri);
        using var prefs = new PreferenceFile(new()
        {
            [AndroidSubscriptionRefresh.UiCommand] = "free-command",
            ["subscription_url"] = Feed,
            ["servers_json"] = JsonSerializer.Serialize(new[] { old }),
            ["selected_server_name"] = old.Name,
            ["config_mode"] = "subscribe",
        });
        var feed = new SubscriptionEntry { Id = "legacy-default", Url = Feed, Servers = [old] };
        Assert.True(AndroidSubscriptionRefresh.SaveSingleServer(VlessUriParser.Parse(ManualUri), ManualUri, "free-command",
            prefs.Read, prefs.Commit, JsonSerializerOptions.Default));
        await Assert.ThrowsAsync<UnsupportedSubscriptionChainException>(() => ReimportAsync(feed, prefs, UnsupportedBody));
        var reopened = prefs.Reload();
        var legacy = Assert.Single(JsonSerializer.Deserialize<List<SubscriptionEntry>>(reopened["subscriptions_json"])!);
        Assert.Equal(Feed, legacy.Url);
        Assert.Empty(legacy.Servers);
        var selected = Assert.Single(JsonSerializer.Deserialize<List<VlessServerEntry>>(reopened["servers_json"])!);
        Assert.Equal(selected.Name, reopened["selected_server_name"]);
        Assert.Equal(ManualUri, reopened["vless_uri"]);
        Assert.Equal("manual", reopened["config_mode"]);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task SupersededSuccess_LeavesLegacySelectionUntilAConfirmedRejection(bool switchToFree, bool migrateOldDuplicate)
    {
        var old = VlessUriParser.Parse(OldTargetUri);
        using var prefs = new PreferenceFile(new()
        {
            [AndroidSubscriptionRefresh.UiCommand] = "stop",
            ["subscription_url"] = Feed,
            ["servers_json"] = JsonSerializer.Serialize(new[] { old }),
            ["selected_server_name"] = old.Name,
            ["vless_uri"] = OldTargetUri,
            ["config_mode"] = "subscribe",
        });
        var feed = new SubscriptionEntry { Id = "legacy-default", Url = Feed };
        Assert.Equal(1, await ReimportAsync(feed, prefs, FreshUri));
        var saved = prefs.Reload();
        Assert.Equal(old.Name, saved["selected_server_name"]);
        Assert.Equal(OldTargetUri, saved["vless_uri"]);
        Assert.Equal(old.Server, AndroidSubscriptionRefresh.ReadRetainedManual(prefs.Read)?.Server);
        Assert.Empty(JsonSerializer.Deserialize<List<VlessServerEntry>>(saved["servers_json"])!);
        if (switchToFree)
        {
            if (migrateOldDuplicate) prefs.Commit(new Dictionary<string, string?>
            {
                ["servers_json"] = JsonSerializer.Serialize(new[] { old })
            });
            prefs.Commit(new Dictionary<string, string?> { [AndroidSubscriptionRefresh.UiCommand] = "free" });
            Assert.True(AndroidSubscriptionRefresh.SaveSingleServer(VlessUriParser.Parse(ManualUri), ManualUri, "free",
                prefs.Read, prefs.Commit, JsonSerializerOptions.Default));
        }
        await Assert.ThrowsAsync<UnsupportedSubscriptionChainException>(() => ReimportAsync(feed, prefs, UnsupportedBody));
        var reopened = prefs.Reload();
        Assert.Null(AndroidSubscriptionRefresh.ReadRetainedManual(prefs.Read));
        var standalone = JsonSerializer.Deserialize<List<VlessServerEntry>>(reopened["servers_json"])!;
        if (switchToFree)
        {
            Assert.Equal(VlessUriParser.Parse(ManualUri).Server, Assert.Single(standalone).Server);
            Assert.Equal(standalone[0].Name, reopened["selected_server_name"]);
            Assert.Equal(ManualUri, reopened["vless_uri"]);
            Assert.Equal("manual", reopened["config_mode"]);
        }
        else
        {
            Assert.Equal("stop", reopened[AndroidSubscriptionRefresh.UiCommand]);
            Assert.False(reopened.ContainsKey("selected_server_name"));
            Assert.False(reopened.ContainsKey("vless_uri"));
            Assert.Empty(standalone);
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

    private static async Task<int> ReimportAsync(SubscriptionEntry entry, PreferenceFile prefs, string body, int status = 200,
        string? command = "qr-command")
    {
        var previous = SubscriptionFetcher.Http;
        SubscriptionFetcher.Http = new FakeHttpClient().Setup(entry.Url, body, status);
        try
        {
            return await AndroidSubscriptionRefresh.RefreshAsync(entry, prefs.ImportRevision, (updated, rejected, revision) =>
                AndroidSubscriptionRefresh.Save(updated, rejected, command, prefs.Read, prefs.Commit, JsonSerializerOptions.Default, revision));
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

        public PreferenceFile(Dictionary<string, string> initial)
        {
            initial.TryAdd(AndroidSubscriptionRefresh.UiCommand, "qr-command");
            File.WriteAllText(_path, JsonSerializer.Serialize(initial));
        }

        public List<SubscriptionEntry> Feeds => JsonSerializer.Deserialize<List<SubscriptionEntry>>(Read("subscriptions_json") ?? "[]")!;
        public List<VlessServerEntry> Standalone => JsonSerializer.Deserialize<List<VlessServerEntry>>(Read("servers_json") ?? "[]")!;
        public string? ImportRevision => Read(AndroidSubscriptionRefresh.ImportRevision);
        private string Mode => AndroidSubscriptionRefresh.ReadMode(Read);
        public void Select(SubscriptionEntry source, VlessServerEntry row) =>
            Assert.True(AndroidSubscriptionRefresh.SaveFeedSelection(source, row, null, Feeds, Read, Commit, JsonSerializerOptions.Default));
        public void Edit(List<SubscriptionEntry> next) => AndroidSubscriptionRefresh.SaveRegistrations(Feeds, next,
            Mode, Read, Commit, JsonSerializerOptions.Default);
        public string SimpleInput => AndroidSubscriptionRefresh.SavedServerInput(Mode, Read, () => Feeds);
        public string? SubscriptionToRefresh(string input) =>
            AndroidSubscriptionRefresh.SubscriptionToRefresh(input, Mode, Read, () => Feeds);
        public VlessConfig Prepare(VlessServerEntry selected)
        {
            var config = new VlessConfig();
            AndroidSubscriptionRefresh.PreparePool(config, selected, Standalone, Feeds, Mode, Read, JsonSerializerOptions.Default);
            return config;
        }
        public string? GeneratePrimary(VlessServerEntry selected) => GenerateMembers(Prepare(selected)).First();

        public static SingBoxConfig Generate(VlessConfig prepared)
        {
            var settings = new AppSettings { Vless = prepared };
            settings.App.BypassRussianTraffic = false;
            settings.App.RoutingMode = "full";
            return ConfigGenerator.Generate(new Profile { Name = "AndroidDefault" }, [], settings,
                strictDnsOverride: false, isServerAlive: null, resolveRuleSet: (_, _) => null);
        }

        public static List<string?> GenerateMembers(VlessConfig prepared)
        {
            var config = Generate(prepared);
            var proxy = config.Outbounds.Single(row => row.Tag == "proxy");
            return proxy.Type == "urltest"
                ? proxy.Outbounds!.Select(tag => config.Outbounds.Single(row => row.Tag == tag).Server).ToList()
                : [proxy.Server];
        }
        public VlessServerEntry? Resolve() => AndroidSubscriptionRefresh.ResolveActiveServer(Standalone, Feeds,
            Mode, Read, Commit, JsonSerializerOptions.Default);
        public VlessServerEntry? Resolve(string command) => AndroidSubscriptionRefresh.ResolveForConnect(command, Standalone, Feeds,
            Mode, Read, Commit, JsonSerializerOptions.Default);

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
