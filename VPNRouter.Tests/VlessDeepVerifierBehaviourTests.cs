#nullable enable
// ============================================================================
// VlessDeepVerifierBehaviourTests.cs — Phase 2G sub-wave 7c-1 (2026-05-18)
// ============================================================================
//
// Companion file to <see cref="VlessDeepVerifierTests"/> — pins the runtime
// behaviour of `VlessDeepVerifier.VerifyAsync` / `VerifyBatchAsync` for the
// gate / fallback paths that don't require spawning a real sing-box:
//
//   * Placeholder-credential guard (v2.32.3 regression, Z:\kanareik incident)
//   * "sing-box binary missing" fallback (CI / Mac / Linux build envs)
//   * Cancellation surface
//
// Split out of VlessDeepVerifierTests.cs to stay under the per-file 300-LOC
// gate from plans/phase2-2G-untested-services-2026-05-17.md. See the lead
// file's class-level doc for full scope rationale.
//
// Brief: plans/phase2-2G-untested-services-2026-05-17.md (sub-wave 7c-1).
// ============================================================================

using Serilog;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

public sealed class VlessDeepVerifierBehaviourTests
{
    // Stas-class placeholder fingerprints. Mirrors PlaceholderGuardTests so
    // any change to the canonical list shows up in both regression suites.
    private const string PlaceholderPubkey = "DnT9hIvt5QEx07unHUeXbWxN4Qo1gnecN4p0s62nckU";
    private const string PlaceholderShortId = "78ca7952";
    private const string PlaceholderServer = "195.135.255.216";

    // Deterministic non-existent path so `IsAvailable` is false in every
    // test here — production resolves to AppPaths.SingBoxExePath which may
    // or may not be installed on the test machine.
    private const string NoBinaryPath = @"C:\definitely-not-here\sing-box.exe";

    private static ILogger SilentLogger() => new LoggerConfiguration().CreateLogger();

    private static VlessServerEntry CleanVlessEntry() =>
        VlessDeepVerifierTests.CleanVlessEntry();

    // ─── Placeholder credential gate (security regression, v2.32.3) ──────

    [Fact]
    public async Task VerifyAsync_PlaceholderPubkey_RefusesToProbe()
    {
        // v2.32.3 regression prevention (Z:\kanareik / stas-class incident).
        // A subscription / paste that smuggled the DnT9... pubkey past
        // upstream input gates must NOT reach the spawn path here — even
        // if the host happens to be reachable on TCP/443, the Reality
        // handshake never completes for those creds, but a buggy verifier
        // could time out late and return a misleading "passed" verdict.
        //
        // The placeholder gate at the top of VerifyAsync short-circuits
        // before any process spawn. We verify (a) Verified=false,
        // (b) reason contains "placeholder", (c) the error names the
        // exact offending field for log clarity.
        var entry = CleanVlessEntry();
        entry.Reality.PublicKey = PlaceholderPubkey;

        var verifier = new VlessDeepVerifier(SilentLogger(), NoBinaryPath);
        var result = await verifier.VerifyAsync(entry, measureBandwidth: false);

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
        var result = await verifier.VerifyAsync(entry, measureBandwidth: false);

        Assert.False(result.Ok);
        Assert.NotNull(result.Error);
        Assert.Contains("placeholder", result.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("reality.short_id", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task VerifyAsync_PlaceholderServerIp_RefusesToProbe()
    {
        // khunrath_ln stas-evidence case: pubkey + short_id both clean,
        // only the server IP matches the placeholder list. Same outcome
        // — refuse to probe.
        var entry = CleanVlessEntry();
        entry.Server = PlaceholderServer;

        var verifier = new VlessDeepVerifier(SilentLogger(), NoBinaryPath);
        var result = await verifier.VerifyAsync(entry, measureBandwidth: false);

        Assert.False(result.Ok);
        Assert.NotNull(result.Error);
        Assert.Contains("placeholder", result.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("server", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task VerifyAsync_PlaceholderGateRunsBeforeBinaryCheck()
    {
        // Order matters: the placeholder gate must fire FIRST so that the
        // logs / error surface stay honest even on test rigs where the
        // sing-box binary is missing. If the order ever reversed, users
        // would see "sing-box missing" for a placeholder config and
        // chase the wrong root cause.
        var entry = CleanVlessEntry();
        entry.Reality.PublicKey = PlaceholderPubkey; // placeholder

        var verifier = new VlessDeepVerifier(SilentLogger(), NoBinaryPath); // no binary
        var result = await verifier.VerifyAsync(entry, measureBandwidth: false);

        Assert.False(result.Ok);
        Assert.Contains("placeholder", result.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("missing", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    // ─── Binary-missing fallback ──────────────────────────────────────────

    [Fact]
    public async Task VerifyAsync_SingBoxBinaryMissing_ReturnsCleanFailure()
    {
        // CI / Mac-Linux env may not have sing-box installed at the path
        // AppPaths.SingBoxExePath resolves to. The verifier must fail
        // gracefully with a descriptive error rather than throw / orphan
        // a process. Pin the surface — a future "let's spawn anyway"
        // refactor would break this contract silently.
        var verifier = new VlessDeepVerifier(SilentLogger(), NoBinaryPath);
        Assert.False(verifier.IsAvailable);

        var entry = CleanVlessEntry();
        var result = await verifier.VerifyAsync(entry, measureBandwidth: false);

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
        // Batch path has its own "binary missing" short-circuit that
        // doesn't loop through VerifyAsync — pin that path too.
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
            measureBandwidth: false);

        Assert.Equal(entries.Length, results.Count);
        Assert.All(results, pair =>
        {
            Assert.False(pair.Result.Ok);
            Assert.Equal("sing-box binary missing", pair.Result.Error);
        });
    }

    // ─── Cancellation surface ────────────────────────────────────────────

    [Fact]
    public async Task VerifyAsync_PreCancelledToken_ShortCircuitsWithoutSpawn()
    {
        // Cancellation passed in already-cancelled state should surface
        // either (a) "binary missing" (gate hits first when no binary is
        // present) or (b) "cancelled" — but it must NOT throw. Either is
        // acceptable behaviour; what we pin here is "graceful, no orphan".
        var verifier = new VlessDeepVerifier(SilentLogger(), NoBinaryPath);
        var entry = CleanVlessEntry();

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // The IsAvailable+placeholder gates execute synchronously before
        // any token check, so a pre-cancelled token still produces a
        // clean DeepVerifyResult.Failed value rather than throwing
        // OperationCanceledException.
        var result = await verifier.VerifyAsync(entry, measureBandwidth: false, cts.Token);

        Assert.False(result.Ok);
        Assert.NotNull(result.Error);
    }
}
