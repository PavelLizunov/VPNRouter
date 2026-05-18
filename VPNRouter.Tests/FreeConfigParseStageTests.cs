// Phase 3E (2026-05-18) — ParseStage unit tests.
//
// Each test seeds FetchStage.PendingFetches with raw URI buckets the way
// FetchStage's per-source path would, then runs ParseStage on a stub
// context and asserts the output FreeConfigEntry list shape.

using System;
using System.Collections.Generic;
using System.Threading;
using Serilog;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services.FreeConfigs;
using VPNRouter.Core.Services.FreeConfigs.Stages;

namespace VPNRouter.Tests;

public class FreeConfigParseStageTests
{
    private static readonly ILogger Logger = new LoggerConfiguration().CreateLogger();

    private static StageContext MakeContext() =>
        new(
            Input: Array.Empty<FreeConfigEntry>(),
            Settings: new AppSettings(),
            Cache: new FreeConfigCache(Logger),
            Sources: Array.Empty<FreeConfigSource>(),
            Logger: Logger);

    [Fact]
    public async System.Threading.Tasks.Task EmptyFetches_ReturnsZeroEntries()
    {
        // Use FetchStage purely as a PendingFetches bucket holder — the
        // poolFetcher/fetcher refs aren't called by ParseStage.
        var fetch = new FetchStage(
            new FreeConfigFetcher(Logger),
            new FreeConfigPoolFetcher(Logger));
        var parse = new ParseStage(fetch);

        var result = await parse.RunAsync(MakeContext(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Empty(result.Output);
    }

    [Fact]
    public async System.Threading.Tasks.Task ValidUris_ProduceFreeConfigEntries()
    {
        var fetch = new FetchStage(
            new FreeConfigFetcher(Logger),
            new FreeConfigPoolFetcher(Logger));
        var src = new FreeConfigSource { Name = "test", Url = "https://example.com/sub", Enabled = true };
        // Use NON-placeholder pubkeys — VlessUriParser.Parse throws
        // PlaceholderConfigException for known fingerprints (Phase 3D
        // input gate). We're testing parse plumbing here, not the
        // placeholder reject path.
        fetch.PendingFetches[src] = new List<string>
        {
            "vless://550e8400-e29b-41d4-a716-446655440000@example.com:443?security=reality&sni=google.com&fp=chrome&pbk=NonPlaceholderPubkey00000000000000000000001&sid=ab012345#node1",
            "vless://550e8400-e29b-41d4-a716-446655440000@example.org:443?security=reality&sni=google.com&fp=chrome&pbk=NonPlaceholderPubkey00000000000000000000002&sid=cd012345#node2",
        };
        var parse = new ParseStage(fetch);

        var result = await parse.RunAsync(MakeContext(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(2, result.Output.Count);
        Assert.Contains(result.Output, e => e.Host == "example.com" && e.Port == 443);
        Assert.Contains(result.Output, e => e.Host == "example.org" && e.Port == 443);
    }

    [Fact]
    public async System.Threading.Tasks.Task InvalidUris_AreCountedButDontAbort()
    {
        var fetch = new FetchStage(
            new FreeConfigFetcher(Logger),
            new FreeConfigPoolFetcher(Logger));
        var src = new FreeConfigSource { Name = "test", Url = "https://example.com/sub", Enabled = true };
        fetch.PendingFetches[src] = new List<string>
        {
            "vless://550e8400-e29b-41d4-a716-446655440000@example.com:443?security=reality&sni=google.com&fp=chrome&pbk=AnotherPubkey0000000000000000000000000000000&sid=99cd2233#node-good",
            "not-a-valid-uri",
            "vless://garbage",
        };
        var parse = new ParseStage(fetch);

        var result = await parse.RunAsync(MakeContext(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Single(result.Output);
        Assert.NotNull(result.FailureReason); // "2 parse errors"
        Assert.Contains("parse errors", result.FailureReason);
    }

    [Fact]
    public async System.Threading.Tasks.Task DuplicateUrisWithinSource_AreDeduped()
    {
        var fetch = new FetchStage(
            new FreeConfigFetcher(Logger),
            new FreeConfigPoolFetcher(Logger));
        var src = new FreeConfigSource { Name = "test", Url = "https://example.com/sub", Enabled = true };
        var uri = "vless://550e8400-e29b-41d4-a716-446655440000@example.com:443?security=reality&sni=google.com&fp=chrome&pbk=Pubkey0000000000000000000000000000000000000&sid=99cd2233#node";
        fetch.PendingFetches[src] = new List<string> { uri, uri, uri };
        var parse = new ParseStage(fetch);

        var result = await parse.RunAsync(MakeContext(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Single(result.Output);
    }

    [Fact]
    public async System.Threading.Tasks.Task PendingFetches_AreClearedAfterRun()
    {
        var fetch = new FetchStage(
            new FreeConfigFetcher(Logger),
            new FreeConfigPoolFetcher(Logger));
        var src = new FreeConfigSource { Name = "test", Url = "https://example.com/sub", Enabled = true };
        fetch.PendingFetches[src] = new List<string>
        {
            "vless://550e8400-e29b-41d4-a716-446655440000@example.com:443?security=reality&sni=g.com&fp=chrome&pbk=P0000000000000000000000000000000000000000000&sid=99cd2233#node",
        };
        var parse = new ParseStage(fetch);

        await parse.RunAsync(MakeContext(), CancellationToken.None);

        // ParseStage drains PendingFetches so a follow-up run doesn't
        // duplicate the work — important for the orchestrator's retry path.
        Assert.Empty(fetch.PendingFetches);
    }
}
