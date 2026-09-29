#nullable enable

using Serilog;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

public sealed class VlessDeepVerifierBehaviourTests
{
    private const string PlaceholderPubkey = "DnT9hIvt5QEx07unHUeXbWxN4Qo1gnecN4p0s62nckU";
    private const string PlaceholderShortId = "78ca7952";
    private const string PlaceholderServer = "195.135.255.216";

    private const string NoBinaryPath = @"C:\definitely-not-here\sing-box.exe";

    private static ILogger SilentLogger() => new LoggerConfiguration().CreateLogger();

    private static VlessServerEntry CleanVlessEntry() =>
        VlessDeepVerifierTests.CleanVlessEntry();

    [Fact]
    public async Task VerifyAsync_PlaceholderPubkey_RefusesToProbe()
    {
        var entry = CleanVlessEntry();
        entry.Reality.PublicKey = PlaceholderPubkey;

        var verifier = new VlessDeepVerifier(SilentLogger(), NoBinaryPath);
        var result = await verifier.VerifyAsync(entry, measureBandwidth: false, TestContext.Current.CancellationToken);

        Assert.False(result.Ok);
        Assert.NotNull(result.Error);
        Assert.Contains("placeholder", result.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("reality.public_key", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task VerifyAsync_PlaceholderShortId_RefusesToProbe()
    {
        var entry = CleanVlessEntry();
        entry.Reality.ShortId = PlaceholderShortId;

        var verifier = new VlessDeepVerifier(SilentLogger(), NoBinaryPath);
        var result = await verifier.VerifyAsync(entry, measureBandwidth: false, TestContext.Current.CancellationToken);

        Assert.False(result.Ok);
        Assert.NotNull(result.Error);
        Assert.Contains("placeholder", result.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("reality.short_id", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task VerifyAsync_PlaceholderServerIp_RefusesToProbe()
    {
        var entry = CleanVlessEntry();
        entry.Server = PlaceholderServer;

        var verifier = new VlessDeepVerifier(SilentLogger(), NoBinaryPath);
        var result = await verifier.VerifyAsync(entry, measureBandwidth: false, TestContext.Current.CancellationToken);

        Assert.False(result.Ok);
        Assert.NotNull(result.Error);
        Assert.Contains("placeholder", result.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("server", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task VerifyAsync_PlaceholderGateRunsBeforeBinaryCheck()
    {
        var entry = CleanVlessEntry();
        entry.Reality.PublicKey = PlaceholderPubkey;

        var verifier = new VlessDeepVerifier(SilentLogger(), NoBinaryPath);
        var result = await verifier.VerifyAsync(entry, measureBandwidth: false, TestContext.Current.CancellationToken);

        Assert.False(result.Ok);
        Assert.Contains("placeholder", result.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("missing", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task VerifyAsync_SingBoxBinaryMissing_ReturnsCleanFailure()
    {
        var verifier = new VlessDeepVerifier(SilentLogger(), NoBinaryPath);
        Assert.False(verifier.IsAvailable);

        var entry = CleanVlessEntry();
        var result = await verifier.VerifyAsync(entry, measureBandwidth: false, TestContext.Current.CancellationToken);

        Assert.False(result.Ok);
        Assert.NotNull(result.Error);
        Assert.Contains("sing-box", result.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("missing", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, result.HttpLatencyMs);
        Assert.Null(result.BandwidthMbps);
    }

    [Fact]
    public async Task VerifyBatchAsync_BinaryMissing_MarksEveryEntryFailed()
    {
        var verifier = new VlessDeepVerifier(SilentLogger(), NoBinaryPath);

        var entries = new[]
        {
            CleanVlessEntry(),
            new VlessServerEntry
            {
                Name = "second",
                Protocol = "vless",
                Server = "second.example.com",
                Port = 443,
                Uuid = "another-uuid",
            },
            new VlessServerEntry
            {
                Name = "third-hy2",
                Protocol = "hysteria2",
                Server = "h2.example.com",
                Port = 443,
                Password = "p",
            },
        };

        var results = new List<(VlessServerEntry Entry, DeepVerifyResult Result)>();
        await verifier.VerifyBatchAsync(
            entries,
            (e, r) => { lock (results) results.Add((e, r)); },
            measureBandwidth: false,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(entries.Length, results.Count);
        Assert.All(results, pair =>
        {
            Assert.False(pair.Result.Ok);
            Assert.Equal("sing-box binary missing", pair.Result.Error);
        });
    }

    [Fact]
    public async Task VerifyAsync_PreCancelledToken_ShortCircuitsWithoutSpawn()
    {
        var verifier = new VlessDeepVerifier(SilentLogger(), NoBinaryPath);
        var entry = CleanVlessEntry();

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await verifier.VerifyAsync(entry, measureBandwidth: false, cts.Token);

        Assert.False(result.Ok);
        Assert.NotNull(result.Error);
    }

    private static VlessServerEntry DnsTunnelEntry() => new()
    {
        Name = "dns-tunnel-test",
        Protocol = "dns-tunnel",
        Server = "tunnel.example.com",
        Port = 443,
        Uuid = "abcd1234-5678-90ab-cdef-1234567890ab",
        DnsDomain = "tunnel.example.com",
        DnsResolvers = new List<string> { "195.208.4.1:53" },
    };

    [Fact]
    public async Task DnsTunnelEntry_UnsupportedByVerifier_MapsToSkipped()
    {
        var verifier = new VlessDeepVerifier(SilentLogger(), NoBinaryPath);
        var result = await verifier.VerifyAsync(DnsTunnelEntry(), measureBandwidth: false, TestContext.Current.CancellationToken);

        Assert.False(result.Ok);
        Assert.Equal(DeepVerifyFailurePhase.UnsupportedByVerifier, result.FailurePhase);
        Assert.Contains("dns-tunnel", result.Error!, StringComparison.OrdinalIgnoreCase);

        var phases = ServerHealthPhaseMapper.FromDeepVerify(result);
        Assert.Equal(PhaseOutcome.Skipped, phases.ProxiedHttpControl);
    }
}
