using System.Text.Json;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;

namespace VPNRouter.Tests;

[Collection(SafeModeStateCollection.Name)]
public sealed class BusinessAutoSelectionTests
{
    private static VlessServerEntry Entry(string name, string host = "192.0.2.1", int port = 443) => new()
    {
        Name = name,
        Protocol = "vless",
        Server = host,
        Port = port,
        Uuid = $"{name}-uuid",
        Security = "tls",
        Tls = new VlessTlsConfig { Enabled = true, ServerName = host },
    };

    private static DeepVerifyResult Ok(int latencyMs) => new(true, latencyMs, null, null);

    private static DeepVerifyResult Failed(DeepVerifyFailurePhase phase, string error = "synthetic") =>
        new(false, 0, null, error, phase);

    [Fact]
    public void BusinessProbeUsesTheSameHttpUpgradeAndAlpnAsTheSession()
    {
        var entry = VlessDeepVerifierTests.CleanVlessEntry();
        entry.Security = "tls";
        entry.Flow = "";
        entry.Tls = new VlessTlsConfig { Enabled = true, ServerName = "office.example", Alpn = "h2,http/1.1" };
        entry.Transport = new VlessTransportConfig { Type = "httpupgrade", Path = "/upgrade" };

        using var probe = JsonDocument.Parse(VlessDeepVerifier.BuildSingleOutboundConfig(
            entry, new VerificationProxy(18181), useConfiguredOutbound: true));
        var actual = probe.RootElement.GetProperty("outbounds")[0];
        var reference = JsonSerializer.SerializeToElement(ConfigGenerator.BuildVlessOutbound(entry, "proxy"),
            VPNRouter.Core.Json.AppJsonContext.Default.SingBoxOutbound);
        Assert.Equal("httpupgrade", actual.GetProperty("transport").GetProperty("type").GetString());
        Assert.Equal("/upgrade", actual.GetProperty("transport").GetProperty("path").GetString());
        Assert.Equal(2, actual.GetProperty("tls").GetProperty("alpn").GetArrayLength());
        Assert.True(JsonElement.DeepEquals(reference.GetProperty("transport"), actual.GetProperty("transport")));
        Assert.True(JsonElement.DeepEquals(reference.GetProperty("tls"), actual.GetProperty("tls")));
        Assert.Equal("dns-google", actual.GetProperty("domain_resolver").GetString());
    }

    [Fact]
    public async Task SelectFastest_PicksLowestLatencySuccessfulCandidate()
    {
        var slow = Entry("slow");
        var fast = Entry("fast");
        var outcome = await BusinessServerAutoSelection.SelectFastestAsync(
            [slow, fast],
            (entry, _) => Task.FromResult(entry == slow ? Ok(220) : Ok(35)),
            TestContext.Current.CancellationToken);

        Assert.True(outcome.HasWinner);
        Assert.Same(fast, outcome.Winner);
        Assert.Equal(35, outcome.WinnerLatencyMs);
        Assert.All(outcome.Probes, probe => Assert.True(probe.Result.Ok));
    }

    [Fact]
    public async Task SelectFastest_OpenTcpButHandshakeFailureIsNeverSuccessful()
    {
        // The first entry represents a host whose TCP port is open but whose
        // configured protocol/credentials never reach the HTTPS endpoint.
        var tcpOpenButBlocked = Entry("tcp-open-handshake-blocked");
        var working = Entry("working", "192.0.2.2");

        var outcome = await BusinessServerAutoSelection.SelectFastestAsync(
            [tcpOpenButBlocked, working],
            (entry, _) => Task.FromResult(entry == tcpOpenButBlocked
                ? Failed(DeepVerifyFailurePhase.ProxiedHttp, "http failed")
                : Ok(700)),
            TestContext.Current.CancellationToken);

        Assert.True(outcome.HasWinner);
        Assert.Same(working, outcome.Winner);
        var rejected = outcome.Probes.Single(
            probe => ReferenceEquals(probe.Entry, tcpOpenButBlocked));
        Assert.False(rejected.Result.Ok);
        Assert.Equal(DeepVerifyFailurePhase.ProxiedHttp, rejected.Result.FailurePhase);
    }

    [Fact]
    public async Task SelectFastest_NoPositiveResultReportsNoWinner()
    {
        var outcome = await BusinessServerAutoSelection.SelectFastestAsync(
            [Entry("a"), Entry("b")],
            (_, _) => Task.FromResult(Failed(DeepVerifyFailurePhase.SocksBind, "bind")),
            TestContext.Current.CancellationToken);

        Assert.False(outcome.HasWinner);
        Assert.All(outcome.Probes, probe => Assert.False(probe.Result.Ok));
        var probeResult = BusinessServerAutoSelection.ToProbeResult(outcome.Probes[0].Result);
        Assert.Equal(ServerProbeStatus.Unreachable, probeResult.Status);
    }

    [Fact]
    public async Task SelectFastest_CallerCancellationInterruptsAndThrows()
    {
        using var cts = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = BusinessServerAutoSelection.SelectFastestAsync(
            [Entry("a"), Entry("b")],
            async (_, ct) =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return Ok(1);
            },
            cts.Token);

        await started.Task.WaitAsync(TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => run.WaitAsync(TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SelectFastest_BudgetReturnsPartialOutcomeWithoutWinner()
    {
        var outcome = await BusinessServerAutoSelection.SelectFastestAsync(
            [Entry("slow-to-probe")],
            async (_, ct) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
                return Ok(1);
            },
            TestContext.Current.CancellationToken,
            maxConcurrency: 1,
            budget: TimeSpan.FromMilliseconds(50));

        Assert.True(outcome.BudgetExpired);
        Assert.False(outcome.HasWinner);
        Assert.Single(outcome.Probes);
        Assert.Equal(DeepVerifyFailurePhase.Cancelled, outcome.Probes[0].Result.FailurePhase);
    }

    [Fact]
    public async Task SelectFastest_BoundsConcurrency()
    {
        var entries = Enumerable.Range(0, 8).Select(index => Entry($"n{index}", $"192.0.2.{index + 1}")).ToArray();
        var current = 0;
        var maximum = 0;
        var outcome = await BusinessServerAutoSelection.SelectFastestAsync(
            entries,
            async (_, _) =>
            {
                var active = Interlocked.Increment(ref current);
                int observed;
                do
                {
                    observed = Volatile.Read(ref maximum);
                    if (active <= observed) break;
                } while (Interlocked.CompareExchange(ref maximum, active, observed) != observed);
                await Task.Delay(20);
                Interlocked.Decrement(ref current);
                return Ok(100);
            },
            TestContext.Current.CancellationToken,
            maxConcurrency: 2);

        Assert.True(outcome.HasWinner);
        Assert.InRange(maximum, 1, 2);
    }

    [Fact]
    public void ChainConfig_DeclaresBootstrapResolverOnBothLinks()
    {
        var target = Entry("target", "target.example.com");
        target.DetourVia = "hop";
        var upstream = Entry("upstream", "upstream.example.com");
        upstream.OutboundId = "hop";

        var json = VlessDeepVerifier.BuildSingleOutboundConfig(
            target, new VerificationProxy(18080), chainUpstream: upstream);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var dnsTags = root.GetProperty("dns").GetProperty("servers").EnumerateArray()
            .Select(server => server.GetProperty("tag").GetString()).ToArray();
        Assert.Contains("dns-google", dnsTags);

        var outbounds = root.GetProperty("outbounds").EnumerateArray().ToArray();
        var targetOutbound = outbounds.Single(outbound =>
            outbound.GetProperty("tag").GetString() == "proxy");
        var upstreamOutbound = outbounds.Single(outbound =>
            outbound.GetProperty("tag").GetString() == "chain-entry");
        Assert.Equal("chain-entry", targetOutbound.GetProperty("detour").GetString());
        Assert.Equal("dns-google", targetOutbound.GetProperty("domain_resolver").GetString());
        Assert.Equal("dns-google", upstreamOutbound.GetProperty("domain_resolver").GetString());
        Assert.Equal("target-uuid", targetOutbound.GetProperty("uuid").GetString());
        Assert.Equal("upstream-uuid", upstreamOutbound.GetProperty("uuid").GetString());
        Assert.Equal("proxy", root.GetProperty("route").GetProperty("final").GetString());
    }

    [Fact]
    public void NativeDnstt_UsesTheConfiguredPairWhenRuntimeIsAvailable()
    {
        var previous = SingBoxFeatures.OverrideDnstt;
        SingBoxFeatures.OverrideDnstt = true;
        try
        {
            var entry = new VlessServerEntry
            {
                Name = "native-dnstt",
                Protocol = "dnstt",
                Server = "tunnel.example.com",
                Port = 443,
                Dnstt = new DnsttProfile
                {
                    Domain = "tunnel.example.com",
                    NoiseKey = new string('a', 64),
                    SshHostKey = "ssh-ed25519 fixture",
                    SshPrivateKey = "fixture-private-key",
                },
            };
            Assert.True(BusinessServerAutoSelection.IsVerifiableCandidate(entry));
            var json = VlessDeepVerifier.BuildSingleOutboundConfig(
                entry, new VerificationProxy(18081));
            using var doc = JsonDocument.Parse(json);
            var outbounds = doc.RootElement.GetProperty("outbounds").EnumerateArray().ToArray();
            Assert.Contains(outbounds, outbound => outbound.GetProperty("tag").GetString() == "dnstt-transport");
            Assert.Contains(outbounds, outbound => outbound.GetProperty("tag").GetString() == "proxy");
        }
        finally { SingBoxFeatures.OverrideDnstt = previous; }
    }

    [Fact]
    public async Task NativeDnstt_WithoutRuntimeStaysUnsupportedAndManual()
    {
        var previous = SingBoxFeatures.OverrideDnstt;
        SingBoxFeatures.OverrideDnstt = false;
        try
        {
            var entry = new VlessServerEntry
            {
                Name = "native-dnstt",
                Protocol = "dnstt",
                Server = "tunnel.example.com",
                Port = 443,
                Dnstt = new DnsttProfile { Domain = "tunnel.example.com", NoiseKey = "key" },
            };
            var verifier = new VlessDeepVerifier(Serilog.Log.Logger, @"C:\definitely-not-here\sing-box.exe");
            var result = await verifier.VerifyAsync(
                entry, measureBandwidth: false, TestContext.Current.CancellationToken);

            Assert.False(result.Ok);
            Assert.Equal(DeepVerifyFailurePhase.UnsupportedByVerifier, result.FailurePhase);
        }
        finally { SingBoxFeatures.OverrideDnstt = previous; }
    }
}
