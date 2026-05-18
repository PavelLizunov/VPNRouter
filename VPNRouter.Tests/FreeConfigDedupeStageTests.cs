// Phase 3E (2026-05-18) — DedupeStage unit tests.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services.FreeConfigs;
using VPNRouter.Core.Services.FreeConfigs.Stages;

namespace VPNRouter.Tests;

public class FreeConfigDedupeStageTests
{
    private static readonly ILogger Logger = new LoggerConfiguration().CreateLogger();

    private static StageContext Ctx(IReadOnlyList<FreeConfigEntry> input) => new(
        Input: input,
        Settings: new AppSettings(),
        Cache: new FreeConfigCache(Logger),
        Sources: Array.Empty<FreeConfigSource>(),
        Logger: Logger);

    private static FreeConfigEntry MakeEntry(string id, string host = "h") => new()
    {
        Id = id,
        Host = host,
        Port = 443,
        Uuid = "u",
    };

    [Fact]
    public async Task DistinctIds_AllSurvive()
    {
        var stage = new DedupeStage();
        var input = new List<FreeConfigEntry>
        {
            MakeEntry("aaa1"),
            MakeEntry("bbb2"),
            MakeEntry("ccc3"),
        };

        var result = await stage.RunAsync(Ctx(input), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(3, result.Output.Count);
    }

    [Fact]
    public async Task DuplicateIds_FirstWins()
    {
        var stage = new DedupeStage();
        var first = MakeEntry("dup", "first.example.com");
        var second = MakeEntry("dup", "second.example.com");
        var input = new List<FreeConfigEntry> { first, second };

        var result = await stage.RunAsync(Ctx(input), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Single(result.Output);
        Assert.Same(first, result.Output[0]); // first occurrence wins
    }

    [Fact]
    public async Task EmptyId_IsSkipped()
    {
        var stage = new DedupeStage();
        var input = new List<FreeConfigEntry>
        {
            new() { Id = "", Host = "no-id" },
            MakeEntry("real"),
        };

        var result = await stage.RunAsync(Ctx(input), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Single(result.Output);
        Assert.Equal("real", result.Output[0].Id);
    }

    [Fact]
    public async Task CaseInsensitiveIdMatching_DropsDuplicates()
    {
        // The orchestrator hashes host+uuid before storing IDs, so cased
        // duplicates rarely occur — but the dedupe dictionary uses
        // OrdinalIgnoreCase so a manually-injected cased duplicate still
        // dedupes.
        var stage = new DedupeStage();
        var input = new List<FreeConfigEntry>
        {
            MakeEntry("ABCDEF12", "first"),
            MakeEntry("abcdef12", "second"),
        };

        var result = await stage.RunAsync(Ctx(input), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Single(result.Output);
    }

    [Fact]
    public async Task EmptyInput_ProducesEmptyOutput()
    {
        var stage = new DedupeStage();

        var result = await stage.RunAsync(
            Ctx(Array.Empty<FreeConfigEntry>()),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Empty(result.Output);
    }
}
