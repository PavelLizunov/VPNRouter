#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

public class DeepVerifyProbeScopeTests
{
    [Fact]
    public void ProbeScope_TracksInFlight_AndDisposeIsIdempotent()
    {
        var baseline = DeepVerifyProbe.ProbesInFlightForTests;

        var a = DeepVerifyProbe.BeginProbeScope();
        var b = DeepVerifyProbe.BeginProbeScope();
        Assert.True(DeepVerifyProbe.ProbesInFlightForTests >= baseline + 2);
        Assert.True(DeepVerifyProbe.AnyProbeInFlight);

        a.Dispose();
        a.Dispose();
        var afterA = DeepVerifyProbe.ProbesInFlightForTests;
        Assert.True(afterA >= baseline + 1, $"underflow: {afterA} < {baseline + 1}");

        b.Dispose();
        Assert.True(DeepVerifyProbe.ProbesInFlightForTests >= baseline);
    }

    [Fact]
    public void NativeFailureReportsOnlyAStableCategory()
    {
        var proxy = new VerificationProxy(18080);
        proxy.ObserveChildOutput("FATAL: bind: address already in use (fixture.example)");
        Assert.Equal("sing-box: bind failed", proxy.StartupError);
    }

    [Fact]
    public void CrossProcessProbe_TrustedImageWithoutGlobalTunOwnership_IsNotATunnel()
    {
        Assert.False(RuntimeStatusDetector.IsTunnelPresent(
            liveTunnelChild: true,
            ownership: TunOwnershipStatus.Free));
    }

    [Fact]
    public void CrossProcessProbe_DifferentParent_CannotBecomeDurableV2Child()
    {
        const int tunnelOwnerPid = 2001;
        var executable = Path.Combine(Path.GetTempPath(), "vpnrouter", "bin", "sing-box-lx.exe");
        var verifier = new OwnedProcessIdentity(
            2002,
            3000,
            executable,
            ParentPid: 2999);
        var tunnelChild = verifier with { Pid = 2003, ParentPid = tunnelOwnerPid };

        Assert.False(ProcessOwnership.CanPublishChildIdentity(
            verifier,
            executable,
            notBeforeUtcTicks: 2500,
            expectedParentPid: tunnelOwnerPid,
            enforceParent: true));
        Assert.True(ProcessOwnership.CanPublishChildIdentity(
            tunnelChild,
            executable,
            notBeforeUtcTicks: 2500,
            expectedParentPid: tunnelOwnerPid,
            enforceParent: true));
    }

    [Fact]
    public void RetainedSemaphoreWithoutRecordedLiveChild_IsNotATunnel()
    {
        Assert.False(RuntimeStatusDetector.IsTunnelPresent(
            liveTunnelChild: false,
            ownership: TunOwnershipStatus.Owned));
    }

    private static string? LoadSource(params string[] relativeParts)
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        for (var i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(new[] { dir.FullName }.Concat(relativeParts).ToArray());
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
        }
        return null;
    }

    private static string StripLineComments(string src)
        => string.Join('\n',
            src.Split('\n').Select(l => l.Contains("//") ? l[..l.IndexOf("//", StringComparison.Ordinal)] : l));
}
