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
            Console.WriteLine("[SubscriptionRefreshChecks] Eight offline wire/error and persistence scenarios passed");
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
        const string validBody = "trojan://synthetic-password@192.0.2.8:443#fresh";
        var storage = new ConfigStorage(dir);
        var settings = storage.GetSettings();
        settings.App.Subscriptions = scenario == "none" ? new() : new() { Entry("first") };
        if (scenario == "partial") settings.App.Subscriptions.Add(Entry("second"));
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
            Assert(wire.TryGetProperty("result", out _), "Valid refresh or no targets must succeed");
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
        }
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
