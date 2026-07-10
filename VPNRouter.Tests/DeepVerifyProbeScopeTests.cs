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
    public void Detector_GatesOnTunSemaphore_WhileProbeInFlight()
    {
        var src = LoadSource("VPNRouter.Core", "Services", "RuntimeStatusDetector.cs");
        if (src == null) return; // partial checkout
        var stripped = StripLineComments(src);
        Assert.Contains("DeepVerifyProbe.AnyProbeInFlight", stripped);
        Assert.Contains("TunOwnershipLock.IsOwnedByAnyone", stripped);
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
