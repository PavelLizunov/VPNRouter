using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// SEC-1: subscription URLs embed provider tokens in path/query; every {Url}
/// log argument in SubscriptionFetcher must go through CanaryPolicy.RedactUrl.
/// Pure redaction shape is pinned by CanaryPolicyTests — not duplicated here.
/// </summary>
public sealed class SubscriptionUrlRedactionTests
{
    private const string SubUrl = "https://provider.example/api/sub?token=secret123";
    private const string RedactedHost = "https://provider.example";

    private const string CleanVless =
        "vless://uuid1@server1.example:443?security=tls&type=tcp&flow=xtls-rprx-vision#one";

    // Placeholder-bait pubkey -> 0 servers -> fires both RefreshEntryAsync warning sites.
    private const string PlaceholderPubkey =
        "DnT9hIvt5QEx07unHUeXbWxN4Qo1gnecN4p0s62nckU";

    private static string PlaceholderVless =>
        "vless://uuid-bad-1@bad1.example:443?security=reality&sni=yahoo.com&fp=firefox" +
        $"&pbk={PlaceholderPubkey}&sid=78ca7952&spx=/&type=tcp&flow=xtls-rprx-vision&encryption=none#bad1";

    [Fact]
    public async Task FetchAsync_LogsDoNotContainToken()
    {
        var (logger, sink) = BuildCapturingLogger();
        var fake = new FakeHttpClient().Setup("provider.example", $"{CleanVless}\n");
        var previous = SubscriptionFetcher.Http;
        SubscriptionFetcher.Http = fake;
        try
        {
            var servers = await SubscriptionFetcher.FetchAsync(SubUrl, logger);

            Assert.Single(servers);
            var all = AllRenderedText(sink);

            Assert.DoesNotContain("secret123", all);
            Assert.DoesNotContain("/api/sub", all);
            Assert.DoesNotContain(SubUrl, all);
            Assert.Contains(RedactedHost, all);
        }
        finally
        {
            SubscriptionFetcher.Http = previous;
        }
    }

    [Fact]
    public async Task RefreshEntryAsync_LogsDoNotContainToken()
    {
        var (logger, sink) = BuildCapturingLogger();
        var fake = new FakeHttpClient().Setup("provider.example", $"{PlaceholderVless}\n");
        var previous = SubscriptionFetcher.Http;
        SubscriptionFetcher.Http = fake;
        try
        {
            var entry = new SubscriptionEntry { Url = SubUrl };
            var count = await SubscriptionFetcher.RefreshEntryAsync(entry, logger);

            Assert.Equal(0, count);
            var all = AllRenderedText(sink);

            Assert.DoesNotContain("secret123", all);
            Assert.DoesNotContain("/api/sub", all);
            Assert.DoesNotContain(SubUrl, all);
            Assert.Contains(RedactedHost, all);
        }
        finally
        {
            SubscriptionFetcher.Http = previous;
        }
    }

    private static (ILogger logger, CapturingSink sink) BuildCapturingLogger()
    {
        var sink = new CapturingSink();
        var logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(sink)
            .CreateLogger();
        return (logger, sink);
    }

    private static string AllRenderedText(CapturingSink sink) =>
        string.Join("\n", sink.Events.Select(e => e.RenderMessage()));

    private sealed class CapturingSink : ILogEventSink
    {
        private readonly List<LogEvent> _events = new();
        private readonly object _gate = new();

        public void Emit(LogEvent logEvent)
        {
            lock (_gate) _events.Add(logEvent);
        }

        public IReadOnlyList<LogEvent> Events
        {
            get { lock (_gate) return _events.ToList(); }
        }
    }
}
