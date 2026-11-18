using System.Text;
using System.Text.Json;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Headless.Protocol;
using VPNRouter.Headless.Storage;

namespace VPNRouter.Headless.Tests;

public static class SubscriptionRefreshChecks
{
    private const string FreshBody = "ss://aes-256-gcm:synthetic-password@192.0.2.8:443#fresh";
    private const string RejectedBody = """
        {"outbounds":[{"type":"vless","tag":"old-target","server":"exit.invalid","server_port":443,
        "uuid":"00000000-0000-4000-8000-000000000001","detour":"missing-private-hop"}]}
        """;

    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "vpnrouter-refresh-" + Guid.NewGuid().ToString("N"));
        var previousHttp = SubscriptionFetcher.Http;
        try
        {
            foreach (var scenario in new[] { "http", "transport", "empty", "invalid", "cancel", "partial", "success", "none" })
                await CheckAsync(Path.Combine(root, scenario), scenario);
            foreach (var scenario in new[] { "rotated", "removed", "invalid-native" })
                await CheckRequiredDetourAsync(Path.Combine(root, "detour-" + scenario), scenario);
            await CheckRejectedChainAsync(Path.Combine(root, "chain"), refreshAll: false);
            await CheckRejectedChainAsync(Path.Combine(root, "chain-batch"), refreshAll: true);
            foreach (var scenario in new[] { "success", "rejected", "transient" })
                await CheckLegacyRefreshAsync(Path.Combine(root, "legacy-" + scenario), scenario);
            await CheckLegacyRefreshAsync(Path.Combine(root, "legacy-scalar-only"), "rejected", scalarOnly: true);
            await CheckStaleSameUrlResultAsync(Path.Combine(root, "newer-rejected"), rejectNewer: true);
            await CheckStaleSameUrlResultAsync(Path.Combine(root, "newer-valid"), rejectNewer: false);
            Console.WriteLine("[SubscriptionRefreshChecks] Offline refresh and persistence scenarios passed");
        }
        finally
        {
            SubscriptionFetcher.Http = previousHttp;
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static async Task CheckAsync(string dir, string scenario)
    {
        const string safeMessage = "Subscription refresh failed or returned no usable servers. Cached servers were kept.";
        var storage = new ConfigStorage(dir);
        var settings = storage.GetSettings();
        settings.App.Subscriptions = scenario == "none" ? new() : new() { Entry("first") };
        if (scenario == "partial") settings.App.Subscriptions.Add(Entry("second"));
        if (scenario == "transport")
        {
            settings.App.ConfigMode = "subscribe";
            settings.App.ActiveSubscriptionServer = settings.Vless.ActiveServer = "cached";
        }
        storage.SaveSettings(settings, storage.CurrentRevision);
        var before = File.ReadAllBytes(storage.ConfigPath);
        var revision = storage.CurrentRevision;
        using var cancellation = new CancellationTokenSource();
        SubscriptionFetcher.Http = new RefreshHttp((request, ct) =>
        {
            if (scenario == "cancel")
            {
                cancellation.Cancel();
                ct.ThrowIfCancellationRequested();
            }
            if (scenario == "transport")
                throw new HttpRequestException("Synthetic provider failure: https://fixture.invalid/private-token");
            var body = scenario switch
            {
                "empty" => "",
                "invalid" => "not a subscription",
                _ => FreshBody
            };
            var status = scenario == "http" || (scenario == "partial" && request.Uri.AbsolutePath == "/second") ? 503 : 200;
            return Task.FromResult(Reply(body, status));
        });
        var session = new FakeRouterSession { State = "connected" };
        await using var backend = new RouterBackend(session, dir);
        await using var handler = new RouterBackendHandler(backend);
        var dispatcher = new ProtocolDispatcher(handler);
        var response = await dispatcher.DispatchAsync(new ProtocolRequest(1, "refresh-1", "subscriptions.refresh",
            JsonSerializer.SerializeToElement(new { revision })), cancellation.Token);
        using var frame = JsonDocument.Parse(response.ToUtf8Bytes());
        var wire = frame.RootElement;
        Assert(wire.GetProperty("id").GetString() == "refresh-1", "Response must retain the request identity");
        if (scenario is "success" or "none")
        {
            Assert(wire.TryGetProperty("result", out _), $"{scenario}: valid refresh or no targets must succeed");
            if (scenario == "success")
            {
                Assert(storage.GetSettings().App.Subscriptions[0].Servers.Single().Name == "fresh", "Fresh servers must persist");
                Assert(session.ApplyCallCount == 1, "Successful refresh must apply the committed candidate");
            }
        }
        else
        {
            var error = wire.GetProperty("error");
            var cancelled = scenario == "cancel";
            Assert(error.GetProperty("code").GetString() == (cancelled ? "cancelled" : "subscription_refresh_failed"),
                "Failure must expose the structured code; caller cancellation takes precedence");
            Assert(error.GetProperty("message").GetString() == (cancelled ? "The operation was cancelled." : safeMessage),
                "Wire message must be fixed and contain no provider input");
            Assert(before.SequenceEqual(File.ReadAllBytes(storage.ConfigPath)), "Failure must preserve exact cached settings");
            Assert(storage.CurrentRevision == revision, "Failure must preserve the configuration revision");
            Assert(session.ApplyCallCount == 0, "Failure must not reapply the active connection");
            Assert(session.State == "connected", "Failure must preserve the active session");
            if (scenario == "transport")
            {
                var reloaded = new ConfigStorage(dir).GetSettings();
                Assert(reloaded.App.ConfigMode == "subscribe" && reloaded.App.ActiveSubscriptionServer == "cached"
                    && VlessServersResolver.Resolve(reloaded).Single().Name == "cached",
                    "The last usable subscription and selection must survive a network failure and reload");
            }
        }
    }

    private static async Task CheckRequiredDetourAsync(string dir, string scenario)
    {
        const string user = "00000000-0000-4000-8000-000000000001";
        const string direct = $"vless://{user}@exit.invalid:443?security=none#node";
        static string Target(string id) => $"vless://{user}@exit.invalid:443?security=none&detour={id}#node";
        static string Hop(string id, int port) => $"vless://{user}@entry.invalid:{port}?security=none&outbound={id}#via";
        var storage = new ConfigStorage(dir);
        var settings = storage.GetSettings();
        var target = ServerUriParser.Parse(Target("old"));
        var source = Entry("selected");
        source.Servers = [ServerUriParser.Parse(direct), target, ServerUriParser.Parse(Hop("old", 443))];
        settings.App.Subscriptions = [source];
        settings.App.ConfigMode = "subscribe";
        settings.App.ActiveSubscriptionId = source.Id;
        settings.App.ActiveSubscriptionServer = target.Name;
        settings.App.ActiveSubscriptionRow = SubscriptionEntry.RowKey(target);
        storage.SaveSettings(settings, storage.CurrentRevision);
        var revision = storage.CurrentRevision;
        var original = File.ReadAllBytes(storage.ConfigPath);
        var updated = scenario == "rotated";
        var body = updated ? string.Join("\n", direct, Target("fresh"), Hop("fresh", 8443))
            : scenario == "removed" ? direct : RejectedBody;
        SubscriptionFetcher.Http = new RefreshHttp((_, _) => Task.FromResult(Reply(body)));
        var session = new FakeRouterSession { State = "connected" };
        await using var handler = new RouterBackendHandler(new RouterBackend(session, dir));
        var dispatcher = new ProtocolDispatcher(handler);
        var response = await dispatcher.DispatchAsync(new(1, "refresh", "subscriptions.refresh",
            JsonSerializer.SerializeToElement(new { revision, id = source.Id })), CancellationToken.None);

        Assert(session.State == "connected" && session.DisconnectCallCount == 0, "The current session must stay connected");
        Assert(session.ApplyCallCount == (updated ? 1 : 0), "Only a usable selected chain may reach Apply");
        if (scenario == "invalid-native")
        {
            Assert(response.Error?.Code == "subscription_chain_unsupported", "A confirmed parser rejection must evict the feed");
            var reopened = new ConfigStorage(dir).GetSettings();
            Assert(reopened.App.Subscriptions.Single().Servers.Count == 0, "Reopening must not revive a rejected registration");
            VlessServersResolver.Resolve(reopened);
            Assert(!reopened.Vless.GetActiveServers().Any(s => s.Server == target.Server || s.OutboundId == "old"),
                "Neither a direct sibling nor its former hop may recover from the rejected feed");
            return;
        }
        if (updated) Assert(response.Error == null, "A single replacement chain should be accepted");
        else Assert(response.Error?.Code == "subscription_refresh_failed" && storage.CurrentRevision == revision &&
            original.SequenceEqual(File.ReadAllBytes(storage.ConfigPath)), "Rejection must preserve the exact recovery file");
        var saved = new ConfigStorage(dir).GetSettings();
        VlessServersResolver.Resolve(saved);
        var active = saved.Vless.GetActiveServers();
        Assert(active.Count == 2 && active[0].DetourVia == (updated ? "fresh" : "old") &&
            active[0].DetourVia == active[1].OutboundId && active[1].Port == (updated ? 8443 : 443),
            "Recovery must select the registered detour even with an identical direct sibling first");
        if (!updated) return;

        var listed = await dispatcher.DispatchAsync(new(1, "list", "servers.list", JsonSerializer.SerializeToElement(new { })),
            CancellationToken.None);
        using var list = JsonDocument.Parse(listed.ToUtf8Bytes());
        var siblings = list.RootElement.GetProperty("result").GetProperty("items").EnumerateArray()
            .Where(row => row.GetProperty("source").GetString() == "subscription" &&
                row.GetProperty("subscriptionId").GetString() == source.Id && row.GetProperty("name").GetString() == "node")
            .ToArray();
        Assert(siblings.Length == 2 && !siblings[0].GetProperty("selected").GetBoolean() && siblings[1].GetProperty("selected").GetBoolean(),
            "Only the registered chained row should be selected when a direct sibling shares its name");
        var directId = siblings[0].GetProperty("id").GetString();
        var selection = await dispatcher.DispatchAsync(new(1, "select", "servers.select",
            JsonSerializer.SerializeToElement(new { revision = storage.CurrentRevision, id = directId })), CancellationToken.None);
        Assert(selection.Error == null, "An explicit choice of the direct sibling must supersede the detour");
        saved = new ConfigStorage(dir).GetSettings();
        VlessServersResolver.Resolve(saved);
        Assert(saved.Vless.GetActiveServers().Single().DetourVia.Length == 0, "The explicit direct choice must survive reload");
    }

    private static async Task CheckRejectedChainAsync(string dir, bool refreshAll)
    {
        static VlessServerEntry Flattened() => new()
        {
            Name = "old-target", Server = "exit.invalid", Port = 443, Security = "tls",
            Tls = new VlessTlsConfig { Enabled = true, ServerName = "exit.invalid" },
            Transport = new VlessTransportConfig { Type = "ws", Path = "/fixture" },
            Uuid = "00000000-0000-4000-8000-000000000001"
        };

        var storage = new ConfigStorage(dir);
        var settings = storage.GetSettings();
        var rejected = Entry("rejected");
        rejected.Servers = [Flattened()];
        var other = Entry("other");
        other.Url = "https://fixture.invalid/REJECTED";
        other.Enabled = refreshAll;
        var shared = other.Servers.Single();
        var alias = Entry("alias");
        alias.Url = rejected.Url;
        alias.Enabled = !refreshAll;
        alias.UserInfo = "alias quota";
        var aliasRow = new VlessServerEntry { Name = "old-alias", Server = "old-alias.invalid" };
        alias.Servers = [aliasRow, shared];
        alias.LastServerCount = alias.Servers.Count;
        var manual = new VlessServerEntry { Name = "manual", Server = "manual.invalid" };
        var sameName = new VlessServerEntry { Name = "old-target", Server = "other-manual.invalid" };
        settings.App.Subscriptions = [other, rejected, alias];
        settings.App.ConfigMode = "subscribe";
        settings.App.SubscriptionUrl = refreshAll ? other.Url : rejected.Url;
        settings.App.SubscriptionServers = refreshAll ? Entry("other").Servers : [Flattened()];
        settings.App.ActiveSubscriptionServer = settings.Vless.ActiveServer = "old-target";
        settings.Vless.Servers = [Flattened(), aliasRow, manual, sameName, shared];
        // Older settings mirrored the selected row here without its TLS or transport fields.
        settings.Vless.Server = rejected.Servers[0].Server;
        settings.Vless.Port = rejected.Servers[0].Port;
        settings.Vless.Uuid = rejected.Servers[0].Uuid;
        settings.Vless.Security = rejected.Servers[0].Security;
        settings.App.StrictDns = true;
        storage.SaveSettings(settings, storage.CurrentRevision);

        var requests = new List<string>();
        SubscriptionFetcher.Http = new RefreshHttp((request, _) =>
        {
            requests.Add(request.Uri.AbsolutePath);
            var body = request.Uri.AbsolutePath == "/rejected" ? RejectedBody :
                "ss://aes-256-gcm:synthetic-password@192.0.2.8:443#staged";
            return Task.FromResult(Reply(body));
        });
        var response = await DispatchRefreshAsync(dir, storage.CurrentRevision, refreshAll ? null : rejected.Id);
        using var frame = JsonDocument.Parse(response.ToUtf8Bytes());
        var error = frame.RootElement.GetProperty("error");
        Assert(error.GetProperty("code").GetString() == "subscription_chain_unsupported", "Expected an unsupported-chain error");
        Assert(error.GetProperty("message").GetString() == "The subscription uses an unsupported proxy chain. Its cached servers were removed.",
            "The rejection must explain eviction without exposing provider data");
        Assert(requests.SequenceEqual(refreshAll ? new[] { "/REJECTED", "/rejected" } : new[] { "/rejected" }),
            "The fixture must reach the rejected feed through the requested refresh path");

        var reloaded = new ConfigStorage(dir).GetSettings();
        var purged = reloaded.App.Subscriptions.Single(s => s.Id == rejected.Id);
        var purgedAlias = reloaded.App.Subscriptions.Single(s => s.Id == alias.Id);
        var kept = reloaded.App.Subscriptions.Single(s => s.Id == other.Id);
        Assert(purged.Servers.Count == 0 && purged.LastServerCount == 0 && purged.Enabled && purged.Url == rejected.Url,
            "Reload must retain the subscription registration and evict its historical rows");
        Assert(purgedAlias.Servers.Count == 0 && purgedAlias.LastServerCount == 0
            && purgedAlias.Name == alias.Name && purgedAlias.Url == rejected.Url && purgedAlias.Enabled == alias.Enabled
            && purgedAlias.UserInfo == alias.UserInfo, "Reload must evict the duplicate cache and retain its registration");
        var legacy = reloaded.App.SubscriptionServers;
        Assert(refreshAll ? legacy.Single().Server == other.Servers.Single().Server : legacy.Count == 0,
            "Reload must keep a different feed's legacy cache and evict the rejected feed's mirror");
        Assert(reloaded.Vless.Servers.Select(s => s.Server).SequenceEqual(new[] { manual.Server, sameName.Server, shared.Server }),
            "Evict both flattened copies and preserve manual rows, including a matching name and a row shared with another URL");
        Assert(string.IsNullOrEmpty(reloaded.Vless.Server) && string.IsNullOrEmpty(reloaded.Vless.Uuid),
            "The scalar fallback must not retain the evicted destination and credentials");
        Assert(kept.Enabled == other.Enabled && kept.Servers.Single().Server == other.Servers.Single().Server
            && kept.LastRefreshedAt == other.LastRefreshedAt && reloaded.App.StrictDns,
            "Eviction must preserve the other feed's saved state and unrelated settings");
        var active = refreshAll ? kept.Servers.Single().Name : manual.Name;
        var mode = refreshAll ? "subscribe" : "generated";
        Assert(reloaded.App.ConfigMode == mode, $"Rejected chain: expected {mode} mode after reload");
        Assert(reloaded.App.ActiveSubscriptionServer == (refreshAll ? active : string.Empty),
            $"Rejected chain: wrong subscription selection after reload in {mode} mode");
        Assert(reloaded.Vless.ActiveServer == active, $"Rejected chain: wrong active server after reload in {mode} mode");
        var resolved = VlessServersResolver.Resolve(reloaded);
        Assert(resolved.Any(s => s.Name == active) && resolved.All(s => s.Server != "exit.invalid" && s.Server != aliasRow.Server),
            "Offline selection after restart must not return the historical direct target");
    }

    private static async Task CheckLegacyRefreshAsync(string dir, string scenario, bool scalarOnly = false)
    {
        const string url = "https://fixture.invalid/legacy-feed";
        var storage = new ConfigStorage(dir);
        var settings = storage.GetSettings();
        var old = new VlessServerEntry
        {
            Name = "old-target", OutboundId = "subscription-exit", Server = "exit.invalid",
            Port = 443, Uuid = "00000000-0000-4000-8000-000000000001"
        };
        var manual = new VlessServerEntry { Name = "manual", Server = "manual.invalid" };
        settings.App.Subscriptions = [];
        settings.App.SubscriptionUrl = url;
        settings.App.SubscriptionServers = [old];
        settings.App.ConfigMode = "subscribe";
        settings.App.ActiveSubscriptionServer = settings.Vless.ActiveServer = old.Name;
        settings.Vless.Servers = scalarOnly ? [] : [old, manual];
        settings.Vless.Server = old.Server;
        settings.Vless.Port = old.Port;
        settings.Vless.Uuid = old.Uuid;
        settings.App.StrictDns = true;
        storage.SaveSettings(settings, storage.CurrentRevision);
        var before = File.ReadAllBytes(storage.ConfigPath);
        var revision = storage.CurrentRevision;

        var requests = new List<string>();
        var body = scenario == "rejected" ? RejectedBody : FreshBody;
        var status = scenario == "transient" ? 503 : 200;
        SubscriptionFetcher.Http = new RefreshHttp((request, _) =>
        {
            requests.Add(request.Uri.OriginalString);
            return Task.FromResult(Reply(body, status));
        });
        var response = await DispatchRefreshAsync(dir, revision);
        Assert(requests.SequenceEqual(new[] { url }), "Refresh-all must fetch the exact legacy URL once");

        var reopened = new ConfigStorage(dir);
        var reloaded = reopened.GetSettings();
        if (scalarOnly)
        {
            Assert(response.Error?.Code == "subscription_chain_unsupported" &&
                reloaded.App.Subscriptions.Single().Servers.Count == 0 && reloaded.App.SubscriptionServers.Count == 0 &&
                reloaded.Vless.Servers.Count == 0 && string.IsNullOrEmpty(reloaded.Vless.Server) &&
                reloaded.Vless.GetEffectiveServers().Count == 0,
                "A rejected tagged row must not return through the legacy scalar after reopening an empty list");
            return;
        }
        Assert(reloaded.App.SubscriptionUrl == url && reloaded.App.StrictDns && reloaded.Vless.Servers.Any(s => s.Server == manual.Server),
            "Refresh must preserve the legacy URL, manual profile and unrelated settings");
        if (scenario == "transient")
        {
            Assert(response.Error?.Code == "subscription_refresh_failed" && reopened.CurrentRevision == revision
                && before.SequenceEqual(File.ReadAllBytes(reopened.ConfigPath)), "A transient legacy failure must keep the exact saved file");
            Assert(reloaded.App.Subscriptions.Count == 0 && reloaded.App.SubscriptionServers.Single().Server == old.Server
                && reloaded.App.ConfigMode == "subscribe" && reloaded.App.ActiveSubscriptionServer == old.Name
                && reloaded.Vless.ActiveServer == old.Name && reloaded.Vless.Server == old.Server && reloaded.Vless.Uuid == old.Uuid,
                "The legacy cache, scalar and selection must survive a transient failure");
            return;
        }

        var feed = reloaded.App.Subscriptions.Single();
        Assert(feed.Enabled && feed.Url == url && reloaded.App.SubscriptionServers.Count == 0,
            "A definitive legacy refresh must save the registration and retire its old cache");
        if (scenario == "success")
        {
            Assert(response.Error == null && feed.Servers.Single().Name == "fresh" && feed.LastServerCount == 1
                && reloaded.App.ConfigMode == "subscribe" && reloaded.App.ActiveSubscriptionServer == "fresh"
                && reloaded.Vless.ActiveServer == "fresh" && VlessServersResolver.Resolve(reloaded).Single().Name == "fresh",
                "The fresh legacy result and subscription selection must survive a restart");
            body = RejectedBody;
            response = await DispatchRefreshAsync(dir, reopened.CurrentRevision);
            Assert(requests.SequenceEqual(new[] { url, url }), "The next refresh must fetch the same registered legacy URL");
            reloaded = new ConfigStorage(dir).GetSettings();
        }
        var rejected = reloaded.App.Subscriptions.Single();
        Assert(response.Error?.Code == "subscription_chain_unsupported" && rejected.Id == feed.Id && rejected.Url == url
            && rejected.Servers.Count == 0 && rejected.LastServerCount == 0 && reloaded.App.SubscriptionServers.Count == 0,
            "Legacy rejection must be visible and its historical feed cache must be empty after restart");
        Assert(reloaded.Vless.Servers.Single().Server == manual.Server && string.IsNullOrEmpty(reloaded.Vless.Server)
            && string.IsNullOrEmpty(reloaded.Vless.Uuid) && reloaded.App.StrictDns, "Superseded mirrors must not outlive the legacy cache");
        Assert(reloaded.App.ConfigMode == "generated" && reloaded.App.ActiveSubscriptionServer == string.Empty
            && reloaded.Vless.ActiveServer == manual.Name && VlessServersResolver.Resolve(reloaded).Single().Server == manual.Server,
            "Legacy rejection must select the surviving manual server");
    }

    private static async Task CheckStaleSameUrlResultAsync(string dir, bool rejectNewer)
    {
        var storage = new ConfigStorage(dir);
        var settings = storage.GetSettings();
        var first = Entry("first");
        var second = Entry("second");
        second.Url = first.Url;
        second.Servers[0].Server = "old-alias.invalid";
        settings.App.Subscriptions = [first, second];
        storage.SaveSettings(settings, storage.CurrentRevision);
        var revision = storage.CurrentRevision;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int requests = 0;
        SubscriptionFetcher.Http = new RefreshHttp(async (_, ct) =>
        {
            if (Interlocked.Increment(ref requests) != 1) return Reply(rejectNewer ? RejectedBody : FreshBody);
            started.SetResult();
            await release.Task.WaitAsync(ct);
            return Reply(rejectNewer ? FreshBody : RejectedBody);
        });

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var older = DispatchRefreshAsync(dir, revision, first.Id, timeout.Token);
        ProtocolResponseFrame late;
        string winningRevision;
        try
        {
            await started.Task.WaitAsync(timeout.Token);
            var newer = await DispatchRefreshAsync(dir, revision, second.Id, timeout.Token);
            Assert(rejectNewer ? newer.Error?.Code == "subscription_chain_unsupported" : newer.Error == null,
                "The later refresh must persist its result before the earlier request resumes");
            winningRevision = new ConfigStorage(dir).CurrentRevision;
        }
        finally
        {
            release.TrySetResult();
            late = await older;
        }
        Assert(requests == 2 && late.Error?.Code == "conflict", "The older response must lose the on-disk revision check");
        var reopened = new ConfigStorage(dir);
        var feeds = reopened.GetSettings().App.Subscriptions;
        Assert(reopened.CurrentRevision == winningRevision && feeds.Select(s => s.Id).SequenceEqual(new[] { first.Id, second.Id })
            && feeds.All(s => s.Url == first.Url), "Neither registration nor the committed revision may change when the stale result arrives");
        if (rejectNewer)
            Assert(feeds.All(s => s.Servers.Count == 0 && s.LastServerCount == 0),
                "After both responses the committed eviction must still cover both registrations on disk");
        else
        {
            var refreshed = feeds.Single(s => s.Id == second.Id);
            Assert(refreshed.Servers.Single().Name == "fresh" && refreshed.LastServerCount == 1,
                "A stale rejection must not erase the newer usable result");
        }
    }

    private static async Task<ProtocolResponseFrame> DispatchRefreshAsync(string dir, string revision, string? id = null,
        CancellationToken ct = default)
    {
        await using var handler = new RouterBackendHandler(new RouterBackend(new FakeRouterSession(), dir));
        var parameters = id == null ? JsonSerializer.SerializeToElement(new { revision })
            : JsonSerializer.SerializeToElement(new { revision, id });
        return await new ProtocolDispatcher(handler).DispatchAsync(new(1, "refresh", "subscriptions.refresh", parameters), ct);
    }

    private static HttpResponse Reply(string body, int status = 200) =>
        new(status, new Dictionary<string, string>(), Encoding.UTF8.GetBytes(body), TimeSpan.Zero);

    private static SubscriptionEntry Entry(string id) => new()
    {
        Id = id, Name = id, Url = "https://fixture.invalid/" + id, Enabled = true,
        Servers = new() { new VlessServerEntry { Name = "cached", Server = "192.0.2.1", Port = 443 } },
        LastServerCount = 1
    };

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class RefreshHttp(Func<HttpRequest, CancellationToken, Task<HttpResponse>> send) : IHttpClient
    {
        public Task<HttpResponse> SendAsync(HttpRequest request, CancellationToken ct = default) => send(request, ct);
        public Task<IHttpStreamingResponse> SendStreamingAsync(HttpRequest request, CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
