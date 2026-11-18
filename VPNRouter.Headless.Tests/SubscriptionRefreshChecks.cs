using System.Text;
using System.Text.Json;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Headless.Protocol;
using VPNRouter.Headless.Storage;

namespace VPNRouter.Headless.Tests;

public static class SubscriptionRefreshChecks
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "vpnrouter-refresh-" + Guid.NewGuid().ToString("N"));
        var previousHttp = SubscriptionFetcher.Http;
        try
        {
            foreach (var scenario in new[] { "http", "transport", "empty", "invalid", "cancel", "partial", "success", "none" })
                await CheckAsync(Path.Combine(root, scenario), scenario);
            await CheckRejectedChainAsync(Path.Combine(root, "chain"), refreshAll: false);
            await CheckRejectedChainAsync(Path.Combine(root, "chain-batch"), refreshAll: true);
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
        const string validBody = "ss://aes-256-gcm:synthetic-password@192.0.2.8:443#fresh";
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
                _ => validBody
            };
            var status = scenario == "http" || (scenario == "partial" && request.Uri.AbsolutePath == "/second") ? 503 : 200;
            return Task.FromResult(new HttpResponse(status, new Dictionary<string, string>(),
                Encoding.UTF8.GetBytes(body), TimeSpan.Zero));
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

    private static async Task CheckRejectedChainAsync(string dir, bool refreshAll)
    {
        const string rejectedBody = """
            {"outbounds":[{"type":"vless","tag":"old-target","server":"exit.invalid","server_port":443,
            "uuid":"00000000-0000-4000-8000-000000000001","detour":"missing-private-hop"}]}
            """;
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
        other.Enabled = refreshAll;
        var manual = new VlessServerEntry { Name = "manual", Server = "manual.invalid" };
        var sameName = new VlessServerEntry { Name = "old-target", Server = "other-manual.invalid" };
        settings.App.Subscriptions = [other, rejected];
        settings.App.ConfigMode = "subscribe";
        settings.App.SubscriptionUrl = refreshAll ? other.Url : rejected.Url;
        settings.App.SubscriptionServers = refreshAll ? Entry("other").Servers : [Flattened()];
        settings.App.ActiveSubscriptionServer = settings.Vless.ActiveServer = "old-target";
        settings.Vless.Servers = [Flattened(), manual, sameName];
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
            var body = request.Uri.AbsolutePath == "/rejected" ? rejectedBody :
                "ss://aes-256-gcm:synthetic-password@192.0.2.8:443#staged";
            return Task.FromResult(new HttpResponse(200, new Dictionary<string, string>(),
                Encoding.UTF8.GetBytes(body), TimeSpan.Zero));
        });
        var revision = storage.CurrentRevision;
        var parameters = refreshAll ? JsonSerializer.SerializeToElement(new { revision })
            : JsonSerializer.SerializeToElement(new { revision, id = rejected.Id });
        await using var backend = new RouterBackend(new FakeRouterSession(), dir);
        await using var handler = new RouterBackendHandler(backend);
        var dispatcher = new ProtocolDispatcher(handler);
        var response = await dispatcher.DispatchAsync(new ProtocolRequest(1, "chain-refresh", "subscriptions.refresh", parameters),
            CancellationToken.None);
        using var frame = JsonDocument.Parse(response.ToUtf8Bytes());
        var error = frame.RootElement.GetProperty("error");
        Assert(error.GetProperty("code").GetString() == "subscription_chain_unsupported", "Expected an unsupported-chain error");
        Assert(error.GetProperty("message").GetString() == "The subscription uses an unsupported proxy chain. Its cached servers were removed.",
            "The rejection must explain eviction without exposing provider data");
        Assert(requests.SequenceEqual(refreshAll ? new[] { "/other", "/rejected" } : new[] { "/rejected" }),
            "The fixture must reach the rejected feed through the requested refresh path");

        var reloaded = new ConfigStorage(dir).GetSettings();
        var purged = reloaded.App.Subscriptions.Single(s => s.Id == rejected.Id);
        var kept = reloaded.App.Subscriptions.Single(s => s.Id == other.Id);
        Assert(purged.Servers.Count == 0 && purged.LastServerCount == 0 && purged.Enabled && purged.Url == rejected.Url,
            "Reload must retain the subscription registration and evict its historical rows");
        var legacy = reloaded.App.SubscriptionServers;
        Assert(refreshAll ? legacy.Single().Server == other.Servers.Single().Server : legacy.Count == 0,
            "Reload must keep a different feed's legacy cache and evict the rejected feed's mirror");
        Assert(reloaded.Vless.Servers.Select(s => s.Server).SequenceEqual(new[] { manual.Server, sameName.Server }),
            "Only the flattened copy should be evicted, even if another manual row shares its display name");
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
        Assert(resolved.Any(s => s.Name == active) && resolved.All(s => s.Server != "exit.invalid"),
            "Offline selection after restart must not return the historical direct target");
    }

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
