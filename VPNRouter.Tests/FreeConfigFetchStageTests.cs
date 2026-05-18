// Phase 3E (2026-05-18) — FetchStage unit tests.
//
// FetchStage owns the HTTP I/O so its tests focus on the contract glue
// (Name, Optional, short-circuit signal shape) rather than real network
// requests. The pool short-circuit path with mocked HTTP is exercised by
// the orchestrator-level test that doesn't enable the pool.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services.FreeConfigs;
using VPNRouter.Core.Services.FreeConfigs.Stages;

namespace VPNRouter.Tests;

public class FreeConfigFetchStageTests
{
    private static readonly ILogger Logger = new LoggerConfiguration().CreateLogger();

    private static StageContext Ctx(IReadOnlyList<FreeConfigSource> sources) => new(
        Input: Array.Empty<FreeConfigEntry>(),
        Settings: new AppSettings(),
        Cache: new FreeConfigCache(Logger),
        Sources: sources,
        Logger: Logger);

    [Fact]
    public void Name_IsFetch()
    {
        var stage = new FetchStage(
            new FreeConfigFetcher(Logger),
            new FreeConfigPoolFetcher(Logger));
        Assert.Equal("fetch", stage.Name);
    }

    [Fact]
    public void Optional_IsFalse()
    {
        // FetchStage MUST be mandatory — without it the pipeline has no
        // raw input to operate on. The orchestrator aborts when a non-
        // optional stage returns Success = false.
        var stage = new FetchStage(
            new FreeConfigFetcher(Logger),
            new FreeConfigPoolFetcher(Logger));
        Assert.False(stage.Optional);
    }

    [Fact]
    public async Task NoEnabledSources_PoolDisabled_ReturnsEmpty()
    {
        // No pool + no sources → empty output, no short-circuit. The
        // orchestrator continues to the next stage with no work to do.
        var stage = new FetchStage(
            new FreeConfigFetcher(Logger),
            new FreeConfigPoolFetcher(Logger),
            useServerPool: false);

        var result = await stage.RunAsync(
            Ctx(Array.Empty<FreeConfigSource>()),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Empty(result.Output);
        Assert.False(result.ShortCircuit);
    }

    [Fact]
    public void PendingFetches_StartsEmpty()
    {
        // The bucket ParseStage consumes is per-instance and starts empty
        // — re-running RunAsync clears it at the top so test stability
        // doesn't depend on cross-test order.
        var stage = new FetchStage(
            new FreeConfigFetcher(Logger),
            new FreeConfigPoolFetcher(Logger),
            useServerPool: false);

        Assert.Empty(stage.PendingFetches);
    }

    [Fact]
    public async Task DisabledSources_AreFiltered()
    {
        // Source.Enabled = false should be honoured by the fetch stage —
        // disabled entries never appear in PendingFetches even when the
        // pool path is bypassed.
        var stage = new FetchStage(
            new FreeConfigFetcher(Logger),
            new FreeConfigPoolFetcher(Logger),
            useServerPool: false);
        var sources = new List<FreeConfigSource>
        {
            new() { Name = "off", Url = "https://example.com/disabled", Enabled = false },
        };

        var result = await stage.RunAsync(Ctx(sources), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Empty(stage.PendingFetches);
    }
}
