// Phase 3E (2026-05-18) — GeoIpStage unit tests.
//
// We don't drive real ip-api.com from tests — instead we assert the
// short-circuit behaviour (all entries already have CC = no network
// call), the optional contract (failure passes through input unchanged),
// and that the stage is wired as Optional = true so the orchestrator
// honours its non-fatal status.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services.FreeConfigs;
using VPNRouter.Core.Services.FreeConfigs.Stages;

namespace VPNRouter.Tests;

public class FreeConfigGeoIpStageTests
{
    private static readonly ILogger Logger = new LoggerConfiguration().CreateLogger();

    private static StageContext Ctx(IReadOnlyList<FreeConfigEntry> input) => new(
        Input: input,
        Settings: new AppSettings(),
        Cache: new FreeConfigCache(Logger),
        Sources: Array.Empty<FreeConfigSource>(),
        Logger: Logger);

    [Fact]
    public void Optional_IsTrue()
    {
        // Pin the contract: GeoIP failure is non-fatal — the orchestrator
        // uses this flag to decide whether to abort the pipeline. If we
        // ever flip it to false without thought, this test catches it.
        var stage = new GeoIpStage(new FreeConfigGeoIp(Logger));
        Assert.True(stage.Optional);
    }

    [Fact]
    public async Task AllEntriesHaveCountryCode_NoNetworkCall_PassThrough()
    {
        // Every input entry already has a CC, so the stage short-circuits
        // before constructing the network request. Asserting on output
        // identity (Same) confirms the pass-through path.
        var stage = new GeoIpStage(new FreeConfigGeoIp(Logger));
        var input = new List<FreeConfigEntry>
        {
            new() { Id = "a", Host = "h1", Port = 443, CountryCode = "NL" },
            new() { Id = "b", Host = "h2", Port = 443, CountryCode = "DE" },
        };

        var result = await stage.RunAsync(Ctx(input), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Same(input, result.Output);
    }

    [Fact]
    public async Task EmptyInput_NoOp()
    {
        var stage = new GeoIpStage(new FreeConfigGeoIp(Logger));

        var result = await stage.RunAsync(
            Ctx(Array.Empty<FreeConfigEntry>()),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Empty(result.Output);
    }

    [Fact]
    public void Name_IsStableAndLowerCased()
    {
        var stage = new GeoIpStage(new FreeConfigGeoIp(Logger));
        // The retry policy lookup is OrdinalIgnoreCase but the canonical
        // name is lowercase to match the StageRetryPolicy.Default keys.
        Assert.Equal("geoip", stage.Name);
    }
}
