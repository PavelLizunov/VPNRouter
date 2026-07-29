#nullable enable
using System;
using System.IO;
using System.Linq;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// r9 P2 (brat 2026-07-10): deep-verify probes spawn REAL sing-box processes
/// from our own bin dir, so the ownership-filtered runtime detector counted
/// them as "VPN running" and the 2s poll flipped the UI to a false
/// "Connected via service" for the duration of a batch. The fix is a
/// probe-in-flight scope (<see cref="DeepVerifyProbe.BeginProbeScope"/>) plus a
/// detector gate: while a probe is in flight, <c>IsVpnRunning</c> requires the
/// TUN ownership semaphore (which only a REAL tunnel holds).
/// </summary>
public class DeepVerifyProbeScopeTests
{
    [Fact]
    public void ProbeScope_TracksInFlight_AndDisposeIsIdempotent()
    {
        // Delta-based: other suites may run real verifier probes in parallel,
        // so assert relative movement of the counter, not global emptiness.
        var baseline = DeepVerifyProbe.ProbesInFlightForTests;

        var a = DeepVerifyProbe.BeginProbeScope();
        var b = DeepVerifyProbe.BeginProbeScope();
        Assert.True(DeepVerifyProbe.ProbesInFlightForTests >= baseline + 2);
        Assert.True(DeepVerifyProbe.AnyProbeInFlight);

        a.Dispose();
        a.Dispose();                                     // double-dispose must not underflow
        var afterA = DeepVerifyProbe.ProbesInFlightForTests;
        Assert.True(afterA >= baseline + 1, $"underflow: {afterA} < {baseline + 1}");

        b.Dispose();
        Assert.True(DeepVerifyProbe.ProbesInFlightForTests >= baseline);
    }

    // ── source pins (behaviour needs live processes + a named semaphore) ──

    [Fact]
    public void Detector_CombinesOwnedProcessWithGlobalTunSemaphore_OnEveryPoll()
    {
        var src = LoadSource("VPNRouter.Core", "Services", "RuntimeStatusDetector.cs");
        if (src == null) return; // partial checkout
        var stripped = StripLineComments(src);
        Assert.Contains("TunOwnershipLock.ProbeOwnership", stripped);
        Assert.Contains("IsTunnelPresent", stripped);
        Assert.DoesNotContain("DeepVerifyProbe.AnyProbeInFlight", stripped);
    }

    [Fact]
    public void CrossProcessProbe_TrustedImageWithoutGlobalTunOwnership_IsNotATunnel()
    {
        // A process-local DeepVerifyProbe counter cannot observe a verifier in
        // another VPNRouter process. The global semaphore is the cross-process
        // signal: an ownership-filtered image with a positively free lock is a
        // verifier/orphan, not a live tunnel.
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
        // Restart-disabled / restart-exhausted owners may retain the semaphore
        // after their child dies. Lock ownership alone must never report VPN up.
        Assert.False(RuntimeStatusDetector.IsTunnelPresent(
            liveTunnelChild: false,
            ownership: TunOwnershipStatus.Owned));
    }

    [Theory]
    [InlineData("VPNRouter.Core", "Services", "VlessDeepVerifier.cs")]
    [InlineData("VPNRouter.Core", "Services", "FreeConfigs", "FreeConfigDeepVerifier.cs")]
    public void Verifiers_OpenProbeScope(params string[] parts)
    {
        var src = LoadSource(parts);
        if (src == null) return;
        Assert.Contains("DeepVerifyProbe.BeginProbeScope()", StripLineComments(src));
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
