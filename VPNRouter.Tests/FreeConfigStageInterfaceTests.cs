// Phase 3E (2026-05-18) — IFreeConfigStage / StageRetryPolicy contract tests.
//
// Pin the interface invariants we rely on in FreeConfigAggregator's stage
// loop: name + Optional contract, StageRetryPolicy lookup with default
// fallback, and the canonical "Default" policy carrying the historic
// fetch-retries-twice / everything-else-once setting.

using System;
using System.Collections.Generic;
using VPNRouter.Core.Services.FreeConfigs;

namespace VPNRouter.Tests;

public class FreeConfigStageInterfaceTests
{
    [Fact]
    public void StageRetryPolicy_For_UnknownStage_ReturnsDefault()
    {
        var policy = new StageRetryPolicy(
            overrides: new Dictionary<string, StageRetry> { ["fetch"] = new StageRetry(Count: 7) },
            @default: new StageRetry(Count: 1, BaseDelayMs: 100));

        var result = policy.For("non-existent-stage");

        Assert.Equal(1, result.Count);
        Assert.Equal(100, result.BaseDelayMs);
    }

    [Fact]
    public void StageRetryPolicy_For_KnownStage_ReturnsOverride()
    {
        var policy = new StageRetryPolicy(
            overrides: new Dictionary<string, StageRetry> { ["fetch"] = new StageRetry(Count: 7, BaseDelayMs: 500) });

        var result = policy.For("fetch");

        Assert.Equal(7, result.Count);
        Assert.Equal(500, result.BaseDelayMs);
    }

    [Fact]
    public void StageRetryPolicy_Default_FetchHasTwoAttempts()
    {
        var policy = StageRetryPolicy.Default;

        var fetch = policy.For("fetch");

        Assert.Equal(2, fetch.Count);
        Assert.True(fetch.BaseDelayMs > 0, "Fetch should back off between retries.");
    }

    [Fact]
    public void StageRetryPolicy_Default_NonFetchStagesRunOnce()
    {
        var policy = StageRetryPolicy.Default;

        // The internal retry logic inside parse/dedupe/geoip/test/cache-merge
        // owns transient retries (TCP timeouts inside the tester, HTTP timeouts
        // inside fetcher), so the stage-level wrapper runs once.
        Assert.Equal(1, policy.For("parse").Count);
        Assert.Equal(1, policy.For("dedupe").Count);
        Assert.Equal(1, policy.For("geoip").Count);
        Assert.Equal(1, policy.For("test").Count);
        Assert.Equal(1, policy.For("cache-merge").Count);
    }

    [Fact]
    public void StageRetryPolicy_For_IsCaseInsensitive()
    {
        var policy = new StageRetryPolicy(
            overrides: new Dictionary<string, StageRetry>(StringComparer.OrdinalIgnoreCase)
            {
                ["FETCH"] = new StageRetry(Count: 9),
            });

        // The lookup must be case-insensitive so stage Name strings can use
        // any casing convention without colliding with the registered key.
        Assert.Equal(9, policy.For("fetch").Count);
        Assert.Equal(9, policy.For("Fetch").Count);
        Assert.Equal(9, policy.For("FETCH").Count);
    }

    [Fact]
    public void StageContext_RecordWith_DoesNotMutateInput()
    {
        // The orchestrator threads context forward via `ctx = ctx with { Input = … }`.
        // This pins that records produce a new instance per the C# record
        // contract — we never accidentally mutate the input list across
        // stages.
        var input = new List<VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry>
        {
            new() { Id = "abc" },
        };
        var ctx = new StageContext(
            Input: input,
            Settings: new VPNRouter.Core.Models.AppSettings(),
            Cache: new VPNRouter.Core.Services.FreeConfigs.FreeConfigCache(
                new Serilog.LoggerConfiguration().CreateLogger()),
            Sources: Array.Empty<VPNRouter.Core.Services.FreeConfigs.FreeConfigSource>(),
            Logger: new Serilog.LoggerConfiguration().CreateLogger());

        var freshInput = new List<VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry>();
        var next = ctx with { Input = freshInput };

        Assert.NotSame(ctx, next);
        Assert.Same(input, ctx.Input);
        Assert.Same(freshInput, next.Input);
    }
}
