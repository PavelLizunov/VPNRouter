using Serilog;
using VPNRouter.Core.Services;
using VPNRouter.Core.Services.FreeConfigs;
using VPNRouter.Tests.Fakes;

namespace VPNRouter.Tests;

public sealed class StandaloneProbeRouteTests
{
    [Fact]
    public async Task SubscriptionAndFreeConfigChecksRequireTheConfiguredUpstream()
    {
        const string uri = "vless://641da42c-1abc-4941-87e9-ac8d24bef356@route.example.org:443?detour=required-upstream&security=tls";
        var server = ServerUriParser.Parse(uri);
        Assert.Equal("required-upstream", server.DetourVia);
        var ct = TestContext.Current.CancellationToken;
        var quick = await TcpTlsProbe.ProbeServerAsync(server, ct);
        Assert.Equal(ServerProbeStatus.SkippedNotApplicable, quick.Status);

        var runner = new FakeProcessRunner();
        var result = await new VlessDeepVerifier(Log.Logger, runner).VerifyAsync(server, false, ct);
        Assert.Equal(DeepVerifyFailurePhase.UnsupportedByVerifier, result.FailurePhase);
        Assert.Empty(runner.StartCalls);
        Assert.Throws<InvalidOperationException>(() =>
            VlessDeepVerifier.BuildSingleOutboundConfig(server, new VerificationProxy(18080)));

        using var scope = SingBoxRuntimePolicy.EnterScope(null);
        var free = new FreeConfigEntry { RawUri = uri, Status = FreeConfigStatus.Verified, LatencyMs = 20 };
        var tester = new FreeConfigTester();
        await tester.TcpPingOnlyAsync(free, ct);
        Assert.Equal(DeepVerifyConstants.DetourNote, free.LastError);
        await tester.TestOneAsync(free, ct);
        Assert.Equal(DeepVerifyConstants.DetourNote, free.LastError);
        await new FreeConfigDeepVerifier(Log.Logger).VerifyOneAsync(free, ct);
        Assert.NotEqual(FreeConfigStatus.Verified, free.Status);
        Assert.Equal(DeepVerifyConstants.DetourNote, free.LastError);
    }
}
